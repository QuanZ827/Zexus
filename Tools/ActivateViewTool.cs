using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: activate (open) any view in Revit — floor plans, sections,
    /// schedules, sheets, 3D views, legends, drafting views, etc.
    /// Uses UIDocument.RequestViewChange which requires no open transaction.
    /// </summary>
    public class ActivateViewTool : IAgentTool
    {
        public string Name => "ActivateView";

        public string Description =>
            "Open/activate a view in Revit by name or element ID. " +
            "Works for all view types: floor plans, ceiling plans, sections, elevations, 3D views, " +
            "schedules, sheets, legends, and drafting views. " +
            "For sheets, you can also match by sheet number (e.g. 'A101'). " +
            "Use after creating a schedule or when the user asks to open/switch to a view.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["view_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "View name to search for. Supports exact and fuzzy (contains) matching. " +
                            "For sheets, also matches by sheet number (e.g. 'A101')."
                    },
                    ["view_id"] = new PropertySchema
                    {
                        Type = "number",
                        Description = "Element ID of the view to activate. Takes priority over view_name if both provided."
                    },
                    ["view_type"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Optional filter to narrow search by view type.",
                        Enum = new List<string>
                        {
                            "FloorPlan", "CeilingPlan", "Section", "Elevation",
                            "ThreeD", "Schedule", "Sheet", "Legend", "DraftingView"
                        }
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                if (uiDoc == null)
                    return ToolResult.Fail("UIDocument not available.");

                // ── Parse parameters ──
                string viewName = null;
                long? viewId = null;
                string viewTypeFilter = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("view_name", out var vn) && vn != null)
                        viewName = vn.ToString().Trim();

                    if (parameters.TryGetValue("view_id", out var vi) && vi != null)
                    {
                        try { viewId = Convert.ToInt64(vi); }
                        catch { }
                    }

                    if (parameters.TryGetValue("view_type", out var vt) && vt != null)
                        viewTypeFilter = vt.ToString().Trim();
                }

                if (viewId == null && string.IsNullOrEmpty(viewName))
                    return ToolResult.Fail("Provide at least view_name or view_id.");

                // ── Strategy 1: Direct lookup by ID ──
                if (viewId.HasValue)
                {
                    var element = doc.GetElement(RevitCompat.CreateId(viewId.Value));
                    var view = element as View;

                    if (view == null)
                        return ToolResult.Fail($"Element {viewId.Value} is not a view or does not exist.");

                    if (view.IsTemplate)
                        return ToolResult.Fail($"'{view.Name}' is a View Template and cannot be activated.");

                    return ActivateAndReturn(uiDoc, view);
                }

                // ── Strategy 2: Search by name ──
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .ToList();

                // Apply view_type filter if provided
                if (!string.IsNullOrEmpty(viewTypeFilter))
                {
                    allViews = FilterByType(allViews, viewTypeFilter);
                }

                // Step A: Exact name match (case-insensitive)
                var exactMatches = allViews
                    .Where(v => v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Step B: For sheets, also match by SheetNumber
                if (exactMatches.Count == 0)
                {
                    var sheetMatches = allViews
                        .OfType<ViewSheet>()
                        .Where(s => s.SheetNumber.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                        .Cast<View>()
                        .ToList();

                    if (sheetMatches.Count > 0)
                        exactMatches = sheetMatches;
                }

                // Step C: Fuzzy (Contains) match
                if (exactMatches.Count == 0)
                {
                    exactMatches = allViews
                        .Where(v =>
                        {
                            if (v.Name.IndexOf(viewName, StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;

                            // Also check sheet number for partial match
                            if (v is ViewSheet sheet &&
                                sheet.SheetNumber.IndexOf(viewName, StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;

                            return false;
                        })
                        .ToList();
                }

                // ── Evaluate results ──
                if (exactMatches.Count == 0)
                {
                    return ToolResult.Fail($"No view found matching '{viewName}'." +
                        (string.IsNullOrEmpty(viewTypeFilter) ? "" : $" (filtered by type: {viewTypeFilter})"));
                }

                if (exactMatches.Count == 1)
                {
                    return ActivateAndReturn(uiDoc, exactMatches[0]);
                }

                // Multiple matches — return candidate list for LLM to choose
                var candidates = exactMatches.Take(10).Select(v => new Dictionary<string, object>
                {
                    ["id"] = RevitCompat.GetIdValue(v.Id),
                    ["name"] = v is ViewSheet vs ? $"{vs.SheetNumber} - {vs.Name}" : v.Name,
                    ["type"] = GetViewTypeName(v)
                }).ToList();

                return ToolResult.Ok(
                    $"Found {exactMatches.Count} matching views. Use view_id to activate a specific one:",
                    new Dictionary<string, object>
                    {
                        ["match_count"] = exactMatches.Count,
                        ["candidates"] = candidates
                    });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error activating view: {ex.Message}");
            }
        }

        /// <summary>
        /// Activate a view and return a success result.
        /// </summary>
        private ToolResult ActivateAndReturn(UIDocument uiDoc, View view)
        {
            uiDoc.RequestViewChange(view);

            var typeName = GetViewTypeName(view);
            var displayName = view is ViewSheet vs ? $"{vs.SheetNumber} - {vs.Name}" : view.Name;

            return ToolResult.Ok(
                $"Activated view: {displayName} ({typeName})",
                new Dictionary<string, object>
                {
                    ["view_id"] = RevitCompat.GetIdValue(view.Id),
                    ["view_name"] = displayName,
                    ["view_type"] = typeName
                });
        }

        /// <summary>
        /// Filter views by type string from the LLM.
        /// </summary>
        private List<View> FilterByType(List<View> views, string typeFilter)
        {
            switch (typeFilter.ToLower())
            {
                case "floorplan":
                    return views.Where(v => v.ViewType == ViewType.FloorPlan).ToList();
                case "ceilingplan":
                    return views.Where(v => v.ViewType == ViewType.CeilingPlan).ToList();
                case "section":
                    return views.Where(v => v.ViewType == ViewType.Section).ToList();
                case "elevation":
                    return views.Where(v => v.ViewType == ViewType.Elevation).ToList();
                case "threed":
                    return views.Where(v => v.ViewType == ViewType.ThreeD).ToList();
                case "schedule":
                    return views.Where(v => v is ViewSchedule).ToList();
                case "sheet":
                    return views.Where(v => v is ViewSheet).ToList();
                case "legend":
                    return views.Where(v => v.ViewType == ViewType.Legend).ToList();
                case "draftingview":
                    return views.Where(v => v.ViewType == ViewType.DraftingView).ToList();
                default:
                    return views; // Unknown filter — return all
            }
        }

        /// <summary>
        /// Human-readable view type name.
        /// </summary>
        private string GetViewTypeName(View view)
        {
            if (view is ViewSheet) return "Sheet";
            if (view is ViewSchedule) return "Schedule";

            switch (view.ViewType)
            {
                case ViewType.FloorPlan: return "Floor Plan";
                case ViewType.CeilingPlan: return "Ceiling Plan";
                case ViewType.Section: return "Section";
                case ViewType.Elevation: return "Elevation";
                case ViewType.ThreeD: return "3D View";
                case ViewType.Legend: return "Legend";
                case ViewType.DraftingView: return "Drafting View";
                case ViewType.AreaPlan: return "Area Plan";
                case ViewType.Detail: return "Detail View";
                case ViewType.EngineeringPlan: return "Engineering Plan";
                default: return view.ViewType.ToString();
            }
        }
    }
}

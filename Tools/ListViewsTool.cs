using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: lists views only (floor plans, sections, elevations, 3D, schedules).
    /// Does NOT include sheets — use ListSheets for that.
    /// </summary>
    public class ListViewsTool : IAgentTool
    {
        public string Name => "ListViews";

        public string Description =>
            "List views in the model (floor plans, sections, elevations, 3D views, schedules). " +
            "Does NOT include sheets — use ListSheets for sheets. " +
            "Optional filters by name or view type.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["name_filter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter by view name (partial match, case-insensitive). Example: 'Level 1'"
                    },
                    ["view_type"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter by view type. Leave empty for all types.",
                        Enum = new List<string>
                        {
                            "FloorPlan", "CeilingPlan", "Section", "Elevation",
                            "ThreeD", "Schedule", "Drafting", "Legend", "Detail"
                        }
                    },
                    ["printable_only"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Only return views that can be printed/exported. Default: false"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string nameFilter = null;
                string viewTypeFilter = null;
                bool printableOnly = false;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("name_filter", out var nf))
                        nameFilter = nf?.ToString();
                    if (parameters.TryGetValue("view_type", out var vt))
                        viewTypeFilter = vt?.ToString();
                    if (parameters.TryGetValue("printable_only", out var po))
                    {
                        if (po is bool b) printableOnly = b;
                        else bool.TryParse(po?.ToString(), out printableOnly);
                    }
                }

                // ── Build a single deferred LINQ chain — one materialization at the end ──
                IEnumerable<View> query = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && !(v is ViewSheet));

                if (printableOnly)
                    query = query.Where(v => v.CanBePrinted);

                if (!string.IsNullOrEmpty(nameFilter))
                    query = query.Where(v =>
                        v.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!string.IsNullOrEmpty(viewTypeFilter))
                {
                    if (Enum.TryParse<ViewType>(viewTypeFilter, true, out var vt))
                        query = query.Where(v => v.ViewType == vt);
                }

                var views = query.ToList();

                // Build result
                var viewList = new List<Dictionary<string, object>>();

                foreach (var v in views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).Take(500))
                {
                    var info = new Dictionary<string, object>
                    {
                        ["id"] = RevitCompat.GetIdValue(v.Id),
                        ["name"] = v.Name,
                        ["view_type"] = v.ViewType.ToString()
                    };

                    // Level (if available)
                    if (v.GenLevel != null)
                        info["level"] = v.GenLevel.Name;

                    // Schedule-specific: row count
                    if (v is ViewSchedule vs)
                    {
                        try
                        {
                            var tableData = vs.GetTableData();
                            var body = tableData.GetSectionData(SectionType.Body);
                            info["row_count"] = body.NumberOfRows;
                        }
                        catch { }
                    }

                    viewList.Add(info);
                }

                // Group by type for summary
                var byType = viewList
                    .GroupBy(v => v["view_type"]?.ToString() ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count());

                var resultData = new Dictionary<string, object>
                {
                    ["total_views"] = viewList.Count,
                    ["views"] = viewList,
                    ["by_type"] = byType
                };

                var typeSummary = string.Join(", ", byType.Select(kv => $"{kv.Key}: {kv.Value}"));
                return ToolResult.Ok($"Found {viewList.Count} view(s) ({typeSummary})", resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error listing views: {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: select (highlight) elements and optionally zoom to them.
    /// Blue highlight via native Revit selection + ShowElements for zoom.
    /// </summary>
    public class SelectElementsTool : IAgentTool
    {
        public string Name => "SelectElements";

        public string Description =>
            "Select and highlight elements in Revit by their element IDs. " +
            "Blue highlight via native selection. Optionally zoom to show them. " +
            "Use after SearchElements to let the user see the results in the model.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["element_ids"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "Element IDs to select (from SearchElements results)."
                    },
                    ["zoom_to_fit"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Zoom the active view to show selected elements. Default: true"
                    },
                    ["clear_previous"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Clear previous selection first. Default: true"
                    }
                },
                Required = new List<string> { "element_ids" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                var elementIds = new List<ElementId>();
                bool zoomToFit = true;
                bool clearPrevious = true;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("element_ids", out var ids) && ids != null)
                    {
                        var idList = ids as System.Collections.IEnumerable;
                        if (idList != null)
                        {
                            foreach (var id in idList)
                            {
                                try { elementIds.Add(RevitCompat.CreateId(Convert.ToInt64(id))); }
                                catch { }
                            }
                        }
                    }

                    if (parameters.TryGetValue("zoom_to_fit", out var ztf))
                        zoomToFit = Convert.ToBoolean(ztf);
                    if (parameters.TryGetValue("clear_previous", out var cp))
                        clearPrevious = Convert.ToBoolean(cp);
                }

                if (elementIds.Count == 0)
                    return ToolResult.Fail("No element IDs provided.");

                if (uiDoc == null)
                    return ToolResult.Fail("UIDocument not available.");

                // Validate IDs
                var validIds = new List<ElementId>();
                var invalidIds = new List<long>();

                foreach (var id in elementIds)
                {
                    if (doc.GetElement(id) != null)
                        validIds.Add(id);
                    else
                        invalidIds.Add(RevitCompat.GetIdValue(id));
                }

                if (validIds.Count == 0)
                    return ToolResult.Fail("None of the provided element IDs are valid.");

                // ── Build selection set ──
                ICollection<ElementId> newSelection;
                if (clearPrevious)
                {
                    newSelection = validIds;
                }
                else
                {
                    try
                    {
                        var current = uiDoc.Selection.GetElementIds().ToList();
                        current.AddRange(validIds);
                        newSelection = current;
                    }
                    catch { newSelection = validIds; }
                }

                // ── Apply selection (blue highlight) ──
                try
                {
                    uiDoc.Selection.SetElementIds(newSelection);
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail($"Error setting selection: {ex.Message}");
                }

                // ── Zoom to fit ──
                if (zoomToFit && validIds.Count > 0)
                {
                    try
                    {
                        uiDoc.ShowElements(validIds);
                    }
                    catch { }
                }

                // ── Build result ──
                var result = new Dictionary<string, object>
                {
                    ["selected_count"] = validIds.Count,
                    ["selected_ids"] = validIds.Select(id => RevitCompat.GetIdValue(id)).ToList()
                };

                if (invalidIds.Count > 0)
                {
                    result["invalid_ids"] = invalidIds;
                    result["warning"] = $"{invalidIds.Count} element ID(s) were invalid";
                }

                string msg = $"Selected {validIds.Count} element(s) in Revit";
                if (zoomToFit) msg += ", zoomed to fit";

                return invalidIds.Count > 0
                    ? ToolResult.WithWarning(msg, result)
                    : ToolResult.Ok(msg, result);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error selecting elements: {ex.Message}");
            }
        }
    }
}

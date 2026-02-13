using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: read the user's current Revit selection.
    /// Returns element IDs + basic info (category, name, type) for each selected element.
    /// This is the entry point when the user says "these elements" / "what I selected" / "the current selection".
    /// </summary>
    public class GetSelectionTool : IAgentTool
    {
        public string Name => "GetSelection";

        public string Description =>
            "Read the user's current Revit selection. Returns element IDs, category, name, " +
            "and type for each selected element. Use when the user says 'selected elements', " +
            "'these elements', 'what I picked', or refers to their current selection.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>(),
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                if (uiDoc == null)
                    return ToolResult.Fail("UIDocument not available.");

                var selectedIds = uiDoc.Selection.GetElementIds();
                if (selectedIds == null || selectedIds.Count == 0)
                    return ToolResult.Ok("No elements currently selected.",
                        new Dictionary<string, object> { ["selected_count"] = 0 });

                var elements = new List<Dictionary<string, object>>();

                foreach (var id in selectedIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;

                    var info = new Dictionary<string, object>
                    {
                        ["id"] = RevitCompat.GetIdValue(id),
                        ["category"] = elem.Category?.Name ?? "",
                        ["name"] = elem.Name ?? ""
                    };

                    // Type name
                    if (elem is FamilyInstance fi)
                    {
                        info["family"] = fi.Symbol?.Family?.Name ?? "";
                        info["type"] = fi.Symbol?.Name ?? "";
                    }
                    else
                    {
                        var typeId = elem.GetTypeId();
                        if (typeId != null && typeId != ElementId.InvalidElementId)
                            info["type"] = doc.GetElement(typeId)?.Name ?? "";
                    }

                    // Level
                    try
                    {
                        var lvlParam = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                        if (lvlParam != null && lvlParam.HasValue)
                        {
                            var lvl = doc.GetElement(lvlParam.AsElementId()) as Level;
                            if (lvl != null) info["level"] = lvl.Name;
                        }
                    }
                    catch { }

                    elements.Add(info);
                }

                // Group by category
                var byCategory = elements
                    .GroupBy(e => e["category"]?.ToString() ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count());

                var resultData = new Dictionary<string, object>
                {
                    ["selected_count"] = elements.Count,
                    ["element_ids"] = elements.Select(e => e["id"]).ToList(),
                    ["elements"] = elements,
                    ["by_category"] = byCategory
                };

                var catSummary = string.Join(", ", byCategory.Select(kv => $"{kv.Key}: {kv.Value}"));
                return ToolResult.Ok($"{elements.Count} element(s) selected ({catSummary})", resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error reading selection: {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to get all views placed on a specific sheet.
    /// Returns view names, types, and positions for understanding sheet composition.
    /// </summary>
    public class GetViewsOnSheetTool : IAgentTool
    {
        public string Name => "GetViewsOnSheet";
        
        public string Description => 
            "Get all views placed on a specific sheet. " +
            "Returns view names, view types, scale, and viewport positions. " +
            "Use to understand sheet composition, find views by name, or analyze sheet layout.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["sheet_id"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Sheet element ID. Use this OR sheet_number."
                    },
                    ["sheet_number"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Sheet number (e.g., 'E-101'). Use this OR sheet_id."
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                int sheetId = 0;
                string sheetNumber = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("sheet_id", out var idObj))
                    {
                        if (idObj is int i) sheetId = i;
                        else if (idObj is long l) sheetId = (int)l;
                        else int.TryParse(idObj?.ToString(), out sheetId);
                    }
                    
                    if (parameters.TryGetValue("sheet_number", out var numObj))
                        sheetNumber = numObj?.ToString();
                }

                // Find the sheet
                ViewSheet sheet = null;

                if (sheetId > 0)
                {
                    var elem = doc.GetElement(RevitCompat.CreateId(sheetId));
                    sheet = elem as ViewSheet;
                }
                else if (!string.IsNullOrEmpty(sheetNumber))
                {
                    sheet = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .FirstOrDefault(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
                }

                if (sheet == null)
                {
                    return ToolResult.Fail(
                        "Sheet not found. Please provide either:\n" +
                        "1. A valid sheet_id\n" +
                        "2. A valid sheet_number (e.g., 'E-101')");
                }

                // Get all views placed on this sheet
                var viewIds = sheet.GetAllPlacedViews();
                var viewInfoList = new List<Dictionary<string, object>>();

                foreach (var viewId in viewIds)
                {
                    var view = doc.GetElement(viewId) as View;
                    if (view == null) continue;

                    // Get viewport info
                    var viewport = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .FirstOrDefault(vp => vp.ViewId == viewId);

                    var viewInfo = new Dictionary<string, object>
                    {
                        ["view_id"] = RevitCompat.GetIdValue(view.Id),
                        ["view_name"] = view.Name,
                        ["view_type"] = view.ViewType.ToString(),
                        ["scale"] = view.Scale
                    };

                    if (viewport != null)
                    {
                        viewInfo["viewport_id"] = RevitCompat.GetIdValue(viewport.Id);
                        
                        var center = viewport.GetBoxCenter();
                        viewInfo["position_x"] = Math.Round(center.X, 2);
                        viewInfo["position_y"] = Math.Round(center.Y, 2);
                        
                        // Get viewport type name
                        var vpType = doc.GetElement(viewport.GetTypeId());
                        viewInfo["viewport_type"] = vpType?.Name ?? "";
                    }

                    viewInfoList.Add(viewInfo);
                }

                // Sort by position (left to right, top to bottom)
                viewInfoList = viewInfoList
                    .OrderByDescending(v => v.ContainsKey("position_y") ? Convert.ToDouble(v["position_y"]) : 0)
                    .ThenBy(v => v.ContainsKey("position_x") ? Convert.ToDouble(v["position_x"]) : 0)
                    .ToList();

                // Get title block info
                var titleBlockInfo = GetTitleBlockOnSheet(doc, sheet);

                var resultData = new Dictionary<string, object>
                {
                    ["sheet_id"] = RevitCompat.GetIdValue(sheet.Id),
                    ["sheet_number"] = sheet.SheetNumber,
                    ["sheet_name"] = sheet.Name,
                    ["view_count"] = viewInfoList.Count,
                    ["views"] = viewInfoList,
                    ["title_block"] = titleBlockInfo
                };

                // Build summary
                var viewTypeCounts = viewInfoList
                    .GroupBy(v => v["view_type"]?.ToString() ?? "Unknown")
                    .Select(g => $"{g.Key}: {g.Count()}")
                    .ToList();

                string summary = $"Sheet {sheet.SheetNumber} '{sheet.Name}' has {viewInfoList.Count} view(s)";
                if (viewTypeCounts.Count > 0)
                {
                    summary += $" ({string.Join(", ", viewTypeCounts)})";
                }

                return ToolResult.Ok(summary, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting views on sheet: {ex.Message}");
            }
        }

        private Dictionary<string, object> GetTitleBlockOnSheet(Document doc, ViewSheet sheet)
        {
            var info = new Dictionary<string, object>
            {
                ["id"] = 0,
                ["family_name"] = "",
                ["type_name"] = ""
            };

            try
            {
                var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .ToList();

                if (titleBlocks.Count > 0)
                {
                    var tb = titleBlocks[0] as FamilyInstance;
                    if (tb != null)
                    {
                        info["id"] = RevitCompat.GetIdValue(tb.Id);
                        info["family_name"] = tb.Symbol?.Family?.Name ?? "";
                        info["type_name"] = tb.Symbol?.Name ?? "";
                    }
                }
            }
            catch { }

            return info;
        }
    }
}

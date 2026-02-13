using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: lists sheets only. Fast, lightweight, no views or paper sizes.
    /// </summary>
    public class ListSheetsTool : IAgentTool
    {
        public string Name => "ListSheets";

        public string Description =>
            "List all sheets in the model. Returns sheet number, name, title block info, " +
            "placed view count, and current revision for each sheet. " +
            "Optional filters by name or number prefix.";

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
                        Description = "Filter by sheet name (partial match, case-insensitive). Example: 'LEVEL 1' or 'ELECTRICAL'"
                    },
                    ["number_filter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter by sheet number prefix (partial match). Example: 'E-' for electrical sheets"
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
                string numberFilter = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("name_filter", out var nf))
                        nameFilter = nf?.ToString();
                    if (parameters.TryGetValue("number_filter", out var numf))
                        numberFilter = numf?.ToString();
                }

                // Collect all sheets
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .ToList();

                // Apply filters
                if (!string.IsNullOrEmpty(nameFilter))
                    sheets = sheets.Where(s =>
                        s.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (!string.IsNullOrEmpty(numberFilter))
                    sheets = sheets.Where(s =>
                        s.SheetNumber.IndexOf(numberFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                // Build result
                var sheetList = new List<Dictionary<string, object>>();

                foreach (var sheet in sheets.OrderBy(s => s.SheetNumber))
                {
                    int viewCount = sheet.GetAllPlacedViews().Count;

                    // Title block
                    string tbFamily = "", tbType = "";
                    try
                    {
                        var tbs = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .ToList();
                        if (tbs.Count > 0)
                        {
                            var fi = tbs[0] as FamilyInstance;
                            if (fi != null)
                            {
                                tbFamily = fi.Symbol?.Family?.Name ?? "";
                                tbType = fi.Symbol?.Name ?? "";
                            }
                        }
                    }
                    catch { }

                    // Revision
                    string revision = "";
                    var revParam = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION);
                    if (revParam != null) revision = revParam.AsString() ?? "";

                    sheetList.Add(new Dictionary<string, object>
                    {
                        ["id"] = RevitCompat.GetIdValue(sheet.Id),
                        ["sheet_number"] = sheet.SheetNumber,
                        ["sheet_name"] = sheet.Name,
                        ["view_count"] = viewCount,
                        ["current_revision"] = revision,
                        ["title_block_family"] = tbFamily,
                        ["title_block_type"] = tbType
                    });
                }

                var resultData = new Dictionary<string, object>
                {
                    ["total_sheets"] = sheetList.Count,
                    ["sheets"] = sheetList
                };

                return ToolResult.Ok($"Found {sheetList.Count} sheet(s)", resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error listing sheets: {ex.Message}");
            }
        }
    }
}

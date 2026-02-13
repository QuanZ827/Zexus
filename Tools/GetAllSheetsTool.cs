using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to get all sheets in the model with their basic information.
    /// Returns sheet number, name, and title block info for AI to analyze.
    /// </summary>
    public class GetAllSheetsTool : IAgentTool
    {
        public string Name => "GetAllSheets";
        
        public string Description => 
            "Get all sheets in the model with their sheet number, sheet name, title block info, and view count. " +
            "Use this to understand project documentation structure, find sheets by name pattern, or analyze sheet organization.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["include_placeholders"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include placeholder sheets (sheets without views). Default: false"
                    },
                    ["name_filter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter sheets by name (partial match, case-insensitive). Example: 'LEVEL 1' or 'ELECTRICAL'"
                    },
                    ["number_filter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter sheets by sheet number (partial match). Example: 'E-' for electrical sheets"
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
                bool includePlaceholders = false;
                string nameFilter = null;
                string numberFilter = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("include_placeholders", out var phObj))
                    {
                        if (phObj is bool b) includePlaceholders = b;
                        else bool.TryParse(phObj?.ToString(), out includePlaceholders);
                    }
                    
                    if (parameters.TryGetValue("name_filter", out var nameObj))
                        nameFilter = nameObj?.ToString();
                    
                    if (parameters.TryGetValue("number_filter", out var numObj))
                        numberFilter = numObj?.ToString();
                }

                // Collect all sheets
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .ToList();

                // Apply filters
                if (!string.IsNullOrEmpty(nameFilter))
                {
                    sheets = sheets.Where(s => 
                        s.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                if (!string.IsNullOrEmpty(numberFilter))
                {
                    sheets = sheets.Where(s => 
                        s.SheetNumber.IndexOf(numberFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                // Build sheet info list
                var sheetInfoList = new List<Dictionary<string, object>>();

                foreach (var sheet in sheets.OrderBy(s => s.SheetNumber))
                {
                    // Get views on sheet
                    var viewIds = sheet.GetAllPlacedViews();
                    int viewCount = viewIds.Count;

                    // Filter out placeholders if requested
                    if (!includePlaceholders && viewCount == 0)
                        continue;

                    // Get title block info
                    var titleBlockInfo = GetTitleBlockInfo(doc, sheet);

                    // Get revision info
                    string currentRevision = "";
                    var revParam = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION);
                    if (revParam != null)
                        currentRevision = revParam.AsString() ?? "";

                    var sheetInfo = new Dictionary<string, object>
                    {
                        ["id"] = RevitCompat.GetIdValue(sheet.Id),
                        ["sheet_number"] = sheet.SheetNumber,
                        ["sheet_name"] = sheet.Name,
                        ["view_count"] = viewCount,
                        ["current_revision"] = currentRevision,
                        ["title_block_id"] = titleBlockInfo.TitleBlockId,
                        ["title_block_family"] = titleBlockInfo.FamilyName,
                        ["title_block_type"] = titleBlockInfo.TypeName
                    };

                    sheetInfoList.Add(sheetInfo);
                }

                // Build category summary (by sheet number prefix)
                var prefixGroups = sheetInfoList
                    .GroupBy(s => GetSheetPrefix(s["sheet_number"]?.ToString() ?? ""))
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .ToDictionary(g => g.Key, g => g.Count());

                var resultData = new Dictionary<string, object>
                {
                    ["total_count"] = sheetInfoList.Count,
                    ["sheets"] = sheetInfoList,
                    ["by_prefix"] = prefixGroups
                };

                // Build summary
                string summary = $"Found {sheetInfoList.Count} sheet(s)";
                if (prefixGroups.Count > 0)
                {
                    var prefixSummary = string.Join(", ", 
                        prefixGroups.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                    summary += $" ({prefixSummary})";
                }

                return ToolResult.Ok(summary, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting sheets: {ex.Message}");
            }
        }

        private TitleBlockInfo GetTitleBlockInfo(Document doc, ViewSheet sheet)
        {
            var info = new TitleBlockInfo();

            try
            {
                // Find title block on this sheet
                var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .ToList();

                if (titleBlocks.Count > 0)
                {
                    var tb = titleBlocks[0] as FamilyInstance;
                    if (tb != null)
                    {
                        info.TitleBlockId = RevitCompat.GetIdValue(tb.Id);
                        info.FamilyName = tb.Symbol?.Family?.Name ?? "";
                        info.TypeName = tb.Symbol?.Name ?? "";
                    }
                }
            }
            catch { }

            return info;
        }

        private string GetSheetPrefix(string sheetNumber)
        {
            // Extract prefix like "E-", "M-", "A-" from sheet numbers
            if (string.IsNullOrEmpty(sheetNumber))
                return "";

            int dashIndex = sheetNumber.IndexOf('-');
            if (dashIndex > 0 && dashIndex < 4)
            {
                return sheetNumber.Substring(0, dashIndex + 1);
            }

            // Try first letter(s) before numbers
            int i = 0;
            while (i < sheetNumber.Length && !char.IsDigit(sheetNumber[i]))
            {
                i++;
            }
            
            if (i > 0 && i < sheetNumber.Length)
                return sheetNumber.Substring(0, i);

            return "";
        }

        private class TitleBlockInfo
        {
            public long TitleBlockId { get; set; } = 0;
            public string FamilyName { get; set; } = "";
            public string TypeName { get; set; } = "";
        }
    }
}

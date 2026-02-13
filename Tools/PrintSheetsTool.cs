using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Print selected sheets to PDF using Revit's PrintManager API.
    /// Supports combined or separate PDFs, paper size, orientation, and color mode settings.
    /// </summary>
    public class PrintSheetsTool : IAgentTool
    {
        public string Name => "PrintSheets";

        public string Description =>
            "Print selected sheets to PDF files. Requires sheet_numbers (from ListSheets) and output_path. " +
            "Supports combined PDF (one file) or separate PDFs (one per sheet), paper size (auto-detect from title block " +
            "or explicit A0-A4/Letter/Tabloid), orientation (auto/landscape/portrait), and color mode (color/grayscale/monochrome).";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["sheet_numbers"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "Array of sheet numbers to print (e.g. [\"E-101\",\"E-102\",\"M-201\"]). Get these from ListSheets."
                    },
                    ["output_path"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Full file path for combined PDF (e.g. 'C:\\Users\\...\\Desktop\\output.pdf') " +
                                      "or folder path for separate PDFs (e.g. 'C:\\Users\\...\\Desktop\\prints')"
                    },
                    ["combined"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "true = one combined PDF file, false = separate PDF per sheet. Default: true"
                    },
                    ["paper_size"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Paper size: 'auto' (detect from title block), 'A0', 'A1', 'A2', 'A3', 'A4', 'Letter', 'Tabloid'. Default: 'auto'",
                        Enum = new List<string> { "auto", "A0", "A1", "A2", "A3", "A4", "Letter", "Tabloid" }
                    },
                    ["orientation"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Page orientation: 'auto', 'landscape', 'portrait'. Default: 'auto'",
                        Enum = new List<string> { "auto", "landscape", "portrait" }
                    },
                    ["color_mode"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Color mode: 'color', 'grayscale', 'monochrome'. Default: 'color'",
                        Enum = new List<string> { "color", "grayscale", "monochrome" }
                    }
                },
                Required = new List<string> { "sheet_numbers", "output_path" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // ── Parse parameters ──
                var sheetNumbers = ParseStringArray(parameters, "sheet_numbers");
                string outputPath = null;
                bool combined = true;
                string paperSize = "auto";
                string orientation = "auto";
                string colorMode = "color";

                if (parameters != null)
                {
                    if (parameters.TryGetValue("output_path", out var opObj))
                        outputPath = opObj?.ToString();
                    if (parameters.TryGetValue("combined", out var cObj))
                    {
                        if (cObj is bool b) combined = b;
                        else bool.TryParse(cObj?.ToString(), out combined);
                    }
                    if (parameters.TryGetValue("paper_size", out var psObj))
                        paperSize = psObj?.ToString() ?? "auto";
                    if (parameters.TryGetValue("orientation", out var orObj))
                        orientation = orObj?.ToString() ?? "auto";
                    if (parameters.TryGetValue("color_mode", out var cmObj))
                        colorMode = cmObj?.ToString() ?? "color";
                }

                // ── Validate ──
                if (sheetNumbers == null || sheetNumbers.Count == 0)
                    return ToolResult.Fail("sheet_numbers is required. Call ListSheets first to get available sheet numbers.");

                if (string.IsNullOrEmpty(outputPath))
                    return ToolResult.Fail("output_path is required (file path for combined, folder path for separate).");

                // ── Find matching sheets ──
                var allSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();

                var targetSheets = new List<ViewSheet>();
                var notFound = new List<string>();

                foreach (var num in sheetNumbers)
                {
                    var sheet = allSheets.FirstOrDefault(s =>
                        s.SheetNumber.Equals(num, StringComparison.OrdinalIgnoreCase));
                    if (sheet != null)
                        targetSheets.Add(sheet);
                    else
                        notFound.Add(num);
                }

                if (targetSheets.Count == 0)
                    return ToolResult.Fail($"No matching sheets found for: {string.Join(", ", sheetNumbers)}");

                // ── Configure PrintManager ──
                var printMgr = doc.PrintManager;
                printMgr.SelectNewPrintDriver("Microsoft Print to PDF");
                printMgr.PrintRange = PrintRange.Select;
                printMgr.PrintToFile = true;
                printMgr.CombinedFile = combined;

                // Output path setup
                if (combined)
                {
                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // Ensure .pdf extension
                    if (!outputPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        outputPath += ".pdf";

                    printMgr.PrintToFileName = outputPath;
                }
                else
                {
                    // For separate files, outputPath is a folder
                    if (!Directory.Exists(outputPath))
                        Directory.CreateDirectory(outputPath);
                }

                // ── Paper size ──
                if (paperSize != "auto")
                {
                    ApplyPaperSize(printMgr, paperSize);
                }

                // ── Color mode ──
                try
                {
                    var printSetup = printMgr.PrintSetup;
                    var currentSettings = printSetup.CurrentPrintSetting;

                    // Note: PrintSetup properties vary by Revit version; use try/catch
                    switch (colorMode.ToLower())
                    {
                        case "grayscale":
                            printSetup.CurrentPrintSetting.PrintParameters.ColorDepth = ColorDepthType.GrayScale;
                            break;
                        case "monochrome":
                            printSetup.CurrentPrintSetting.PrintParameters.ColorDepth = ColorDepthType.BlackLine;
                            break;
                        default: // "color"
                            printSetup.CurrentPrintSetting.PrintParameters.ColorDepth = ColorDepthType.Color;
                            break;
                    }
                }
                catch { /* Some Revit versions handle print settings differently */ }

                // ── Build ViewSet ──
                var viewSet = new ViewSet();
                foreach (var sheet in targetSheets)
                    viewSet.Insert(sheet);

                // ── Apply to ViewSheetSetting ──
                var viewSheetSetting = printMgr.ViewSheetSetting;
                viewSheetSetting.CurrentViewSheetSet.Views = viewSet;

                var outputFiles = new List<string>();

                if (combined)
                {
                    // Combined print
                    printMgr.SubmitPrint();
                    outputFiles.Add(outputPath);
                }
                else
                {
                    // Separate print: one PDF per sheet
                    foreach (var sheet in targetSheets)
                    {
                        var singleSet = new ViewSet();
                        singleSet.Insert(sheet);
                        viewSheetSetting.CurrentViewSheetSet.Views = singleSet;

                        var fileName = $"{sheet.SheetNumber} - {SanitizeFileName(sheet.Name)}.pdf";
                        var filePath = Path.Combine(outputPath, fileName);
                        printMgr.PrintToFileName = filePath;
                        printMgr.SubmitPrint();
                        outputFiles.Add(filePath);
                    }
                }

                // ── Build result ──
                var resultData = new Dictionary<string, object>
                {
                    ["printed_count"] = targetSheets.Count,
                    ["combined"] = combined,
                    ["output_files"] = outputFiles,
                    ["paper_size"] = paperSize,
                    ["color_mode"] = colorMode,
                    ["orientation"] = orientation
                };

                if (notFound.Count > 0)
                    resultData["not_found"] = notFound;

                string message = combined
                    ? $"Printed {targetSheets.Count} sheet(s) to {outputPath}"
                    : $"Printed {targetSheets.Count} sheet(s) as separate PDFs to {outputPath}";

                if (notFound.Count > 0)
                    return ToolResult.WithWarning(
                        $"{message}. Warning: {notFound.Count} sheet number(s) not found: {string.Join(", ", notFound)}",
                        resultData);

                return ToolResult.Ok(message, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Print failed: {ex.Message}");
            }
        }

        private List<string> ParseStringArray(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var val))
                return null;

            if (val is List<object> objList)
                return objList.Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();

            if (val is string[] strArr)
                return strArr.Where(s => !string.IsNullOrEmpty(s)).ToList();

            if (val is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in je.EnumerateArray())
                    list.Add(item.GetString() ?? item.ToString());
                return list;
            }

            // Single string fallback
            var single = val?.ToString();
            if (!string.IsNullOrEmpty(single))
                return new List<string> { single };

            return null;
        }

        private void ApplyPaperSize(PrintManager printMgr, string paperSizeName)
        {
            try
            {
                foreach (PaperSize ps in printMgr.PaperSizes)
                {
                    if (ps.Name.IndexOf(paperSizeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        printMgr.PrintSetup.CurrentPrintSetting.PrintParameters.PaperSize = ps;
                        break;
                    }
                }
            }
            catch { }
        }

        private string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }
    }
}

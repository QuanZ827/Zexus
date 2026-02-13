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
    /// Universal export tool for non-PDF formats: DWG, DXF, IFC, NWC, PNG/JPG/TIFF images, and schedule CSV.
    /// Uses a 'format' discriminator to select the correct export pipeline.
    /// </summary>
    public class ExportDocumentTool : IAgentTool
    {
        public string Name => "ExportDocument";

        public string Description =>
            "Export sheets or views to various formats: DWG, DXF, IFC, NWC (Navisworks), PNG/JPG/TIFF images, or CSV (schedules). " +
            "Requires format and output_folder. For DWG/DXF/NWC, provide sheet_numbers or view_ids. " +
            "For images, provide view_ids. For CSV, provide schedule_name.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["format"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Export format",
                        Enum = new List<string> { "dwg", "dxf", "ifc", "nwc", "png", "jpg", "tiff", "csv" }
                    },
                    ["output_folder"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Destination folder path (e.g. 'C:\\Users\\...\\Desktop\\Export'). Created automatically if it doesn't exist."
                    },
                    ["sheet_numbers"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "Sheet numbers to export (for DWG/DXF/NWC). Get from ListSheets."
                    },
                    ["view_ids"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "Element IDs of views to export (for images or DWG/DXF). Get from ListViews."
                    },

                    // DWG/DXF options
                    ["dwg_version"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "AutoCAD version for DWG/DXF export. Default: '2018'",
                        Enum = new List<string> { "2013", "2018" }
                    },

                    // IFC options
                    ["ifc_version"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "IFC schema version. Default: 'IFC4'",
                        Enum = new List<string> { "IFC2x3", "IFC4" }
                    },

                    // Image options
                    ["image_resolution"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Image resolution in DPI (for PNG/JPG/TIFF). Default: 300"
                    },
                    ["image_pixel_size"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Image width in pixels (height auto-calculated from view aspect ratio). Default: 3840"
                    },

                    // CSV options
                    ["schedule_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Name of the schedule view to export as CSV. Must match exactly."
                    }
                },
                Required = new List<string> { "format", "output_folder" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // ── Parse common parameters ──
                string format = null;
                string outputFolder = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("format", out var fObj))
                        format = fObj?.ToString()?.ToLower();
                    if (parameters.TryGetValue("output_folder", out var ofObj))
                        outputFolder = ofObj?.ToString();
                }

                if (string.IsNullOrEmpty(format))
                    return ToolResult.Fail("format is required (dwg, dxf, ifc, nwc, png, jpg, tiff, csv).");
                if (string.IsNullOrEmpty(outputFolder))
                    return ToolResult.Fail("output_folder is required.");

                // Ensure output folder exists
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                // ── Dispatch by format ──
                switch (format)
                {
                    case "dwg":
                    case "dxf":
                        return ExportDwgDxf(doc, parameters, format, outputFolder);
                    case "ifc":
                        return ExportIfc(doc, parameters, outputFolder);
                    case "nwc":
                        return ExportNwc(doc, parameters, outputFolder);
                    case "png":
                    case "jpg":
                    case "tiff":
                        return ExportImage(doc, parameters, format, outputFolder);
                    case "csv":
                        return ExportScheduleCsv(doc, parameters, outputFolder);
                    default:
                        return ToolResult.Fail($"Unsupported format: {format}. Supported: dwg, dxf, ifc, nwc, png, jpg, tiff, csv");
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Export failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  DWG / DXF Export
        // ══════════════════════════════════════════════════════════════
        private ToolResult ExportDwgDxf(Document doc, Dictionary<string, object> parameters, string format, string outputFolder)
        {
            // Parse DWG-specific options
            string dwgVersion = "2018";
            if (parameters.TryGetValue("dwg_version", out var dvObj))
                dwgVersion = dvObj?.ToString() ?? "2018";

            // Collect views/sheets to export
            var viewIds = CollectViewIds(doc, parameters);
            if (viewIds.Count == 0)
                return ToolResult.Fail("No sheets or views specified. Provide sheet_numbers or view_ids.");

            // Configure export options
            var options = new DWGExportOptions();

            // Set version
            switch (dwgVersion)
            {
                case "2013":
                    options.FileVersion = ACADVersion.R2013;
                    break;
                default: // 2018
                    options.FileVersion = ACADVersion.R2018;
                    break;
            }

            // Set format
            if (format == "dxf")
                options.ExportOfSolids = SolidGeometry.Polymesh; // DXF-compatible

            // Build view collection
            var viewIdCollection = new List<ElementId>(viewIds.Select(id => RevitCompat.CreateId(id)));

            // Export
            var exportedFiles = new List<string>();
            var fileName = $"{doc.Title ?? "export"}.{format}";

            bool success = format == "dwg"
                ? doc.Export(outputFolder, fileName, viewIdCollection, options)
                : doc.Export(outputFolder, fileName, viewIdCollection, options);

            if (success)
            {
                // DWG export creates one file per view with view name suffix
                foreach (var id in viewIdCollection)
                {
                    var view = doc.GetElement(id) as View;
                    if (view != null)
                        exportedFiles.Add($"{view.Name}.{format}");
                }
            }

            var resultData = new Dictionary<string, object>
            {
                ["format"] = format.ToUpper(),
                ["exported_count"] = exportedFiles.Count,
                ["output_files"] = exportedFiles,
                ["output_folder"] = outputFolder,
                ["dwg_version"] = dwgVersion
            };

            return success
                ? ToolResult.Ok($"Exported {exportedFiles.Count} view(s) to {format.ToUpper()} in {outputFolder}", resultData)
                : ToolResult.Fail($"{format.ToUpper()} export failed. Check that the output folder is writable.");
        }

        // ══════════════════════════════════════════════════════════════
        //  IFC Export
        // ══════════════════════════════════════════════════════════════
        private ToolResult ExportIfc(Document doc, Dictionary<string, object> parameters, string outputFolder)
        {
            string ifcVersion = "IFC4";
            if (parameters.TryGetValue("ifc_version", out var ivObj))
                ifcVersion = ivObj?.ToString() ?? "IFC4";

            var options = new IFCExportOptions();

            switch (ifcVersion.ToUpper())
            {
                case "IFC2X3":
                    options.FileVersion = IFCVersion.IFC2x3;
                    break;
                default: // IFC4
                    options.FileVersion = IFCVersion.IFC4;
                    break;
            }

            var fileName = $"{doc.Title ?? "export"}.ifc";

            using (var trans = new Transaction(doc, "IFC Export"))
            {
                trans.Start();
                bool success = doc.Export(outputFolder, fileName, options);
                trans.Commit();

                var resultData = new Dictionary<string, object>
                {
                    ["format"] = "IFC",
                    ["exported_count"] = success ? 1 : 0,
                    ["output_files"] = success ? new List<string> { fileName } : new List<string>(),
                    ["output_folder"] = outputFolder,
                    ["ifc_version"] = ifcVersion
                };

                return success
                    ? ToolResult.Ok($"Exported IFC ({ifcVersion}) to {Path.Combine(outputFolder, fileName)}", resultData)
                    : ToolResult.Fail("IFC export failed. Check that the IFC exporter is installed.");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NWC Export (Navisworks)
        // ══════════════════════════════════════════════════════════════
        private ToolResult ExportNwc(Document doc, Dictionary<string, object> parameters, string outputFolder)
        {
            try
            {
                var options = new NavisworksExportOptions();
                options.ExportScope = NavisworksExportScope.Model;

                var fileName = $"{doc.Title ?? "export"}.nwc";

                doc.Export(outputFolder, fileName, options);

                var resultData = new Dictionary<string, object>
                {
                    ["format"] = "NWC",
                    ["exported_count"] = 1,
                    ["output_files"] = new List<string> { fileName },
                    ["output_folder"] = outputFolder
                };

                return ToolResult.Ok($"Exported NWC to {Path.Combine(outputFolder, fileName)}", resultData);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Navisworks") || ex is InvalidOperationException)
                    return ToolResult.Fail("NWC export failed. Navisworks exporter may not be installed. " +
                                           "Install the 'Navisworks Export Utility' from Autodesk.");
                throw;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Image Export (PNG / JPG / TIFF)
        // ══════════════════════════════════════════════════════════════
        private ToolResult ExportImage(Document doc, Dictionary<string, object> parameters, string format, string outputFolder)
        {
            int resolution = 300;
            int pixelWidth = 3840;

            if (parameters.TryGetValue("image_resolution", out var resObj))
            {
                try { resolution = Convert.ToInt32(resObj); } catch { }
            }
            if (parameters.TryGetValue("image_pixel_size", out var sizeObj))
            {
                try { pixelWidth = Convert.ToInt32(sizeObj); } catch { }
            }

            // Get view IDs
            var viewIds = CollectViewIds(doc, parameters);
            if (viewIds.Count == 0)
            {
                // Default to active view
                var activeView = doc.ActiveView;
                if (activeView != null)
                    viewIds.Add(RevitCompat.GetIdValue(activeView.Id));
            }

            if (viewIds.Count == 0)
                return ToolResult.Fail("No views specified. Provide view_ids from ListViews.");

            var exportedFiles = new List<string>();
            int exportedCount = 0;

            foreach (var viewIdVal in viewIds)
            {
                var viewId = RevitCompat.CreateId(viewIdVal);
                var view = doc.GetElement(viewId) as View;
                if (view == null) continue;

                var options = new ImageExportOptions
                {
                    FilePath = Path.Combine(outputFolder, $"{SanitizeFileName(view.Name)}.{format}"),
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = pixelWidth,
                    ExportRange = ExportRange.SetOfViews,
                    ShadowViewsFileType = MapImageType(format)
                };

                options.SetViewsAndSheets(new List<ElementId> { viewId });

                try
                {
                    doc.ExportImage(options);
                    exportedFiles.Add($"{SanitizeFileName(view.Name)}.{format}");
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Image export failed for {view.Name}: {ex.Message}");
                }
            }

            var resultData = new Dictionary<string, object>
            {
                ["format"] = format.ToUpper(),
                ["exported_count"] = exportedCount,
                ["output_files"] = exportedFiles,
                ["output_folder"] = outputFolder,
                ["resolution_dpi"] = resolution,
                ["pixel_width"] = pixelWidth
            };

            return exportedCount > 0
                ? ToolResult.Ok($"Exported {exportedCount} image(s) as {format.ToUpper()} to {outputFolder}", resultData)
                : ToolResult.Fail($"No images were exported. Check that the specified views are valid.");
        }

        // ══════════════════════════════════════════════════════════════
        //  Schedule CSV Export
        // ══════════════════════════════════════════════════════════════
        private ToolResult ExportScheduleCsv(Document doc, Dictionary<string, object> parameters, string outputFolder)
        {
            string scheduleName = null;
            if (parameters.TryGetValue("schedule_name", out var snObj))
                scheduleName = snObj?.ToString();

            if (string.IsNullOrEmpty(scheduleName))
                return ToolResult.Fail("schedule_name is required for CSV export. Provide the exact name of a schedule view.");

            // Find the schedule view
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTemplate)
                .ToList();

            var targetSchedule = schedules.FirstOrDefault(s =>
                s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase));

            if (targetSchedule == null)
            {
                // Try partial match
                targetSchedule = schedules.FirstOrDefault(s =>
                    s.Name.IndexOf(scheduleName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (targetSchedule == null)
            {
                var available = schedules.Select(s => s.Name).Take(20).ToList();
                return ToolResult.Fail($"Schedule '{scheduleName}' not found. Available schedules: {string.Join(", ", available)}");
            }

            // Export
            var fileName = $"{SanitizeFileName(targetSchedule.Name)}.csv";
            var filePath = Path.Combine(outputFolder, fileName);

            var options = new ViewScheduleExportOptions
            {
                FieldDelimiter = ",",
                TextQualifier = ExportTextQualifier.DoubleQuote
            };

            targetSchedule.Export(outputFolder, fileName, options);

            var resultData = new Dictionary<string, object>
            {
                ["format"] = "CSV",
                ["exported_count"] = 1,
                ["output_files"] = new List<string> { fileName },
                ["output_folder"] = outputFolder,
                ["schedule_name"] = targetSchedule.Name
            };

            return ToolResult.Ok($"Exported schedule '{targetSchedule.Name}' to {filePath}", resultData);
        }

        // ══════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════
        private List<long> CollectViewIds(Document doc, Dictionary<string, object> parameters)
        {
            var ids = new List<long>();

            // From sheet_numbers
            if (parameters.TryGetValue("sheet_numbers", out var snVal))
            {
                var sheetNumbers = ParseStringArray(snVal);
                if (sheetNumbers.Count > 0)
                {
                    var allSheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .ToList();

                    foreach (var num in sheetNumbers)
                    {
                        var sheet = allSheets.FirstOrDefault(s =>
                            s.SheetNumber.Equals(num, StringComparison.OrdinalIgnoreCase));
                        if (sheet != null)
                            ids.Add(RevitCompat.GetIdValue(sheet.Id));
                    }
                }
            }

            // From view_ids
            if (parameters.TryGetValue("view_ids", out var vidVal))
            {
                var viewIdList = ParseLongArray(vidVal);
                ids.AddRange(viewIdList);
            }

            return ids;
        }

        private List<string> ParseStringArray(object val)
        {
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

            var single = val?.ToString();
            if (!string.IsNullOrEmpty(single))
                return new List<string> { single };

            return new List<string>();
        }

        private List<long> ParseLongArray(object val)
        {
            var result = new List<long>();

            if (val is List<object> objList)
            {
                foreach (var o in objList)
                {
                    try { result.Add(Convert.ToInt64(o)); } catch { }
                }
                return result;
            }

            if (val is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in je.EnumerateArray())
                {
                    try { result.Add(item.GetInt64()); } catch { }
                }
                return result;
            }

            return result;
        }

        private ImageFileType MapImageType(string format)
        {
            switch (format.ToLower())
            {
                case "jpg": return ImageFileType.JPEGLossless;
                case "tiff": return ImageFileType.TIFF;
                default: return ImageFileType.PNG;
            }
        }

        private string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }
    }
}

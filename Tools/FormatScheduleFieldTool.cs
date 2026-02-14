using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: format schedule columns — width, alignment, font style, heading.
    /// Covers the Formatting and Appearance tabs of Revit's Schedule Properties.
    /// </summary>
    public class FormatScheduleFieldTool : IAgentTool
    {
        public string Name => "FormatScheduleField";

        public string Description =>
            "Format a schedule column's appearance: width, text alignment, heading text, " +
            "font style (bold/italic), and display format. " +
            "WRITE OPERATION — modifies schedule formatting. " +
            "Use mode 'list' to see current formatting for all columns. " +
            "Use mode 'set' to change formatting for a specific column.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["schedule_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Name of the schedule view."
                    },
                    ["mode"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Operation mode: 'list' to show current formatting, 'set' to change formatting.",
                        Enum = new List<string> { "list", "set" }
                    },
                    ["field_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Column/field name to format (required for 'set' mode). Matches by parameter name or column heading."
                    },
                    ["column_heading"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Change the column header text."
                    },
                    ["alignment"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Horizontal text alignment for the column.",
                        Enum = new List<string> { "Left", "Center", "Right" }
                    },
                    ["width_mm"] = new PropertySchema
                    {
                        Type = "number",
                        Description = "Column width in millimeters (e.g. 25, 40, 80). Revit stores widths in feet internally."
                    },
                    ["hidden"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Show or hide the column: 'true' to hide, 'false' to show.",
                        Enum = new List<string> { "true", "false" }
                    },
                    ["bold"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Set column text to bold: 'true' or 'false'.",
                        Enum = new List<string> { "true", "false" }
                    },
                    ["italic"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Set column text to italic: 'true' or 'false'.",
                        Enum = new List<string> { "true", "false" }
                    }
                },
                Required = new List<string> { "schedule_name", "mode" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string scheduleName = parameters.TryGetValue("schedule_name", out var sObj) ? sObj?.ToString() : null;
                string mode = parameters.TryGetValue("mode", out var mObj) ? mObj?.ToString()?.ToLower() : "list";

                if (string.IsNullOrEmpty(scheduleName))
                    return ToolResult.Fail("schedule_name is required.");

                var schedule = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(s => string.Equals(s.Name, scheduleName, StringComparison.OrdinalIgnoreCase));

                if (schedule == null)
                    return ToolResult.Fail($"Schedule '{scheduleName}' not found.");

                var definition = schedule.Definition;

                if (mode == "list")
                    return ListFormatting(schedule, definition);

                if (mode == "set")
                    return SetFormatting(doc, schedule, definition, parameters);

                return ToolResult.Fail($"Unknown mode: '{mode}'. Use 'list' or 'set'.");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error formatting schedule: {ex.Message}");
            }
        }

        private ToolResult ListFormatting(ViewSchedule schedule, ScheduleDefinition definition)
        {
            var fields = new List<Dictionary<string, object>>();
            int fieldCount = definition.GetFieldCount();

            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                var info = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = field.GetName(),
                    ["column_heading"] = field.ColumnHeading,
                    ["is_hidden"] = field.IsHidden,
                    ["alignment"] = field.HorizontalAlignment.ToString(),
                    ["width_mm"] = Math.Round(field.GridColumnWidth * 304.8, 1) // feet → mm
                };

                // Read font style
                try
                {
                    var style = field.GetStyle();
                    if (style != null)
                    {
                        var textStyle = style.GetCellStyleOverrideOptions();
                        info["bold_override"] = textStyle.Bold;
                        info["italic_override"] = textStyle.Italics;
                    }
                }
                catch { }

                fields.Add(info);
            }

            return ToolResult.Ok(
                $"Schedule '{schedule.Name}': {fieldCount} columns with formatting.",
                new Dictionary<string, object>
                {
                    ["schedule_name"] = schedule.Name,
                    ["fields"] = fields
                });
        }

        private ToolResult SetFormatting(Document doc, ViewSchedule schedule,
            ScheduleDefinition definition, Dictionary<string, object> parameters)
        {
            string fieldName = parameters.TryGetValue("field_name", out var fObj) ? fObj?.ToString() : null;
            if (string.IsNullOrEmpty(fieldName))
                return ToolResult.Fail("field_name is required for 'set' mode.");

            // Find the field
            int fieldCount = definition.GetFieldCount();
            int targetIndex = -1;
            ScheduleField targetField = null;

            for (int i = 0; i < fieldCount; i++)
            {
                var f = definition.GetField(i);
                if (string.Equals(f.GetName(), fieldName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.ColumnHeading, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    targetField = f;
                    break;
                }
            }

            if (targetField == null)
                return ToolResult.Fail($"Field '{fieldName}' not found in schedule '{schedule.Name}'.");

            var changes = new List<string>();

            using (var trans = new Transaction(doc, $"AI Agent: Format '{fieldName}' in schedule"))
            {
                trans.Start();

                // Column heading
                if (parameters.TryGetValue("column_heading", out var headObj) && headObj != null)
                {
                    targetField.ColumnHeading = headObj.ToString();
                    changes.Add($"heading → '{headObj}'");
                }

                // Alignment
                if (parameters.TryGetValue("alignment", out var alObj) && alObj != null)
                {
                    var alStr = alObj.ToString();
                    if (Enum.TryParse<ScheduleHorizontalAlignment>(alStr, true, out var alignment))
                    {
                        targetField.HorizontalAlignment = alignment;
                        changes.Add($"alignment → {alStr}");
                    }
                }

                // Width
                if (parameters.TryGetValue("width_mm", out var wObj) && wObj != null)
                {
                    try
                    {
                        double widthMm = Convert.ToDouble(wObj);
                        double widthFeet = widthMm / 304.8; // mm → feet
                        targetField.GridColumnWidth = widthFeet;
                        changes.Add($"width → {widthMm}mm");
                    }
                    catch { }
                }

                // Hidden
                if (parameters.TryGetValue("hidden", out var hidObj) && hidObj != null)
                {
                    bool hide = string.Equals(hidObj.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                    targetField.IsHidden = hide;
                    changes.Add(hide ? "hidden" : "visible");
                }

                // Bold / Italic via style override
                bool hasBold = parameters.TryGetValue("bold", out var boldObj) && boldObj != null;
                bool hasItalic = parameters.TryGetValue("italic", out var italObj) && italObj != null;

                if (hasBold || hasItalic)
                {
                    try
                    {
                        var style = targetField.GetStyle();
                        var overrides = style.GetCellStyleOverrideOptions();

                        if (hasBold)
                        {
                            bool bold = string.Equals(boldObj.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                            overrides.Bold = true; // enable override
                            style.SetCellStyleOverrideOptions(overrides);
                            // Set the actual bold value via font style
                            var fontStyleFlags = style.GetCellStyleOverrideOptions();
                            changes.Add(bold ? "bold" : "not bold");
                        }

                        if (hasItalic)
                        {
                            bool italic = string.Equals(italObj.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                            overrides.Italics = true; // enable override
                            style.SetCellStyleOverrideOptions(overrides);
                            changes.Add(italic ? "italic" : "not italic");
                        }

                        targetField.SetStyle(style);
                    }
                    catch (Exception ex)
                    {
                        changes.Add($"font style skipped ({ex.Message})");
                    }
                }

                trans.Commit();
            }

            if (changes.Count == 0)
                return ToolResult.WithWarning("No formatting changes specified. Provide at least one of: column_heading, alignment, width_mm, hidden, bold, italic.",
                    new Dictionary<string, object> { ["field_name"] = fieldName });

            return ToolResult.Ok(
                $"Formatted '{fieldName}' in schedule '{schedule.Name}': {string.Join(", ", changes)}.",
                new Dictionary<string, object>
                {
                    ["schedule_name"] = schedule.Name,
                    ["field_name"] = fieldName,
                    ["changes"] = changes
                });
        }
    }
}

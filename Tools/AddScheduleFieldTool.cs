using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to add a field (column) to an existing Schedule view.
    /// Can also remove or reorder fields.
    /// </summary>
    public class AddScheduleFieldTool : IAgentTool
    {
        public string Name => "AddScheduleField";

        public string Description =>
            "Add, remove, or list fields (columns) in a Schedule view. " +
            "WRITE OPERATION when adding/removing fields. " +
            "Use mode 'list' to see available fields before adding. " +
            "Use mode 'add' to add a parameter as a column. " +
            "Use mode 'remove' to remove a column by name.";

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
                        Description = "Name of the schedule view to modify."
                    },
                    ["mode"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Operation mode: 'list' to show current and available fields, 'add' to add a field, 'remove' to remove a field.",
                        Enum = new List<string> { "list", "add", "remove" }
                    },
                    ["field_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Parameter/field name to add or remove (required for 'add' and 'remove' modes)."
                    },
                    ["column_header"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Optional custom column header text. If not provided, uses the parameter name."
                    },
                    ["hidden"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Set to 'true' to add the field but keep it hidden (useful for filtering/sorting). Default: false.",
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
                string fieldName = parameters.TryGetValue("field_name", out var fObj) ? fObj?.ToString() : null;
                string columnHeader = parameters.TryGetValue("column_header", out var hObj) ? hObj?.ToString() : null;
                bool hidden = parameters.TryGetValue("hidden", out var hidObj) &&
                    string.Equals(hidObj?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrEmpty(scheduleName))
                    return ToolResult.Fail("schedule_name is required");

                // Find the schedule
                var schedule = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(s => string.Equals(s.Name, scheduleName, StringComparison.OrdinalIgnoreCase));

                if (schedule == null)
                    return ToolResult.Fail($"Schedule '{scheduleName}' not found. Use ListViews to find available schedules.");

                var definition = schedule.Definition;

                switch (mode)
                {
                    case "list":
                        return ListFields(schedule, definition);
                    case "add":
                        if (string.IsNullOrEmpty(fieldName))
                            return ToolResult.Fail("field_name is required for 'add' mode");
                        return AddField(doc, schedule, definition, fieldName, columnHeader, hidden);
                    case "remove":
                        if (string.IsNullOrEmpty(fieldName))
                            return ToolResult.Fail("field_name is required for 'remove' mode");
                        return RemoveField(doc, schedule, definition, fieldName);
                    default:
                        return ToolResult.Fail($"Unknown mode: '{mode}'. Use 'list', 'add', or 'remove'.");
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error modifying schedule field: {ex.Message}");
            }
        }

        private ToolResult ListFields(ViewSchedule schedule, ScheduleDefinition definition)
        {
            // Current fields
            var currentFields = new List<Dictionary<string, object>>();
            int fieldCount = definition.GetFieldCount();
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                currentFields.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = field.GetName(),
                    ["column_header"] = field.ColumnHeading,
                    ["is_hidden"] = field.IsHidden,
                    ["field_type"] = field.FieldType.ToString()
                });
            }

            // Available (schedulable) fields not yet added
            var schedulableFields = definition.GetSchedulableFields();
            var currentFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fieldCount; i++)
                currentFieldNames.Add(definition.GetField(i).GetName());

            var availableFields = new List<Dictionary<string, object>>();
            foreach (var sf in schedulableFields)
            {
                string sfName = sf.GetName(null);
                if (!string.IsNullOrEmpty(sfName) && !currentFieldNames.Contains(sfName))
                {
                    availableFields.Add(new Dictionary<string, object>
                    {
                        ["name"] = sfName,
                        ["field_type"] = sf.FieldType.ToString()
                    });
                }
            }

            var resultData = new Dictionary<string, object>
            {
                ["schedule_name"] = schedule.Name,
                ["current_fields"] = currentFields,
                ["current_field_count"] = currentFields.Count,
                ["available_fields"] = availableFields,
                ["available_field_count"] = availableFields.Count
            };

            return ToolResult.Ok(
                $"Schedule '{schedule.Name}': {currentFields.Count} current fields, {availableFields.Count} available fields to add.",
                resultData);
        }

        private ToolResult AddField(Document doc, ViewSchedule schedule, ScheduleDefinition definition,
            string fieldName, string columnHeader, bool hidden)
        {
            // Check if field already exists
            int fieldCount = definition.GetFieldCount();
            for (int i = 0; i < fieldCount; i++)
            {
                var existing = definition.GetField(i);
                if (string.Equals(existing.GetName(), fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.WithWarning(
                        $"Field '{fieldName}' is already in schedule '{schedule.Name}' at position {i}.",
                        new Dictionary<string, object>
                        {
                            ["already_exists"] = true,
                            ["field_name"] = fieldName,
                            ["position"] = i
                        });
                }
            }

            // Find the schedulable field
            var schedulableFields = definition.GetSchedulableFields();
            SchedulableField targetField = null;
            foreach (var sf in schedulableFields)
            {
                if (string.Equals(sf.GetName(null), fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    targetField = sf;
                    break;
                }
            }

            if (targetField == null)
            {
                // Collect available names for helpful error
                var available = schedulableFields
                    .Select(sf => sf.GetName(null))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n)
                    .Take(30)
                    .ToList();

                return ToolResult.Fail(
                    $"Field '{fieldName}' not found in schedulable fields. " +
                    $"Use mode 'list' to see available fields. Some options: {string.Join(", ", available)}");
            }

            // Add the field
            using (var trans = new Transaction(doc, $"AI Agent: Add field '{fieldName}' to schedule"))
            {
                trans.Start();

                var newField = definition.AddField(targetField);

                if (!string.IsNullOrEmpty(columnHeader))
                    newField.ColumnHeading = columnHeader;

                if (hidden)
                    newField.IsHidden = true;

                trans.Commit();
            }

            var resultData = new Dictionary<string, object>
            {
                ["success"] = true,
                ["schedule_name"] = schedule.Name,
                ["field_name"] = fieldName,
                ["column_header"] = columnHeader ?? fieldName,
                ["is_hidden"] = hidden,
                ["position"] = definition.GetFieldCount() - 1
            };

            return ToolResult.Ok(
                $"Added field '{fieldName}' to schedule '{schedule.Name}' at position {definition.GetFieldCount() - 1}." +
                (hidden ? " (hidden)" : ""),
                resultData);
        }

        private ToolResult RemoveField(Document doc, ViewSchedule schedule, ScheduleDefinition definition, string fieldName)
        {
            int fieldCount = definition.GetFieldCount();
            int targetIndex = -1;

            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (string.Equals(field.GetName(), fieldName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(field.ColumnHeading, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                return ToolResult.Fail($"Field '{fieldName}' not found in schedule '{schedule.Name}'.");

            using (var trans = new Transaction(doc, $"AI Agent: Remove field '{fieldName}' from schedule"))
            {
                trans.Start();
                definition.RemoveField(targetIndex);
                trans.Commit();
            }

            return ToolResult.Ok(
                $"Removed field '{fieldName}' from schedule '{schedule.Name}'.",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["removed_field"] = fieldName,
                    ["remaining_fields"] = definition.GetFieldCount()
                });
        }
    }
}

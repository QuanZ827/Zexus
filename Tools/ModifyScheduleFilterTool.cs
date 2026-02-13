using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to add, remove, or list filters on a Schedule view.
    /// </summary>
    public class ModifyScheduleFilterTool : IAgentTool
    {
        public string Name => "ModifyScheduleFilter";

        public string Description =>
            "Add, remove, clear, or list filters on a Schedule view. " +
            "Filters control which rows appear in the schedule. " +
            "Use mode 'list' to see current filters, 'add' to add a filter, " +
            "'remove' to remove a filter by index, 'clear' to remove all filters.";

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
                        Description = "Operation: 'list', 'add', 'remove' (by index), or 'clear' (all filters).",
                        Enum = new List<string> { "list", "add", "remove", "clear" }
                    },
                    ["field_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Field/parameter name to filter by (required for 'add' mode). Must be a field in the schedule."
                    },
                    ["operator"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter operator (required for 'add' mode).",
                        Enum = new List<string> { "Equal", "NotEqual", "GreaterThan", "GreaterOrEqual", "LessThan", "LessOrEqual", "Contains", "NotContains", "BeginsWith", "NotBeginsWith", "EndsWith", "NotEndsWith", "HasValue", "HasNoValue" }
                    },
                    ["value"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter value (required for most operators, not needed for HasValue/HasNoValue)."
                    },
                    ["filter_index"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Index of the filter to remove (required for 'remove' mode, 0-based)."
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
                string operatorStr = parameters.TryGetValue("operator", out var oObj) ? oObj?.ToString() : null;
                string value = parameters.TryGetValue("value", out var vObj) ? vObj?.ToString() : null;
                int filterIndex = -1;
                if (parameters.TryGetValue("filter_index", out var iObj))
                {
                    if (iObj is int ii) filterIndex = ii;
                    else if (iObj is long ll) filterIndex = (int)ll;
                    else int.TryParse(iObj?.ToString(), out filterIndex);
                }

                if (string.IsNullOrEmpty(scheduleName))
                    return ToolResult.Fail("schedule_name is required");

                var schedule = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(s => string.Equals(s.Name, scheduleName, StringComparison.OrdinalIgnoreCase));

                if (schedule == null)
                    return ToolResult.Fail($"Schedule '{scheduleName}' not found.");

                var definition = schedule.Definition;

                switch (mode)
                {
                    case "list":
                        return ListFilters(schedule, definition);
                    case "add":
                        return AddFilter(doc, schedule, definition, fieldName, operatorStr, value);
                    case "remove":
                        return RemoveFilter(doc, schedule, definition, filterIndex);
                    case "clear":
                        return ClearFilters(doc, schedule, definition);
                    default:
                        return ToolResult.Fail($"Unknown mode: '{mode}'.");
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error modifying schedule filter: {ex.Message}");
            }
        }

        private ToolResult ListFilters(ViewSchedule schedule, ScheduleDefinition definition)
        {
            var filters = new List<Dictionary<string, object>>();
            int filterCount = definition.GetFilterCount();

            for (int i = 0; i < filterCount; i++)
            {
                var filter = definition.GetFilter(i);
                var filterFieldId = filter.FieldId;
                string fName = "(unknown)";
                // Match FieldId against schedule fields
                for (int fi = 0; fi < definition.GetFieldCount(); fi++)
                {
                    if (definition.GetField(fi).FieldId == filterFieldId)
                    {
                        fName = definition.GetField(fi).GetName();
                        break;
                    }
                }

                filters.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["field_name"] = fName,
                    ["operator"] = filter.FilterType.ToString(),
                    ["value"] = filter.IsStringValue ? filter.GetStringValue() : filter.GetDoubleValue().ToString()
                });
            }

            return ToolResult.Ok(
                $"Schedule '{schedule.Name}' has {filterCount} filter(s).",
                new Dictionary<string, object>
                {
                    ["schedule_name"] = schedule.Name,
                    ["filters"] = filters,
                    ["filter_count"] = filterCount
                });
        }

        private ToolResult AddFilter(Document doc, ViewSchedule schedule, ScheduleDefinition definition,
            string fieldName, string operatorStr, string value)
        {
            if (string.IsNullOrEmpty(fieldName))
                return ToolResult.Fail("field_name is required for 'add' mode");
            if (string.IsNullOrEmpty(operatorStr))
                return ToolResult.Fail("operator is required for 'add' mode");

            // Find the field index in the schedule
            int fieldIndex = -1;
            int fieldCount = definition.GetFieldCount();
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (string.Equals(field.GetName(), fieldName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(field.ColumnHeading, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    fieldIndex = i;
                    break;
                }
            }

            if (fieldIndex < 0)
            {
                var availableNames = new List<string>();
                for (int i = 0; i < fieldCount; i++)
                    availableNames.Add(definition.GetField(i).GetName());
                return ToolResult.Fail(
                    $"Field '{fieldName}' not found in schedule. The field must be added to the schedule first. " +
                    $"Current fields: {string.Join(", ", availableNames)}");
            }

            var filterType = MapFilterType(operatorStr);

            using (var trans = new Transaction(doc, $"AI Agent: Add filter on '{fieldName}'"))
            {
                trans.Start();

                var fieldId = definition.GetField(fieldIndex).FieldId;
                var filter = new ScheduleFilter(fieldId, filterType, value ?? "");
                definition.AddFilter(filter);

                trans.Commit();
            }

            return ToolResult.Ok(
                $"Added filter to schedule '{schedule.Name}': {fieldName} {operatorStr} '{value ?? ""}'",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["field_name"] = fieldName,
                    ["operator"] = operatorStr,
                    ["value"] = value ?? "",
                    ["total_filters"] = definition.GetFilterCount()
                });
        }

        private ToolResult RemoveFilter(Document doc, ViewSchedule schedule, ScheduleDefinition definition, int filterIndex)
        {
            int filterCount = definition.GetFilterCount();
            if (filterIndex < 0 || filterIndex >= filterCount)
                return ToolResult.Fail($"Invalid filter_index: {filterIndex}. Schedule has {filterCount} filter(s) (0-based index).");

            using (var trans = new Transaction(doc, "AI Agent: Remove schedule filter"))
            {
                trans.Start();
                definition.RemoveFilter(filterIndex);
                trans.Commit();
            }

            return ToolResult.Ok(
                $"Removed filter at index {filterIndex} from schedule '{schedule.Name}'. {definition.GetFilterCount()} filter(s) remaining.",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["removed_index"] = filterIndex,
                    ["remaining_filters"] = definition.GetFilterCount()
                });
        }

        private ToolResult ClearFilters(Document doc, ViewSchedule schedule, ScheduleDefinition definition)
        {
            int filterCount = definition.GetFilterCount();
            if (filterCount == 0)
                return ToolResult.Ok($"Schedule '{schedule.Name}' has no filters to clear.",
                    new Dictionary<string, object> { ["schedule_name"] = schedule.Name, ["cleared"] = 0 });

            using (var trans = new Transaction(doc, "AI Agent: Clear all schedule filters"))
            {
                trans.Start();
                definition.ClearFilters();
                trans.Commit();
            }

            return ToolResult.Ok(
                $"Cleared all {filterCount} filter(s) from schedule '{schedule.Name}'.",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["cleared"] = filterCount
                });
        }

        private ScheduleFilterType MapFilterType(string op)
        {
            switch (op?.ToLower())
            {
                case "equal": return ScheduleFilterType.Equal;
                case "notequal": return ScheduleFilterType.NotEqual;
                case "greaterthan": return ScheduleFilterType.GreaterThan;
                case "greaterorequal": return ScheduleFilterType.GreaterThanOrEqual;
                case "lessthan": return ScheduleFilterType.LessThan;
                case "lessorequal": return ScheduleFilterType.LessThanOrEqual;
                case "contains": return ScheduleFilterType.Contains;
                case "notcontains": return ScheduleFilterType.NotContains;
                case "beginswith": return ScheduleFilterType.BeginsWith;
                case "notbeginswith": return ScheduleFilterType.NotBeginsWith;
                case "endswith": return ScheduleFilterType.EndsWith;
                case "notendswith": return ScheduleFilterType.NotEndsWith;
                case "hasvalue": return ScheduleFilterType.HasValue;
                case "hasnovalue": return ScheduleFilterType.HasNoValue;
                default: return ScheduleFilterType.Equal;
            }
        }
    }
}

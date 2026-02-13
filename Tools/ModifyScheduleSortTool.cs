using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to add, remove, or list sort/group definitions on a Schedule view.
    /// </summary>
    public class ModifyScheduleSortTool : IAgentTool
    {
        public string Name => "ModifyScheduleSort";

        public string Description =>
            "Add, remove, clear, or list sort/group definitions on a Schedule view. " +
            "Sort definitions control the order of rows and optional grouping with headers/footers. " +
            "Use mode 'list' to see current sort settings, 'add' to add a sort field, " +
            "'remove' to remove by index, 'clear' to remove all sort definitions.";

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
                        Description = "Operation: 'list', 'add', 'remove' (by index), or 'clear' (all sort definitions).",
                        Enum = new List<string> { "list", "add", "remove", "clear" }
                    },
                    ["field_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Field/parameter name to sort by (required for 'add' mode). Must be a field already in the schedule."
                    },
                    ["sort_order"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Sort direction (for 'add' mode). Default: ascending.",
                        Enum = new List<string> { "ascending", "descending" }
                    },
                    ["show_header"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Show group header when grouping by this field (for 'add' mode). Default: false.",
                        Enum = new List<string> { "true", "false" }
                    },
                    ["show_footer"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Show group footer with totals (for 'add' mode). Default: false.",
                        Enum = new List<string> { "true", "false" }
                    },
                    ["blank_line"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Show blank line between groups (for 'add' mode). Default: false.",
                        Enum = new List<string> { "true", "false" }
                    },
                    ["itemize_every_instance"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Itemize every instance (show each element as a row). Default: true. Set to false to collapse groups.",
                        Enum = new List<string> { "true", "false" }
                    },
                    ["sort_index"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Index of the sort definition to remove (required for 'remove' mode, 0-based)."
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
                string sortOrder = parameters.TryGetValue("sort_order", out var soObj) ? soObj?.ToString()?.ToLower() : "ascending";
                bool showHeader = parameters.TryGetValue("show_header", out var shObj) &&
                    string.Equals(shObj?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                bool showFooter = parameters.TryGetValue("show_footer", out var sfObj) &&
                    string.Equals(sfObj?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                bool blankLine = parameters.TryGetValue("blank_line", out var blObj) &&
                    string.Equals(blObj?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                bool itemize = !parameters.TryGetValue("itemize_every_instance", out var ieObj) ||
                    !string.Equals(ieObj?.ToString(), "false", StringComparison.OrdinalIgnoreCase);

                int sortIndex = -1;
                if (parameters.TryGetValue("sort_index", out var iObj))
                {
                    if (iObj is int ii) sortIndex = ii;
                    else if (iObj is long ll) sortIndex = (int)ll;
                    else int.TryParse(iObj?.ToString(), out sortIndex);
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
                        return ListSortGroups(schedule, definition);
                    case "add":
                        return AddSortGroup(doc, schedule, definition, fieldName, sortOrder, showHeader, showFooter, blankLine, itemize);
                    case "remove":
                        return RemoveSortGroup(doc, schedule, definition, sortIndex);
                    case "clear":
                        return ClearSortGroups(doc, schedule, definition);
                    default:
                        return ToolResult.Fail($"Unknown mode: '{mode}'.");
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error modifying schedule sort: {ex.Message}");
            }
        }

        private ToolResult ListSortGroups(ViewSchedule schedule, ScheduleDefinition definition)
        {
            var sortGroups = new List<Dictionary<string, object>>();
            int sortCount = definition.GetSortGroupFieldCount();

            for (int i = 0; i < sortCount; i++)
            {
                var sg = definition.GetSortGroupField(i);
                var sortFieldId = sg.FieldId;
                string fName = "(unknown)";
                // Match FieldId against schedule fields
                for (int fi = 0; fi < definition.GetFieldCount(); fi++)
                {
                    if (definition.GetField(fi).FieldId == sortFieldId)
                    {
                        fName = definition.GetField(fi).GetName();
                        break;
                    }
                }

                sortGroups.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["field_name"] = fName,
                    ["sort_order"] = sg.SortOrder == ScheduleSortOrder.Ascending ? "ascending" : "descending",
                    ["show_header"] = sg.ShowHeader,
                    ["show_footer"] = sg.ShowFooter,
                    ["show_blank_line"] = sg.ShowBlankLine
                });
            }

            return ToolResult.Ok(
                $"Schedule '{schedule.Name}' has {sortCount} sort/group definition(s). " +
                $"Itemize every instance: {definition.IsItemized}",
                new Dictionary<string, object>
                {
                    ["schedule_name"] = schedule.Name,
                    ["sort_groups"] = sortGroups,
                    ["sort_count"] = sortCount,
                    ["is_itemized"] = definition.IsItemized
                });
        }

        private ToolResult AddSortGroup(Document doc, ViewSchedule schedule, ScheduleDefinition definition,
            string fieldName, string sortOrder, bool showHeader, bool showFooter, bool blankLine, bool itemize)
        {
            if (string.IsNullOrEmpty(fieldName))
                return ToolResult.Fail("field_name is required for 'add' mode");

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

            var fieldId = definition.GetField(fieldIndex).FieldId;
            var order = string.Equals(sortOrder, "descending", StringComparison.OrdinalIgnoreCase)
                ? ScheduleSortOrder.Descending
                : ScheduleSortOrder.Ascending;

            using (var trans = new Transaction(doc, $"AI Agent: Add sort on '{fieldName}'"))
            {
                trans.Start();

                var sortGroup = new ScheduleSortGroupField(fieldId, order);
                sortGroup.ShowHeader = showHeader;
                sortGroup.ShowFooter = showFooter;
                sortGroup.ShowBlankLine = blankLine;
                definition.AddSortGroupField(sortGroup);

                // Set itemize mode
                definition.IsItemized = itemize;

                trans.Commit();
            }

            return ToolResult.Ok(
                $"Added sort/group to schedule '{schedule.Name}': {fieldName} ({sortOrder})" +
                (showHeader ? " [header]" : "") + (showFooter ? " [footer]" : "") +
                (blankLine ? " [blank line]" : "") +
                $". Itemize: {itemize}",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["field_name"] = fieldName,
                    ["sort_order"] = sortOrder,
                    ["show_header"] = showHeader,
                    ["show_footer"] = showFooter,
                    ["blank_line"] = blankLine,
                    ["itemize"] = itemize,
                    ["total_sort_groups"] = definition.GetSortGroupFieldCount()
                });
        }

        private ToolResult RemoveSortGroup(Document doc, ViewSchedule schedule, ScheduleDefinition definition, int sortIndex)
        {
            int sortCount = definition.GetSortGroupFieldCount();
            if (sortIndex < 0 || sortIndex >= sortCount)
                return ToolResult.Fail($"Invalid sort_index: {sortIndex}. Schedule has {sortCount} sort definition(s) (0-based index).");

            using (var trans = new Transaction(doc, "AI Agent: Remove schedule sort"))
            {
                trans.Start();
                definition.RemoveSortGroupField(sortIndex);
                trans.Commit();
            }

            return ToolResult.Ok(
                $"Removed sort definition at index {sortIndex} from schedule '{schedule.Name}'. {definition.GetSortGroupFieldCount()} sort definition(s) remaining.",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["removed_index"] = sortIndex,
                    ["remaining_sort_groups"] = definition.GetSortGroupFieldCount()
                });
        }

        private ToolResult ClearSortGroups(Document doc, ViewSchedule schedule, ScheduleDefinition definition)
        {
            int sortCount = definition.GetSortGroupFieldCount();
            if (sortCount == 0)
                return ToolResult.Ok($"Schedule '{schedule.Name}' has no sort definitions to clear.",
                    new Dictionary<string, object> { ["schedule_name"] = schedule.Name, ["cleared"] = 0 });

            using (var trans = new Transaction(doc, "AI Agent: Clear all schedule sort definitions"))
            {
                trans.Start();
                definition.ClearSortGroupFields();
                trans.Commit();
            }

            return ToolResult.Ok(
                $"Cleared all {sortCount} sort definition(s) from schedule '{schedule.Name}'.",
                new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["schedule_name"] = schedule.Name,
                    ["cleared"] = sortCount
                });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: create a new schedule view for a given Revit category.
    /// Uses ViewSchedule.CreateSchedule() — no ExecuteCode needed.
    /// </summary>
    public class CreateScheduleTool : IAgentTool
    {
        public string Name => "CreateSchedule";

        public string Description =>
            "Create a new schedule (table view) for a Revit category. " +
            "WRITE OPERATION — creates a new schedule in the project. " +
            "Specify a category (e.g. 'Walls', 'Doors') and an optional name. " +
            "After creation, use AddScheduleField to add columns, then ActivateView to open it.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["category"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Revit category for the schedule (e.g. 'Walls', 'Doors', 'Windows', 'Rooms', " +
                            "'Floors', 'Ceilings', 'Structural Columns', 'Mechanical Equipment', " +
                            "'Electrical Equipment', 'Plumbing Fixtures', 'Cable Tray', 'Conduit')."
                    },
                    ["name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Optional custom name for the schedule. If not provided, Revit assigns a default name."
                    },
                    ["fields"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "Optional list of field/parameter names to add as columns immediately after creation. " +
                            "If not provided, the schedule starts empty (use AddScheduleField to add columns later)."
                    }
                },
                Required = new List<string> { "category" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string categoryName = parameters.TryGetValue("category", out var cObj) ? cObj?.ToString() : null;
                string scheduleName = parameters.TryGetValue("name", out var nObj) ? nObj?.ToString() : null;

                // Parse optional fields list
                var fieldNames = new List<string>();
                if (parameters.TryGetValue("fields", out var fObj) && fObj is System.Collections.IEnumerable fieldList)
                {
                    foreach (var f in fieldList)
                    {
                        var fname = f?.ToString();
                        if (!string.IsNullOrEmpty(fname))
                            fieldNames.Add(fname);
                    }
                }

                if (string.IsNullOrEmpty(categoryName))
                    return ToolResult.Fail("category is required (e.g. 'Walls', 'Doors', 'Rooms').");

                // Resolve category name to BuiltInCategory
                var categoryId = ResolveCategoryId(doc, categoryName);
                if (categoryId == null)
                {
                    // List some valid categories for help
                    var validCats = GetSchedulableCategories(doc);
                    return ToolResult.Fail(
                        $"Category '{categoryName}' not found or not schedulable. " +
                        $"Valid categories include: {string.Join(", ", validCats.Take(20))}");
                }

                // Create the schedule
                ViewSchedule schedule;
                using (var trans = new Transaction(doc, $"AI Agent: Create {categoryName} Schedule"))
                {
                    trans.Start();

                    schedule = ViewSchedule.CreateSchedule(doc, categoryId);

                    if (!string.IsNullOrEmpty(scheduleName))
                    {
                        try { schedule.Name = scheduleName; }
                        catch { /* Name might conflict — Revit auto-resolves */ }
                    }

                    // Add requested fields
                    var addedFields = new List<string>();
                    var failedFields = new List<string>();
                    if (fieldNames.Count > 0)
                    {
                        var definition = schedule.Definition;
                        var schedulableFields = definition.GetSchedulableFields();

                        foreach (var fname in fieldNames)
                        {
                            var sf = schedulableFields.FirstOrDefault(f =>
                                string.Equals(f.GetName(doc), fname, StringComparison.OrdinalIgnoreCase));

                            if (sf != null)
                            {
                                try
                                {
                                    definition.AddField(sf);
                                    addedFields.Add(fname);
                                }
                                catch { failedFields.Add(fname); }
                            }
                            else
                            {
                                failedFields.Add(fname);
                            }
                        }
                    }

                    trans.Commit();
                }

                var resultData = new Dictionary<string, object>
                {
                    ["schedule_id"] = RevitCompat.GetIdValue(schedule.Id),
                    ["schedule_name"] = schedule.Name,
                    ["category"] = categoryName
                };

                var msg = $"Created schedule '{schedule.Name}' for {categoryName}.";

                if (fieldNames.Count > 0)
                {
                    var addedList = new List<string>();
                    var failedList = new List<string>();

                    // Re-check what was added
                    var definition = schedule.Definition;
                    int count = definition.GetFieldCount();
                    for (int i = 0; i < count; i++)
                        addedList.Add(definition.GetField(i).GetName());

                    resultData["fields_added"] = addedList;
                    resultData["field_count"] = addedList.Count;
                    msg += $" Added {addedList.Count} field(s).";
                }

                return ToolResult.Ok(msg, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error creating schedule: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve a user-friendly category name to an ElementId for schedule creation.
        /// </summary>
        private ElementId ResolveCategoryId(Document doc, string categoryName)
        {
            // Try matching against document categories
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null) continue;
                if (string.Equals(cat.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                    return cat.Id;
            }

            // Try common aliases
            var aliases = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                { "Wall", BuiltInCategory.OST_Walls },
                { "Walls", BuiltInCategory.OST_Walls },
                { "Door", BuiltInCategory.OST_Doors },
                { "Doors", BuiltInCategory.OST_Doors },
                { "Window", BuiltInCategory.OST_Windows },
                { "Windows", BuiltInCategory.OST_Windows },
                { "Room", BuiltInCategory.OST_Rooms },
                { "Rooms", BuiltInCategory.OST_Rooms },
                { "Floor", BuiltInCategory.OST_Floors },
                { "Floors", BuiltInCategory.OST_Floors },
                { "Ceiling", BuiltInCategory.OST_Ceilings },
                { "Ceilings", BuiltInCategory.OST_Ceilings },
                { "Column", BuiltInCategory.OST_StructuralColumns },
                { "Structural Columns", BuiltInCategory.OST_StructuralColumns },
                { "Structural Framing", BuiltInCategory.OST_StructuralFraming },
                { "Mechanical Equipment", BuiltInCategory.OST_MechanicalEquipment },
                { "Electrical Equipment", BuiltInCategory.OST_ElectricalEquipment },
                { "Electrical Fixtures", BuiltInCategory.OST_ElectricalFixtures },
                { "Lighting Fixtures", BuiltInCategory.OST_LightingFixtures },
                { "Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures },
                { "Pipe", BuiltInCategory.OST_PipeCurves },
                { "Pipes", BuiltInCategory.OST_PipeCurves },
                { "Duct", BuiltInCategory.OST_DuctCurves },
                { "Ducts", BuiltInCategory.OST_DuctCurves },
                { "Cable Tray", BuiltInCategory.OST_CableTray },
                { "Conduit", BuiltInCategory.OST_Conduit },
                { "Furniture", BuiltInCategory.OST_Furniture },
                { "Generic Models", BuiltInCategory.OST_GenericModel },
            };

            if (aliases.TryGetValue(categoryName, out var bic))
                return RevitCompat.CreateId(bic);

            return null;
        }

        /// <summary>
        /// Get a list of schedulable category names from the document.
        /// </summary>
        private List<string> GetSchedulableCategories(Document doc)
        {
            var result = new List<string>();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null) continue;
                try
                {
                    if (cat.CategoryType == CategoryType.Model && cat.AllowsBoundParameters)
                        result.Add(cat.Name);
                }
                catch { }
            }
            result.Sort();
            return result;
        }
    }
}

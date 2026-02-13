using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to create a new Project Parameter and bind it to categories.
    /// This is a WRITE operation that modifies the project parameter definitions.
    /// Uses ForgeTypeId API (available in Revit 2022+).
    /// </summary>
    public class CreateProjectParameterTool : IAgentTool
    {
        public string Name => "CreateProjectParameter";

        public string Description =>
            "Create a new Project Parameter and bind it to one or more categories. " +
            "WRITE OPERATION: This permanently adds a parameter definition to the Revit project. " +
            "You MUST confirm the parameter name, type, categories, and binding type with the user BEFORE calling this tool. " +
            "If the parameter already exists, reports the existing parameter info without creating a duplicate.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["parameter_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Name of the parameter to create. Must be unique within the project."
                    },
                    ["parameter_type"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Data type of the parameter.",
                        Enum = new List<string> { "Text", "Integer", "Number", "Length", "Area", "Volume", "Angle", "YesNo", "URL", "Material" }
                    },
                    ["categories"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Comma-separated list of category names to bind to (e.g. 'Walls,Doors,Windows'). Use 'ALL' to bind to all model categories."
                    },
                    ["is_instance"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Binding type: 'instance' for per-element values, 'type' for per-type values. Default: instance.",
                        Enum = new List<string> { "instance", "type" }
                    },
                    ["group"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Parameter group for UI organization. Default: Data.",
                        Enum = new List<string> { "Data", "General", "Identity", "Text", "Constraints", "Dimensions", "Mechanical", "Electrical", "Plumbing", "Other" }
                    }
                },
                Required = new List<string> { "parameter_name", "parameter_type", "categories" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                string paramName = parameters.TryGetValue("parameter_name", out var nameObj) ? nameObj?.ToString() : null;
                string paramType = parameters.TryGetValue("parameter_type", out var typeObj) ? typeObj?.ToString() : "Text";
                string categoriesStr = parameters.TryGetValue("categories", out var catObj) ? catObj?.ToString() : null;
                string bindingStr = parameters.TryGetValue("is_instance", out var bindObj) ? bindObj?.ToString() : "instance";
                string groupStr = parameters.TryGetValue("group", out var grpObj) ? grpObj?.ToString() : "Data";

                if (string.IsNullOrEmpty(paramName))
                    return ToolResult.Fail("parameter_name is required");
                if (string.IsNullOrEmpty(categoriesStr))
                    return ToolResult.Fail("categories is required");

                bool isInstance = !string.Equals(bindingStr, "type", StringComparison.OrdinalIgnoreCase);

                // Check if parameter already exists
                var existingParam = FindExistingProjectParameter(doc, paramName);
                if (existingParam != null)
                {
                    return ToolResult.WithWarning(
                        $"Parameter '{paramName}' already exists in this project. No duplicate created.",
                        new Dictionary<string, object>
                        {
                            ["already_exists"] = true,
                            ["parameter_name"] = paramName
                        });
                }

                // Build category set
                var categorySet = BuildCategorySet(doc, categoriesStr);
                if (categorySet.Size == 0)
                    return ToolResult.Fail($"No valid categories found from: '{categoriesStr}'");

                // Create the parameter using shared parameter approach
                // This is the most reliable cross-version method
                string tempFilePath = System.IO.Path.GetTempFileName();
                try
                {
                    var app = doc.Application;
                    string originalSharedParamFile = app.SharedParametersFilename;

                    // Create a temporary shared parameter file
                    System.IO.File.WriteAllText(tempFilePath, "");
                    app.SharedParametersFilename = tempFilePath;

                    var sharedParamFile = app.OpenSharedParameterFile();
                    var group = sharedParamFile.Groups.Create("Zexus_Parameters");

                    // ForgeTypeId API is available in Revit 2022+ (both net48 and net8.0)
                    var options = new ExternalDefinitionCreationOptions(paramName, GetSpecTypeId(paramType))
                    {
                        Visible = true
                    };

                    var externalDef = group.Definitions.Create(options);

                    // Bind to categories
                    using (var trans = new Transaction(doc, $"AI Agent: Create Parameter '{paramName}'"))
                    {
                        trans.Start();

                        ElementBinding binding;
                        if (isInstance)
                            binding = new InstanceBinding(categorySet);
                        else
                            binding = new TypeBinding(categorySet);

                        doc.ParameterBindings.Insert(externalDef, binding, GetGroupTypeId(groupStr));

                        trans.Commit();
                    }

                    // Restore original shared parameter file
                    app.SharedParametersFilename = originalSharedParamFile ?? "";

                    // Build result
                    var categoryNames = new List<string>();
                    var iter = categorySet.ForwardIterator();
                    while (iter.MoveNext())
                    {
                        if (iter.Current is Category cat)
                            categoryNames.Add(cat.Name);
                    }

                    var resultData = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["parameter_name"] = paramName,
                        ["parameter_type"] = paramType,
                        ["binding"] = isInstance ? "Instance" : "Type",
                        ["group"] = groupStr,
                        ["categories"] = categoryNames,
                        ["category_count"] = categoryNames.Count
                    };

                    return ToolResult.Ok(
                        $"Created {(isInstance ? "instance" : "type")} parameter '{paramName}' ({paramType}) bound to {categoryNames.Count} categories: {string.Join(", ", categoryNames)}",
                        resultData);
                }
                finally
                {
                    // Clean up temp file
                    try { System.IO.File.Delete(tempFilePath); } catch { }
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error creating parameter: {ex.Message}");
            }
        }

        private ParameterElement FindExistingProjectParameter(Document doc, string paramName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterElement))
                .Cast<ParameterElement>()
                .FirstOrDefault(p => string.Equals(p.GetDefinition()?.Name, paramName, StringComparison.OrdinalIgnoreCase));
        }

        private CategorySet BuildCategorySet(Document doc, string categoriesStr)
        {
            var categorySet = new CategorySet();

            if (string.Equals(categoriesStr.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.AllowsBoundParameters && cat.CategoryType == CategoryType.Model)
                        categorySet.Insert(cat);
                }
                return categorySet;
            }

            var catNames = categoriesStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

            foreach (var catName in catNames)
            {
                Category found = null;
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (string.Equals(cat.Name, catName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = cat;
                        break;
                    }
                }
                if (found != null && found.AllowsBoundParameters)
                    categorySet.Insert(found);
            }

            return categorySet;
        }

        /// <summary>
        /// Map parameter type string to ForgeTypeId (SpecTypeId).
        /// Available in Revit 2022+ (works on both net48 and net8.0 targets).
        /// </summary>
        private ForgeTypeId GetSpecTypeId(string type)
        {
            switch (type?.ToLower())
            {
                case "text": return SpecTypeId.String.Text;
                case "integer": return SpecTypeId.Int.Integer;
                case "number": return SpecTypeId.Number;
                case "length": return SpecTypeId.Length;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                case "angle": return SpecTypeId.Angle;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "url": return SpecTypeId.String.Url;
                case "material": return SpecTypeId.Reference.Material;
                default: return SpecTypeId.String.Text;
            }
        }

        /// <summary>
        /// Map parameter group string to ForgeTypeId (GroupTypeId).
        /// Available in Revit 2022+ (works on both net48 and net8.0 targets).
        /// </summary>
        private ForgeTypeId GetGroupTypeId(string group)
        {
            switch (group?.ToLower())
            {
                case "general": return GroupTypeId.General;
                case "identity": return GroupTypeId.IdentityData;
                case "text": return GroupTypeId.Text;
                case "constraints": return GroupTypeId.Constraints;
                case "dimensions": return GroupTypeId.Geometry;
                case "mechanical": return GroupTypeId.Mechanical;
                case "electrical": return GroupTypeId.Electrical;
                case "plumbing": return GroupTypeId.Plumbing;
                case "other": return GroupTypeId.General;
                default: return GroupTypeId.Data;
            }
        }
    }
}

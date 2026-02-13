using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class GetParameterValuesTool : IAgentTool
    {
        public string Name => "GetParameterValues";
        
        public string Description => 
            "Get the distribution of values for a specific parameter across elements in a category. " +
            "Shows unique values and their counts. " +
            "Use when user asks 'what values does Mark have', 'show FBTrayTier distribution', etc.";

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
                        Description = "Category to check (e.g., 'Cable Tray', 'Walls'). Required."
                    },
                    ["parameter_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Parameter name to analyze. Required."
                    },
                    ["show_element_ids"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include element IDs for each value group (default: false)"
                    }
                },
                Required = new List<string> { "category", "parameter_name" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string categoryName = null;
                string parameterName = null;
                bool showIds = false;
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("category", out var cat))
                        categoryName = cat?.ToString();
                    if (parameters.TryGetValue("parameter_name", out var pn))
                        parameterName = pn?.ToString();
                    if (parameters.TryGetValue("show_element_ids", out var si))
                        showIds = Convert.ToBoolean(si);
                }
                
                if (string.IsNullOrEmpty(categoryName) || string.IsNullOrEmpty(parameterName))
                {
                    return ToolResult.Fail("Both category and parameter_name are required");
                }
                
                Category category = null;
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        category = cat;
                        break;
                    }
                }
                
                if (category == null)
                {
                    return ToolResult.Fail($"Category '{categoryName}' not found");
                }
                
                var elements = new FilteredElementCollector(doc)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType()
                    .ToList();
                
                if (elements.Count == 0)
                {
                    return ToolResult.WithWarning($"No elements found in category '{categoryName}'",
                        new Dictionary<string, object>());
                }
                
                var valueGroups = new Dictionary<string, List<long>>();
                int elementsWithParam = 0;
                int elementsWithoutParam = 0;
                int emptyValues = 0;
                
                foreach (var elem in elements)
                {
                    var param = elem.LookupParameter(parameterName);
                    
                    if (param == null)
                    {
                        elementsWithoutParam++;
                        continue;
                    }
                    
                    elementsWithParam++;
                    
                    string value = "(empty)";
                    if (param.HasValue)
                    {
                        value = GetParameterValueAsString(param);
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            value = "(empty)";
                            emptyValues++;
                        }
                    }
                    else
                    {
                        emptyValues++;
                    }
                    
                    if (!valueGroups.ContainsKey(value))
                    {
                        valueGroups[value] = new List<long>();
                    }
                    valueGroups[value].Add(RevitCompat.GetIdValue(elem.Id));
                }
                
                if (elementsWithParam == 0)
                {
                    return ToolResult.WithWarning(
                        $"Parameter '{parameterName}' not found on any elements in category '{categoryName}'",
                        new Dictionary<string, object>
                        {
                            ["total_elements"] = elements.Count
                        });
                }
                
                var distribution = valueGroups
                    .OrderByDescending(kvp => kvp.Value.Count)
                    .Select(kvp => 
                    {
                        var item = new Dictionary<string, object>
                        {
                            ["value"] = kvp.Key,
                            ["count"] = kvp.Value.Count,
                            ["percentage"] = Math.Round((double)kvp.Value.Count / elementsWithParam * 100, 1)
                        };
                        
                        if (showIds)
                        {
                            item["element_ids"] = kvp.Value.Take(50).ToList();
                            if (kvp.Value.Count > 50)
                                item["truncated"] = true;
                        }
                        
                        return item;
                    })
                    .ToList();
                
                return ToolResult.Ok($"Found {valueGroups.Count} unique values for '{parameterName}'",
                    new Dictionary<string, object>
                    {
                        ["category"] = categoryName,
                        ["parameter_name"] = parameterName,
                        ["total_elements"] = elements.Count,
                        ["elements_with_parameter"] = elementsWithParam,
                        ["elements_without_parameter"] = elementsWithoutParam,
                        ["empty_values"] = emptyValues,
                        ["unique_values_count"] = valueGroups.Count,
                        ["distribution"] = distribution
                    });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting parameter values: {ex.Message}");
            }
        }
        
        private string GetParameterValueAsString(Parameter param)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.AsString() ?? "";
                    case StorageType.Integer:
                        return param.AsInteger().ToString();
                    case StorageType.Double:
                        return Math.Round(param.AsDouble(), 4).ToString();
                    case StorageType.ElementId:
                        return param.AsValueString() ?? "";
                    default:
                        return param.AsValueString() ?? "";
                }
            }
            catch
            {
                return "";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class GetCategoryParametersTool : IAgentTool
    {
        public string Name => "GetCategoryParameters";
        
        public string Description => 
            "Get list of all parameters available for elements in a specific category. " +
            "Shows parameter names, types, and whether they're instance or type parameters. " +
            "Use when user asks 'what parameters does X have', 'list Cable Tray parameters', etc.";

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
                        Description = "Category name (e.g., 'Walls', 'Cable Tray', 'Doors'). Required."
                    },
                    ["include_type_params"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include type parameters (default: true)"
                    }
                },
                Required = new List<string> { "category" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string categoryName = null;
                bool includeTypeParams = true;
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("category", out var cat))
                        categoryName = cat?.ToString();
                    if (parameters.TryGetValue("include_type_params", out var itp))
                        includeTypeParams = Convert.ToBoolean(itp);
                }
                
                if (string.IsNullOrEmpty(categoryName))
                {
                    return ToolResult.Fail("Category name is required");
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
                    var availableCategories = new List<string>();
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat.AllowsBoundParameters)
                            availableCategories.Add(cat.Name);
                    }
                    
                    return ToolResult.Fail($"Category '{categoryName}' not found. " +
                        $"Available categories include: {string.Join(", ", availableCategories.Take(20))}...");
                }
                
                var sampleElement = new FilteredElementCollector(doc)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();
                
                if (sampleElement == null)
                {
                    return ToolResult.WithWarning($"No elements found in category '{categoryName}'",
                        new Dictionary<string, object> { ["category"] = categoryName });
                }
                
                var instanceParams = new List<Dictionary<string, object>>();
                foreach (Parameter param in sampleElement.Parameters)
                {
                    if (param?.Definition == null) continue;
                    
                    instanceParams.Add(new Dictionary<string, object>
                    {
                        ["name"] = param.Definition.Name,
                        ["type"] = param.StorageType.ToString(),
                        ["is_readonly"] = param.IsReadOnly,
                        ["has_value"] = param.HasValue,
                        ["sample_value"] = GetSampleValue(param)
                    });
                }
                
                var typeParams = new List<Dictionary<string, object>>();
                if (includeTypeParams)
                {
                    var typeId = sampleElement.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var elemType = doc.GetElement(typeId);
                        if (elemType != null)
                        {
                            foreach (Parameter param in elemType.Parameters)
                            {
                                if (param?.Definition == null) continue;
                                
                                typeParams.Add(new Dictionary<string, object>
                                {
                                    ["name"] = param.Definition.Name,
                                    ["type"] = param.StorageType.ToString(),
                                    ["is_readonly"] = param.IsReadOnly,
                                    ["has_value"] = param.HasValue,
                                    ["sample_value"] = GetSampleValue(param)
                                });
                            }
                        }
                    }
                }
                
                instanceParams = instanceParams.OrderBy(p => p["name"].ToString()).ToList();
                typeParams = typeParams.OrderBy(p => p["name"].ToString()).ToList();
                
                return ToolResult.Ok($"Found {instanceParams.Count} instance parameters and {typeParams.Count} type parameters",
                    new Dictionary<string, object>
                    {
                        ["category"] = categoryName,
                        ["sample_element_id"] = RevitCompat.GetIdValue(sampleElement.Id),
                        ["instance_parameters"] = instanceParams,
                        ["instance_count"] = instanceParams.Count,
                        ["type_parameters"] = typeParams,
                        ["type_count"] = typeParams.Count
                    });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting category parameters: {ex.Message}");
            }
        }
        
        private object GetSampleValue(Parameter param)
        {
            try
            {
                if (!param.HasValue) return null;
                
                switch (param.StorageType)
                {
                    case StorageType.String:
                        var s = param.AsString();
                        return s?.Length > 50 ? s.Substring(0, 50) + "..." : s;
                    case StorageType.Integer:
                        return param.AsInteger();
                    case StorageType.Double:
                        return Math.Round(param.AsDouble(), 4);
                    case StorageType.ElementId:
                        return param.AsValueString();
                    default:
                        return param.AsValueString();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

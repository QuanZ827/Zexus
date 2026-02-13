using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class CheckMissingParametersTool : IAgentTool
    {
        public string Name => "CheckMissingParameters";
        
        public string Description => 
            "Find elements that have missing or empty values for specified parameters. " +
            "Use for QAQC to identify incomplete data. " +
            "Use when user asks 'find empty Mark', 'which elements are missing X', etc.";

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
                    ["parameter_names"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "List of parameter names to check. Required."
                    },
                    ["check_mode"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "'any' = missing any parameter, 'all' = missing all parameters (default: 'any')"
                    }
                },
                Required = new List<string> { "category", "parameter_names" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string categoryName = null;
                var parameterNames = new List<string>();
                string checkMode = "any";
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("category", out var cat))
                        categoryName = cat?.ToString();
                    
                    if (parameters.TryGetValue("parameter_names", out var pn) && pn != null)
                    {
                        var pnList = pn as System.Collections.IEnumerable;
                        if (pnList != null)
                        {
                            foreach (var p in pnList)
                            {
                                parameterNames.Add(p.ToString());
                            }
                        }
                    }
                    
                    if (parameters.TryGetValue("check_mode", out var cm))
                        checkMode = cm?.ToString() ?? "any";
                }
                
                if (string.IsNullOrEmpty(categoryName) || parameterNames.Count == 0)
                {
                    return ToolResult.Fail("Both category and parameter_names are required");
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
                    return ToolResult.Ok($"No elements found in category '{categoryName}'",
                        new Dictionary<string, object> { ["total_elements"] = 0 });
                }
                
                var missingByParam = new Dictionary<string, List<long>>();
                foreach (var paramName in parameterNames)
                {
                    missingByParam[paramName] = new List<long>();
                }
                
                var elementsMissingAny = new List<Dictionary<string, object>>();
                var elementsMissingAll = new List<long>();
                
                foreach (var elem in elements)
                {
                    var missingParams = new List<string>();
                    
                    foreach (var paramName in parameterNames)
                    {
                        var param = elem.LookupParameter(paramName);
                        bool isMissing = param == null || !param.HasValue;
                        
                        if (!isMissing && param.StorageType == StorageType.String)
                        {
                            isMissing = string.IsNullOrWhiteSpace(param.AsString());
                        }
                        
                        if (isMissing)
                        {
                            missingParams.Add(paramName);
                            missingByParam[paramName].Add(RevitCompat.GetIdValue(elem.Id));
                        }
                    }
                    
                    if (missingParams.Count > 0)
                    {
                        elementsMissingAny.Add(new Dictionary<string, object>
                        {
                            ["id"] = RevitCompat.GetIdValue(elem.Id),
                            ["name"] = elem.Name,
                            ["missing_parameters"] = missingParams
                        });
                    }
                    
                    if (missingParams.Count == parameterNames.Count)
                    {
                        elementsMissingAll.Add(RevitCompat.GetIdValue(elem.Id));
                    }
                }
                
                var paramSummary = parameterNames.Select(pn => new Dictionary<string, object>
                {
                    ["parameter"] = pn,
                    ["missing_count"] = missingByParam[pn].Count,
                    ["complete_count"] = elements.Count - missingByParam[pn].Count,
                    ["completeness_percentage"] = Math.Round((1 - (double)missingByParam[pn].Count / elements.Count) * 100, 1),
                    ["missing_element_ids"] = missingByParam[pn].Take(50).ToList()
                }).ToList();
                
                int issueCount = checkMode == "all" ? elementsMissingAll.Count : elementsMissingAny.Count;
                
                if (issueCount == 0)
                {
                    return ToolResult.Ok($"All {elements.Count} elements have complete parameters",
                        new Dictionary<string, object>
                        {
                            ["category"] = categoryName,
                            ["parameters_checked"] = parameterNames,
                            ["total_elements"] = elements.Count,
                            ["elements_with_issues"] = 0,
                            ["by_parameter"] = paramSummary
                        });
                }
                
                return ToolResult.WithWarning($"Found {issueCount} elements with missing parameters",
                    new Dictionary<string, object>
                    {
                        ["category"] = categoryName,
                        ["parameters_checked"] = parameterNames,
                        ["check_mode"] = checkMode,
                        ["total_elements"] = elements.Count,
                        ["elements_missing_any"] = elementsMissingAny.Count,
                        ["elements_missing_all"] = elementsMissingAll.Count,
                        ["by_parameter"] = paramSummary,
                        ["elements"] = elementsMissingAny.Take(100).ToList()
                    });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error checking missing parameters: {ex.Message}");
            }
        }
    }
}

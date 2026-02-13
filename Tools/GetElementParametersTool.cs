using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to get all parameters of an element.
    /// Returns parameter names, values, and types for AI to understand and analyze.
    /// Critical for AI to discover unknown parameter naming patterns (like Key Plan parameters).
    /// </summary>
    public class GetElementParametersTool : IAgentTool
    {
        public string Name => "GetElementParameters";
        
        public string Description => 
            "Get all parameters of a specific element by ID, or from current selection. " +
            "Returns parameter names, current values, value types, and whether they are instance or type parameters. " +
            "Use this to discover parameter naming patterns, check parameter values, or understand element configuration.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["element_id"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Element ID to get parameters from. If not provided, uses current selection."
                    },
                    ["name_filter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter parameters by name (partial match). Example: 'KEY' to find Key Plan parameters."
                    },
                    ["include_empty"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include parameters with empty/null values. Default: true"
                    },
                    ["include_type_params"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include type parameters (in addition to instance parameters). Default: true"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                int elementId = 0;
                string nameFilter = null;
                bool includeEmpty = true;
                bool includeTypeParams = true;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("element_id", out var idObj))
                    {
                        if (idObj is int i) elementId = i;
                        else if (idObj is long l) elementId = (int)l;
                        else int.TryParse(idObj?.ToString(), out elementId);
                    }
                    
                    if (parameters.TryGetValue("name_filter", out var filterObj))
                        nameFilter = filterObj?.ToString();
                    
                    if (parameters.TryGetValue("include_empty", out var emptyObj))
                    {
                        if (emptyObj is bool b) includeEmpty = b;
                        else bool.TryParse(emptyObj?.ToString(), out includeEmpty);
                    }
                    
                    if (parameters.TryGetValue("include_type_params", out var typeObj))
                    {
                        if (typeObj is bool b) includeTypeParams = b;
                        else bool.TryParse(typeObj?.ToString(), out includeTypeParams);
                    }
                }

                // Get the element
                Element element = null;

                if (elementId > 0)
                {
                    element = doc.GetElement(RevitCompat.CreateId(elementId));
                }
                else if (uiDoc != null)
                {
                    // Use first selected element
                    var selectedIds = uiDoc.Selection.GetElementIds();
                    if (selectedIds.Count > 0)
                    {
                        element = doc.GetElement(selectedIds.First());
                    }
                }

                if (element == null)
                {
                    return ToolResult.Fail(
                        "No element found. Please either:\n" +
                        "1. Provide an element_id\n" +
                        "2. Select an element in Revit before asking");
                }

                // Collect instance parameters
                var parameterList = new List<Dictionary<string, object>>();

                foreach (Parameter param in element.Parameters)
                {
                    var paramInfo = GetParameterInfo(param, false);
                    if (paramInfo != null)
                    {
                        // Apply name filter
                        if (!string.IsNullOrEmpty(nameFilter))
                        {
                            string paramName = paramInfo["name"]?.ToString() ?? "";
                            if (paramName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                        }

                        // Apply empty filter
                        if (!includeEmpty)
                        {
                            var value = paramInfo["value"];
                            if (value == null || string.IsNullOrEmpty(value.ToString()))
                                continue;
                        }

                        parameterList.Add(paramInfo);
                    }
                }

                // Collect type parameters if requested
                if (includeTypeParams)
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var elementType = doc.GetElement(typeId);
                        if (elementType != null)
                        {
                            foreach (Parameter param in elementType.Parameters)
                            {
                                var paramInfo = GetParameterInfo(param, true);
                                if (paramInfo != null)
                                {
                                    // Apply name filter
                                    if (!string.IsNullOrEmpty(nameFilter))
                                    {
                                        string paramName = paramInfo["name"]?.ToString() ?? "";
                                        if (paramName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                                            continue;
                                    }

                                    // Apply empty filter
                                    if (!includeEmpty)
                                    {
                                        var value = paramInfo["value"];
                                        if (value == null || string.IsNullOrEmpty(value.ToString()))
                                            continue;
                                    }

                                    parameterList.Add(paramInfo);
                                }
                            }
                        }
                    }
                }

                // Sort by parameter name
                parameterList = parameterList.OrderBy(p => p["name"]?.ToString() ?? "").ToList();

                // Group parameters by potential categories (for AI analysis)
                var yesNoParams = parameterList
                    .Where(p => p["storage_type"]?.ToString() == "Integer" && 
                               (p["value"]?.ToString() == "Yes" || p["value"]?.ToString() == "No"))
                    .Select(p => p["name"]?.ToString())
                    .ToList();

                var resultData = new Dictionary<string, object>
                {
                    ["element_id"] = RevitCompat.GetIdValue(element.Id),
                    ["element_name"] = element.Name,
                    ["element_category"] = element.Category?.Name ?? "Unknown",
                    ["parameter_count"] = parameterList.Count,
                    ["parameters"] = parameterList,
                    ["yes_no_parameters"] = yesNoParams  // Helpful for finding Key Plan-like parameters
                };

                // Build summary
                string summary = $"Element '{element.Name}' ({element.Category?.Name}) has {parameterList.Count} parameter(s)";
                if (yesNoParams.Count > 0)
                {
                    summary += $". Found {yesNoParams.Count} Yes/No parameters: {string.Join(", ", yesNoParams.Take(5))}";
                    if (yesNoParams.Count > 5)
                        summary += $"... and {yesNoParams.Count - 5} more";
                }

                return ToolResult.Ok(summary, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting element parameters: {ex.Message}");
            }
        }

        private Dictionary<string, object> GetParameterInfo(Parameter param, bool isTypeParam)
        {
            if (param == null)
                return null;

            try
            {
                string name = param.Definition?.Name ?? "";
                if (string.IsNullOrEmpty(name))
                    return null;

                object value = null;
                string displayValue = "";
                bool hasValue = param.HasValue;

                if (!hasValue)
                {
                    return new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["value"] = null,
                        ["display_value"] = "",
                        ["storage_type"] = param.StorageType.ToString(),
                        ["is_read_only"] = param.IsReadOnly,
                        ["is_type_parameter"] = isTypeParam,
                        ["has_value"] = false
                    };
                }

                switch (param.StorageType)
                {
                    case StorageType.String:
                        value = param.AsString() ?? "";
                        displayValue = value.ToString();
                        break;

                    case StorageType.Integer:
                        int intVal = param.AsInteger();
                        // Check if it's a Yes/No parameter using AsValueString
                        string intValueStr = param.AsValueString();
                        if (intValueStr == "Yes" || intValueStr == "No")
                        {
                            displayValue = intValueStr;
                            value = displayValue;
                        }
                        else
                        {
                            value = intVal;
                            displayValue = intVal.ToString();
                        }
                        break;

                    case StorageType.Double:
                        double dblVal = param.AsDouble();
                        value = Math.Round(dblVal, 6);
                        displayValue = param.AsValueString() ?? dblVal.ToString();
                        break;

                    case StorageType.ElementId:
                        var elemId = param.AsElementId();
                        value = RevitCompat.GetIdValue(elemId);
                        displayValue = param.AsValueString() ?? RevitCompat.GetIdValue(elemId).ToString();
                        break;

                    default:
                        return null;
                }

                return new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["value"] = value,
                    ["display_value"] = displayValue,
                    ["storage_type"] = param.StorageType.ToString(),
                    ["is_read_only"] = param.IsReadOnly,
                    ["is_type_parameter"] = isTypeParam,
                    ["has_value"] = true
                };
            }
            catch
            {
                return null;
            }
        }
    }
}

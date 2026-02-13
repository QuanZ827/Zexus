using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Batch set a parameter value on multiple elements in a single transaction.
    /// DESTRUCTIVE WRITE OPERATION - AI must confirm with user before calling.
    /// </summary>
    public class BatchSetParameterTool : IAgentTool
    {
        public string Name => "BatchSetParameter";

        public string Description =>
            "Set a parameter value on multiple elements in a single transaction. " +
            "DESTRUCTIVE WRITE OPERATION: This permanently modifies the Revit model for ALL specified elements. " +
            "You MUST present the full change list and get explicit user confirmation BEFORE calling this tool. " +
            "Use after AnalyzeNamingPatterns or FindSimilarValues to fix confirmed issues.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["element_ids"] = new PropertySchema
                    {
                        Type = "array",
                        Description = "List of element IDs to modify. Required."
                    },
                    ["parameter_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Parameter name to set on all elements. Required."
                    },
                    ["value"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "New value to set. For Yes/No parameters, use 'Yes' or 'No'. Required."
                    }
                },
                Required = new List<string> { "element_ids", "parameter_name", "value" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                var elementIds = new List<long>();
                string parameterName = null;
                string newValue = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("element_ids", out var ids) && ids != null)
                    {
                        var idList = ids as System.Collections.IEnumerable;
                        if (idList != null)
                        {
                            foreach (var id in idList)
                            {
                                elementIds.Add(Convert.ToInt64(id));
                            }
                        }
                    }

                    if (parameters.TryGetValue("parameter_name", out var pn))
                        parameterName = pn?.ToString();
                    if (parameters.TryGetValue("value", out var val))
                        newValue = val?.ToString();
                }

                // Validate
                if (elementIds.Count == 0)
                    return ToolResult.Fail("element_ids array is required and must not be empty");
                if (string.IsNullOrEmpty(parameterName))
                    return ToolResult.Fail("parameter_name is required");
                if (newValue == null)
                    return ToolResult.Fail("value is required");

                // Pre-scan: validate all elements and collect old values
                var validElements = new List<PreScanResult>();
                var failures = new List<Dictionary<string, object>>();

                foreach (var elemId in elementIds)
                {
                    var element = doc.GetElement(RevitCompat.CreateId(elemId));
                    if (element == null)
                    {
                        failures.Add(new Dictionary<string, object>
                        {
                            ["element_id"] = elemId,
                            ["reason"] = "Element not found"
                        });
                        continue;
                    }

                    // Find parameter (instance first, then type)
                    Parameter param = element.LookupParameter(parameterName);
                    Element targetElement = element;
                    bool isTypeParam = false;

                    if (param == null || param.IsReadOnly)
                    {
                        var typeId = element.GetTypeId();
                        if (typeId != ElementId.InvalidElementId)
                        {
                            var elementType = doc.GetElement(typeId);
                            if (elementType != null)
                            {
                                var typeParam = elementType.LookupParameter(parameterName);
                                if (typeParam != null && !typeParam.IsReadOnly)
                                {
                                    param = typeParam;
                                    targetElement = elementType;
                                    isTypeParam = true;
                                }
                            }
                        }
                    }

                    if (param == null)
                    {
                        failures.Add(new Dictionary<string, object>
                        {
                            ["element_id"] = elemId,
                            ["reason"] = $"Parameter '{parameterName}' not found"
                        });
                        continue;
                    }

                    if (param.IsReadOnly)
                    {
                        failures.Add(new Dictionary<string, object>
                        {
                            ["element_id"] = elemId,
                            ["reason"] = $"Parameter '{parameterName}' is read-only"
                        });
                        continue;
                    }

                    string oldValue = GetParameterDisplayValue(param);

                    validElements.Add(new PreScanResult
                    {
                        ElementId = elemId,
                        ElementName = element.Name,
                        Parameter = param,
                        TargetElement = targetElement,
                        IsTypeParameter = isTypeParam,
                        OldValue = oldValue
                    });
                }

                if (validElements.Count == 0)
                    return ToolResult.Fail($"None of the {elementIds.Count} elements could be modified. " +
                        $"Failures: {string.Join("; ", failures.Take(5).Select(f => f["reason"]))}");

                // Check for type parameter - warn in result
                bool anyTypeParams = validElements.Any(v => v.IsTypeParameter);

                // Execute in a single transaction
                var changes = new List<Dictionary<string, object>>();
                var setFailures = new List<Dictionary<string, object>>();

                using (var trans = new Transaction(doc, $"AI Agent: Batch Set {parameterName}"))
                {
                    trans.Start();

                    foreach (var item in validElements)
                    {
                        bool success = SetParameterValue(item.Parameter, newValue);
                        if (success)
                        {
                            string newDisplayValue = GetParameterDisplayValue(item.Parameter);
                            changes.Add(new Dictionary<string, object>
                            {
                                ["element_id"] = item.ElementId,
                                ["element_name"] = item.ElementName,
                                ["old_value"] = item.OldValue,
                                ["new_value"] = newDisplayValue,
                                ["is_type_parameter"] = item.IsTypeParameter
                            });
                        }
                        else
                        {
                            setFailures.Add(new Dictionary<string, object>
                            {
                                ["element_id"] = item.ElementId,
                                ["reason"] = $"Failed to set value '{newValue}'"
                            });
                        }
                    }

                    if (changes.Count > 0)
                        trans.Commit();
                    else
                        trans.RollBack();
                }

                // Combine all failures
                failures.AddRange(setFailures);

                // Build result
                var resultData = new Dictionary<string, object>
                {
                    ["parameter_name"] = parameterName,
                    ["new_value"] = newValue,
                    ["success_count"] = changes.Count,
                    ["failure_count"] = failures.Count,
                    ["changes"] = changes.Take(100).ToList(),
                    ["changes_truncated"] = changes.Count > 100
                };

                if (failures.Count > 0)
                    resultData["failures"] = failures.Take(50).ToList();

                string summary;
                if (anyTypeParams)
                {
                    summary = $"WARNING: Batch set TYPE parameter '{parameterName}' to '{newValue}' on {changes.Count} element(s). " +
                        $"Type parameter changes affect ALL instances of each type.";
                }
                else
                {
                    summary = $"Successfully set '{parameterName}' to '{newValue}' on {changes.Count} element(s)";
                }

                if (failures.Count > 0)
                    summary += $". {failures.Count} element(s) failed.";

                if (failures.Count > 0)
                    return ToolResult.WithWarning(summary, resultData);

                return ToolResult.Ok(summary, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error in batch set parameter: {ex.Message}");
            }
        }

        private class PreScanResult
        {
            public long ElementId;
            public string ElementName;
            public Parameter Parameter;
            public Element TargetElement;
            public bool IsTypeParameter;
            public string OldValue;
        }

        private string GetParameterDisplayValue(Parameter param)
        {
            if (param == null || !param.HasValue) return "";
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.AsString() ?? "";
                    case StorageType.Integer:
                        string intValueStr = param.AsValueString();
                        if (intValueStr == "Yes" || intValueStr == "No") return intValueStr;
                        return param.AsInteger().ToString();
                    case StorageType.Double:
                        return param.AsValueString() ?? param.AsDouble().ToString();
                    case StorageType.ElementId:
                        return param.AsValueString() ?? RevitCompat.GetIdValue(param.AsElementId()).ToString();
                    default:
                        return "";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] GetParameterDisplayValue error: {ex.Message}");
                return "";
            }
        }

        private bool SetParameterValue(Parameter param, string valueStr)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.Set(valueStr);

                    case StorageType.Integer:
                        string currentValueStr = param.AsValueString();
                        bool isYesNoParam = currentValueStr == "Yes" || currentValueStr == "No";

                        if (isYesNoParam ||
                            valueStr.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                            valueStr.Equals("No", StringComparison.OrdinalIgnoreCase) ||
                            valueStr.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                            valueStr.Equals("False", StringComparison.OrdinalIgnoreCase))
                        {
                            bool boolVal = valueStr.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                                          valueStr.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                                          valueStr == "1";
                            return param.Set(boolVal ? 1 : 0);
                        }
                        else
                        {
                            if (int.TryParse(valueStr, out int intVal))
                                return param.Set(intVal);
                            return false;
                        }

                    case StorageType.Double:
                        if (double.TryParse(valueStr, out double dblVal))
                            return param.Set(dblVal);
                        return param.SetValueString(valueStr);

                    case StorageType.ElementId:
                        if (long.TryParse(valueStr, out long elemIdVal))
                            return param.Set(RevitCompat.CreateId(elemIdVal));
                        return false;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] SetParameterValue error for '{valueStr}': {ex.Message}");
                return false;
            }
        }
    }
}

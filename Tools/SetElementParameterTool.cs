using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool to set/modify a parameter value on an element.
    /// This is a WRITE operation - use with care.
    /// Returns old value and new value for verification.
    /// </summary>
    public class SetElementParameterTool : IAgentTool
    {
        public string Name => "SetElementParameter";
        
        public string Description =>
            "Set or modify a parameter value on an element. " +
            "DESTRUCTIVE WRITE OPERATION: This permanently modifies the Revit model. " +
            "You MUST confirm the change with the user BEFORE calling this tool. " +
            "If instance parameter is read-only, falls back to Type parameter (affects ALL instances of that type). " +
            "Returns old and new values for verification.";

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
                        Description = "Element ID to modify. Required."
                    },
                    ["parameter_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Exact name of the parameter to set. Required."
                    },
                    ["value"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "New value to set. For Yes/No parameters, use 'Yes' or 'No'. For numbers, provide the numeric value."
                    }
                },
                Required = new List<string> { "element_id", "parameter_name", "value" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                int elementId = 0;
                string parameterName = null;
                object newValue = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("element_id", out var idObj))
                    {
                        if (idObj is int i) elementId = i;
                        else if (idObj is long l) elementId = (int)l;
                        else int.TryParse(idObj?.ToString(), out elementId);
                    }
                    
                    if (parameters.TryGetValue("parameter_name", out var nameObj))
                        parameterName = nameObj?.ToString();
                    
                    if (parameters.TryGetValue("value", out var valObj))
                        newValue = valObj;
                }

                // Validate required parameters
                if (elementId <= 0)
                    return ToolResult.Fail("element_id is required");
                
                if (string.IsNullOrEmpty(parameterName))
                    return ToolResult.Fail("parameter_name is required");
                
                if (newValue == null)
                    return ToolResult.Fail("value is required");

                // Get the element
                var element = doc.GetElement(RevitCompat.CreateId(elementId));
                if (element == null)
                    return ToolResult.Fail($"Element with ID {elementId} not found");

                // Find the parameter (check instance first, then type)
                Parameter param = null;
                bool isTypeParameter = false;
                Element targetElement = element;

                // Try instance parameter first
                param = element.LookupParameter(parameterName);
                
                // If not found or read-only, try type parameter
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
                                isTypeParameter = true;
                                targetElement = elementType;
                            }
                        }
                    }
                }

                if (param == null)
                    return ToolResult.Fail($"Parameter '{parameterName}' not found on element");
                
                if (param.IsReadOnly)
                    return ToolResult.Fail($"Parameter '{parameterName}' is read-only and cannot be modified");

                // Get old value for reporting
                string oldValue = GetParameterDisplayValue(param);

                // Set the new value within a transaction
                using (var trans = new Transaction(doc, $"AI Agent: Set {parameterName}"))
                {
                    trans.Start();

                    bool success = SetParameterValue(param, newValue);

                    if (success)
                    {
                        trans.Commit();
                    }
                    else
                    {
                        trans.RollBack();
                        return ToolResult.Fail($"Failed to set parameter '{parameterName}' to '{newValue}'");
                    }
                }

                // Get new value for verification
                string newValueDisplay = GetParameterDisplayValue(param);

                var resultData = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["element_id"] = elementId,
                    ["element_name"] = element.Name,
                    ["parameter_name"] = parameterName,
                    ["old_value"] = oldValue,
                    ["new_value"] = newValueDisplay,
                    ["is_type_parameter"] = isTypeParameter
                };

                string summary;
                if (isTypeParameter)
                {
                    summary = $"WARNING: Set TYPE parameter '{parameterName}' from '{oldValue}' to '{newValueDisplay}' on type '{targetElement.Name}'. This change affects ALL instances of this type, not just element {elementId}.";
                }
                else
                {
                    summary = $"Successfully set '{parameterName}' from '{oldValue}' to '{newValueDisplay}' on {element.Name} (ID: {elementId})";
                }

                return ToolResult.Ok(summary, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error setting parameter: {ex.Message}");
            }
        }

        private string GetParameterDisplayValue(Parameter param)
        {
            if (param == null || !param.HasValue)
                return "";

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.AsString() ?? "";

                    case StorageType.Integer:
                        int intVal = param.AsInteger();
                        // Check if Yes/No using AsValueString
                        string intValueStr = param.AsValueString();
                        if (intValueStr == "Yes" || intValueStr == "No")
                            return intValueStr;
                        return intVal.ToString();

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

        private bool SetParameterValue(Parameter param, object value)
        {
            try
            {
                string valueStr = value?.ToString() ?? "";

                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.Set(valueStr);

                    case StorageType.Integer:
                        // Handle Yes/No parameters - detect by checking current value string or input value
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
                        // Try to parse with units (SetValueString handles unit conversion)
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
                System.Diagnostics.Debug.WriteLine($"[Zexus] SetParameterValue error for '{value}': {ex.Message}");
                return false;
            }
        }
    }
}

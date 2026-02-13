using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class GetElementDetailsTool : IAgentTool
    {
        public string Name => "GetElementDetails";
        
        public string Description => 
            "Get detailed information about specific elements by ID, or currently selected elements. " +
            "Returns: category, family, type, level, parameters, and geometry info. " +
            "Use when user asks 'what is this element', 'what did I select', or needs element details.";

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
                        Description = "List of element IDs to query. If empty, uses current selection."
                    },
                    ["include_parameters"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include all parameter values (default: true)"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                bool includeParams = true;
                var elementIds = new List<ElementId>();
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("include_parameters", out var ip))
                        includeParams = Convert.ToBoolean(ip);
                    
                    if (parameters.TryGetValue("element_ids", out var ids) && ids != null)
                    {
                        var idList = ids as System.Collections.IEnumerable;
                        if (idList != null)
                        {
                            foreach (var id in idList)
                            {
                                elementIds.Add(RevitCompat.CreateId(Convert.ToInt64(id)));
                            }
                        }
                    }
                }
                
                if (elementIds.Count == 0 && uiDoc != null)
                {
                    try
                    {
                        var selection = uiDoc.Selection;
                        if (selection != null)
                        {
                            var selectedIds = selection.GetElementIds();
                            if (selectedIds != null && selectedIds.Count > 0)
                            {
                                elementIds = selectedIds.ToList();
                            }
                        }
                    }
                    catch { }
                }
                
                if (elementIds.Count == 0)
                {
                    return ToolResult.WithWarning("No elements selected or specified. Please select elements in Revit or provide element_ids.",
                        new Dictionary<string, object> { ["count"] = 0 });
                }
                
                var elementDetails = new List<Dictionary<string, object>>();
                
                foreach (var id in elementIds.Take(50))
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;
                    
                    var detail = new Dictionary<string, object>
                    {
                        ["id"] = id.Value,
                        ["category"] = elem.Category?.Name ?? "(none)",
                        ["name"] = elem.Name
                    };
                    
                    if (elem is FamilyInstance fi)
                    {
                        detail["family"] = fi.Symbol?.Family?.Name ?? "";
                        detail["type"] = fi.Symbol?.Name ?? "";
                    }
                    else
                    {
                        var typeId = elem.GetTypeId();
                        if (typeId != null && typeId != ElementId.InvalidElementId)
                        {
                            var type = doc.GetElement(typeId);
                            detail["type"] = type?.Name ?? "";
                        }
                    }
                    
                    try
                    {
                        var levelParam = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                        if (levelParam != null && levelParam.HasValue)
                        {
                            var levelId = levelParam.AsElementId();
                            if (levelId != null && levelId != ElementId.InvalidElementId)
                            {
                                var level = doc.GetElement(levelId) as Level;
                                detail["level"] = level?.Name ?? "";
                            }
                        }
                    }
                    catch { }
                    
                    try
                    {
                        if (elem.Location is LocationPoint lp)
                        {
                            detail["location"] = new Dictionary<string, double>
                            {
                                ["x"] = Math.Round(lp.Point.X, 2),
                                ["y"] = Math.Round(lp.Point.Y, 2),
                                ["z"] = Math.Round(lp.Point.Z, 2)
                            };
                        }
                        else if (elem.Location is LocationCurve lc)
                        {
                            detail["location_type"] = "curve";
                            detail["length"] = Math.Round(lc.Curve.Length, 2);
                        }
                    }
                    catch { }
                    
                    if (includeParams)
                    {
                        var paramValues = new Dictionary<string, object>();
                        try
                        {
                            foreach (Parameter param in elem.Parameters)
                            {
                                if (param == null || !param.HasValue) continue;
                                
                                var value = GetParameterValue(param);
                                if (value != null && param.Definition != null)
                                {
                                    paramValues[param.Definition.Name] = value;
                                }
                            }
                        }
                        catch { }
                        detail["parameters"] = paramValues;
                    }
                    
                    elementDetails.Add(detail);
                }
                
                return ToolResult.Ok($"Retrieved details for {elementDetails.Count} element(s)", 
                    new Dictionary<string, object>
                    {
                        ["count"] = elementDetails.Count,
                        ["elements"] = elementDetails
                    });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting element details: {ex.Message}");
            }
        }
        
        private object GetParameterValue(Parameter param)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.AsString();
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

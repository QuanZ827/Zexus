using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class ColorElementsTool : IAgentTool
    {
        public string Name => "ColorElements";
        
        public string Description => 
            "Apply color overrides to elements in the current view. Supports: " +
            "1) Color specific elements by IDs, " +
            "2) Color elements by condition (e.g., 'Fill Ratio > 40'), " +
            "3) Color elements grouped by parameter value (ColorSplash), " +
            "4) Clear colors from a category. " +
            "Use for visual QAQC, highlighting issues, or categorizing elements.";

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
                        Description = "List of element IDs to color (Mode 1)"
                    },
                    ["category"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Category name for condition/group coloring (Mode 2, 3, 4)"
                    },
                    ["color"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Color name: 'red', 'green', 'blue', 'yellow', 'orange', 'purple', 'cyan', or hex '#FF0000'"
                    },
                    ["condition"] = new PropertySchema
                    {
                        Type = "object",
                        Description = "Condition filter: {\"parameter\": \"Fill Ratio\", \"operator\": \">\", \"value\": 40} (Mode 2)"
                    },
                    ["group_by_parameter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Parameter name to group by for ColorSplash effect (Mode 3)"
                    },
                    ["clear"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "If true, clear all color overrides for the category (Mode 4)"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                var view = doc.ActiveView;
                if (view == null)
                {
                    return ToolResult.Fail("No active view");
                }
                
                List<long> elementIds = null;
                string categoryName = null;
                string colorName = "red";
                Dictionary<string, object> condition = null;
                string groupByParam = null;
                bool clear = false;
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("element_ids", out var ids) && ids != null)
                    {
                        elementIds = new List<long>();
                        var idList = ids as System.Collections.IEnumerable;
                        if (idList != null)
                        {
                            foreach (var id in idList)
                            {
                                elementIds.Add(Convert.ToInt64(id));
                            }
                        }
                    }
                    
                    if (parameters.TryGetValue("category", out var cat))
                        categoryName = cat?.ToString();
                    if (parameters.TryGetValue("color", out var col))
                        colorName = col?.ToString() ?? "red";
                    if (parameters.TryGetValue("condition", out var cond) && cond is Dictionary<string, object> condDict)
                        condition = condDict;
                    if (parameters.TryGetValue("group_by_parameter", out var gbp))
                        groupByParam = gbp?.ToString();
                    if (parameters.TryGetValue("clear", out var clr))
                        clear = Convert.ToBoolean(clr);
                }
                
                if (clear && !string.IsNullOrEmpty(categoryName))
                {
                    return ClearColors(doc, view, categoryName);
                }
                
                if (!string.IsNullOrEmpty(groupByParam) && !string.IsNullOrEmpty(categoryName))
                {
                    return ColorByParameter(doc, view, categoryName, groupByParam);
                }
                
                if (condition != null && !string.IsNullOrEmpty(categoryName))
                {
                    return ColorByCondition(doc, view, categoryName, condition, colorName);
                }
                
                if (elementIds != null && elementIds.Count > 0)
                {
                    return ColorSpecificElements(doc, view, elementIds, colorName);
                }
                
                return ToolResult.Fail("Please provide: element_ids, category+condition, category+group_by_parameter, or category+clear");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error coloring elements: {ex.Message}");
            }
        }
        
        private ToolResult ColorSpecificElements(Document doc, View view, List<long> elementIds, string colorName)
        {
            var color = ParseColor(colorName);
            var settings = CreateOverrideSettings(color);
            
            int colored = 0;
            using (var tx = new Transaction(doc, "Color Elements"))
            {
                tx.Start();
                
                foreach (var idLong in elementIds)
                {
                    var id = RevitCompat.CreateId(idLong);
                    var elem = doc.GetElement(id);
                    if (elem != null)
                    {
                        view.SetElementOverrides(id, settings);
                        colored++;
                    }
                }
                
                tx.Commit();
            }
            
            return ToolResult.Ok($"Colored {colored} elements with {colorName}",
                new Dictionary<string, object>
                {
                    ["colored_count"] = colored,
                    ["color"] = colorName
                });
        }
        
        private ToolResult ColorByCondition(Document doc, View view, string categoryName, Dictionary<string, object> condition, string colorName)
        {
            var category = GetCategoryByName(doc, categoryName);
            if (category == null)
            {
                return ToolResult.Fail($"Category '{categoryName}' not found");
            }
            
            string paramName = condition.TryGetValue("parameter", out var pn) ? pn?.ToString() : null;
            string op = condition.TryGetValue("operator", out var opObj) ? opObj?.ToString() : ">";
            object value = condition.TryGetValue("value", out var val) ? val : null;
            
            if (string.IsNullOrEmpty(paramName))
            {
                return ToolResult.Fail("Condition requires 'parameter' field");
            }
            
            var elements = new FilteredElementCollector(doc)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .ToList();
            
            var matchingIds = new List<ElementId>();
            
            foreach (var elem in elements)
            {
                try
                {
                    var param = elem.LookupParameter(paramName);
                    if (param == null || !param.HasValue) continue;
                    
                    if (EvaluateCondition(param, op, value))
                    {
                        matchingIds.Add(elem.Id);
                    }
                }
                catch { }
            }
            
            if (matchingIds.Count == 0)
            {
                return ToolResult.Ok($"No elements match the condition",
                    new Dictionary<string, object> { ["matching_count"] = 0 });
            }
            
            var color = ParseColor(colorName);
            var settings = CreateOverrideSettings(color);
            
            using (var tx = new Transaction(doc, "Color by Condition"))
            {
                tx.Start();
                
                foreach (var id in matchingIds)
                {
                    view.SetElementOverrides(id, settings);
                }
                
                tx.Commit();
            }
            
            return ToolResult.Ok($"Colored {matchingIds.Count} elements where {paramName} {op} {value}",
                new Dictionary<string, object>
                {
                    ["colored_count"] = matchingIds.Count,
                    ["color"] = colorName,
                    ["element_ids"] = matchingIds.Select(id => RevitCompat.GetIdValue(id)).Take(100).ToList()
                });
        }
        
        private ToolResult ColorByParameter(Document doc, View view, string categoryName, string paramName)
        {
            var category = GetCategoryByName(doc, categoryName);
            if (category == null)
            {
                return ToolResult.Fail($"Category '{categoryName}' not found");
            }
            
            var elements = new FilteredElementCollector(doc)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .ToList();
            
            var groups = new Dictionary<string, List<ElementId>>();
            
            foreach (var elem in elements)
            {
                try
                {
                    var param = elem.LookupParameter(paramName);
                    string value = "(empty)";
                    
                    if (param != null && param.HasValue)
                    {
                        value = GetParameterValueAsString(param);
                        if (string.IsNullOrWhiteSpace(value)) value = "(empty)";
                    }
                    
                    if (!groups.ContainsKey(value))
                    {
                        groups[value] = new List<ElementId>();
                    }
                    groups[value].Add(elem.Id);
                }
                catch { }
            }
            
            var colors = GetColorPalette();
            var colorAssignments = new Dictionary<string, Dictionary<string, object>>();
            int colorIndex = 0;
            
            using (var tx = new Transaction(doc, "Color by Parameter"))
            {
                tx.Start();
                
                foreach (var group in groups.OrderByDescending(g => g.Value.Count))
                {
                    var color = colors[colorIndex % colors.Count];
                    var settings = CreateOverrideSettings(color);
                    
                    foreach (var id in group.Value)
                    {
                        view.SetElementOverrides(id, settings);
                    }
                    
                    colorAssignments[group.Key] = new Dictionary<string, object>
                    {
                        ["count"] = group.Value.Count,
                        ["color_rgb"] = $"RGB({color.Red},{color.Green},{color.Blue})"
                    };
                    
                    colorIndex++;
                }
                
                tx.Commit();
            }
            
            return ToolResult.Ok($"Colored {elements.Count} elements by {paramName} ({groups.Count} groups)",
                new Dictionary<string, object>
                {
                    ["total_elements"] = elements.Count,
                    ["groups_count"] = groups.Count,
                    ["parameter"] = paramName,
                    ["color_assignments"] = colorAssignments
                });
        }
        
        private ToolResult ClearColors(Document doc, View view, string categoryName)
        {
            var category = GetCategoryByName(doc, categoryName);
            if (category == null)
            {
                return ToolResult.Fail($"Category '{categoryName}' not found");
            }
            
            var elements = new FilteredElementCollector(doc)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .ToList();
            
            var defaultSettings = new OverrideGraphicSettings();
            
            using (var tx = new Transaction(doc, "Clear Color Overrides"))
            {
                tx.Start();
                
                foreach (var elem in elements)
                {
                    view.SetElementOverrides(elem.Id, defaultSettings);
                }
                
                tx.Commit();
            }
            
            return ToolResult.Ok($"Cleared color overrides from {elements.Count} elements",
                new Dictionary<string, object>
                {
                    ["cleared_count"] = elements.Count,
                    ["category"] = categoryName
                });
        }
        
        private Category GetCategoryByName(Document doc, string name)
        {
            try
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return cat;
                }
            }
            catch { }
            return null;
        }
        
        private Color ParseColor(string colorName)
        {
            try
            {
                if (colorName.StartsWith("#") && colorName.Length == 7)
                {
                    int r = Convert.ToInt32(colorName.Substring(1, 2), 16);
                    int g = Convert.ToInt32(colorName.Substring(3, 2), 16);
                    int b = Convert.ToInt32(colorName.Substring(5, 2), 16);
                    return new Color((byte)r, (byte)g, (byte)b);
                }
                
                switch (colorName.ToLower())
                {
                    case "red": return new Color(255, 0, 0);
                    case "green": return new Color(0, 200, 0);
                    case "blue": return new Color(0, 100, 255);
                    case "yellow": return new Color(255, 200, 0);
                    case "orange": return new Color(255, 128, 0);
                    case "purple": return new Color(150, 0, 200);
                    case "cyan": return new Color(0, 200, 200);
                    case "pink": return new Color(255, 100, 150);
                    case "gray": return new Color(150, 150, 150);
                    default: return new Color(255, 0, 0);
                }
            }
            catch
            {
                return new Color(255, 0, 0);
            }
        }
        
        private List<Color> GetColorPalette()
        {
            return new List<Color>
            {
                new Color(66, 133, 244),
                new Color(219, 68, 55),
                new Color(244, 180, 0),
                new Color(15, 157, 88),
                new Color(171, 71, 188),
                new Color(255, 112, 67),
                new Color(0, 172, 193),
                new Color(124, 179, 66),
                new Color(255, 167, 38),
                new Color(121, 85, 72),
                new Color(158, 158, 158),
                new Color(236, 64, 122)
            };
        }
        
        private OverrideGraphicSettings CreateOverrideSettings(Color color)
        {
            var settings = new OverrideGraphicSettings();
            settings.SetProjectionLineColor(color);
            settings.SetSurfaceForegroundPatternColor(color);
            settings.SetCutForegroundPatternColor(color);
            return settings;
        }
        
        private bool EvaluateCondition(Parameter param, string op, object value)
        {
            try
            {
                double paramValue;
                double compareValue;
                
                if (param.StorageType == StorageType.Double)
                {
                    paramValue = param.AsDouble();
                }
                else if (param.StorageType == StorageType.Integer)
                {
                    paramValue = param.AsInteger();
                }
                else
                {
                    return false;
                }
                
                if (!double.TryParse(value?.ToString(), out compareValue))
                {
                    return false;
                }
                
                switch (op)
                {
                    case ">": return paramValue > compareValue;
                    case ">=": return paramValue >= compareValue;
                    case "<": return paramValue < compareValue;
                    case "<=": return paramValue <= compareValue;
                    case "=":
                    case "==": return Math.Abs(paramValue - compareValue) < 0.0001;
                    case "!=": return Math.Abs(paramValue - compareValue) >= 0.0001;
                    default: return false;
                }
            }
            catch
            {
                return false;
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

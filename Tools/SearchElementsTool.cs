using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class SearchElementsTool : IAgentTool
    {
        public string Name => "SearchElements";
        
        public string Description => 
            "Search and filter elements by multiple criteria: category, family, type, parameter values, level, " +
            "or match the currently selected element's type. " +
            "Returns element IDs and summary. " +
            "Use when user asks 'find all...', 'how many...', 'list all...', or 'find similar elements'.";

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
                        Description = "Category name to filter (e.g., 'Walls', 'Cable Tray', 'Doors')"
                    },
                    ["family_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Family name to filter (partial match supported)"
                    },
                    ["type_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Type name to filter (partial match supported)"
                    },
                    ["level_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Filter by level name"
                    },
                    ["parameter_filter"] = new PropertySchema
                    {
                        Type = "object",
                        Description = "Filter by parameter: {\"name\": \"Mark\", \"value\": \"A-001\", \"operator\": \"equals|contains|greater|less\"}"
                    },
                    ["match_selected"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "If true, find all elements matching the currently selected element's Family and Type"
                    },
                    ["max_results"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum number of results to return (default: 100)"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                int maxResults = 100;
                string categoryFilter = null;
                string familyFilter = null;
                string typeFilter = null;
                string levelFilter = null;
                bool matchSelected = false;
                Dictionary<string, object> paramFilter = null;
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("max_results", out var mr))
                        maxResults = Convert.ToInt32(mr);
                    if (parameters.TryGetValue("category", out var cat))
                        categoryFilter = cat?.ToString();
                    if (parameters.TryGetValue("family_name", out var fam))
                        familyFilter = fam?.ToString();
                    if (parameters.TryGetValue("type_name", out var typ))
                        typeFilter = typ?.ToString();
                    if (parameters.TryGetValue("level_name", out var lvl))
                        levelFilter = lvl?.ToString();
                    if (parameters.TryGetValue("match_selected", out var ms))
                        matchSelected = Convert.ToBoolean(ms);
                    if (parameters.TryGetValue("parameter_filter", out var pf) && pf is Dictionary<string, object> pfDict)
                        paramFilter = pfDict;
                }
                
                if (matchSelected && uiDoc != null)
                {
                    try
                    {
                        var selection = uiDoc.Selection;
                        if (selection != null)
                        {
                            var selectedIds = selection.GetElementIds();
                            if (selectedIds != null && selectedIds.Count > 0)
                            {
                                var firstId = selectedIds.First();
                                var selectedElem = doc.GetElement(firstId);
                                if (selectedElem != null)
                                {
                                    categoryFilter = selectedElem.Category?.Name;
                                    
                                    if (selectedElem is FamilyInstance fi)
                                    {
                                        familyFilter = fi.Symbol?.Family?.Name;
                                        typeFilter = fi.Symbol?.Name;
                                    }
                                    else
                                    {
                                        var typeId = selectedElem.GetTypeId();
                                        if (typeId != null && typeId != ElementId.InvalidElementId)
                                        {
                                            var elemType = doc.GetElement(typeId);
                                            typeFilter = elemType?.Name;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                return ToolResult.WithWarning("No elements selected. Please select an element first.", 
                                    new Dictionary<string, object>());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return ToolResult.Fail($"Error accessing selection: {ex.Message}");
                    }
                }
                
                FilteredElementCollector collector;
                
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    var category = GetCategoryByName(doc, categoryFilter);
                    if (category != null)
                    {
                        collector = new FilteredElementCollector(doc)
                            .OfCategoryId(category.Id)
                            .WhereElementIsNotElementType();
                    }
                    else
                    {
                        collector = new FilteredElementCollector(doc)
                            .WhereElementIsNotElementType();
                    }
                }
                else
                {
                    collector = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType();
                }
                
                var elements = collector.ToList();
                
                if (!string.IsNullOrEmpty(familyFilter))
                {
                    elements = elements.Where(e => 
                    {
                        if (e is FamilyInstance fi)
                            return fi.Symbol?.Family?.Name?.IndexOf(familyFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                        return false;
                    }).ToList();
                }
                
                if (!string.IsNullOrEmpty(typeFilter))
                {
                    elements = elements.Where(e => 
                    {
                        string typeName = null;
                        if (e is FamilyInstance fi)
                            typeName = fi.Symbol?.Name;
                        else
                        {
                            var typeId = e.GetTypeId();
                            if (typeId != null && typeId != ElementId.InvalidElementId)
                                typeName = doc.GetElement(typeId)?.Name;
                        }
                        return typeName?.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    }).ToList();
                }
                
                if (!string.IsNullOrEmpty(levelFilter))
                {
                    elements = elements.Where(e => 
                    {
                        try
                        {
                            var levelParam = e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                            if (levelParam != null && levelParam.HasValue)
                            {
                                var levelId = levelParam.AsElementId();
                                if (levelId != null && levelId != ElementId.InvalidElementId)
                                {
                                    var level = doc.GetElement(levelId) as Level;
                                    return level?.Name?.IndexOf(levelFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                                }
                            }
                        }
                        catch { }
                        return false;
                    }).ToList();
                }
                
                if (paramFilter != null)
                {
                    elements = ApplyParameterFilter(elements, paramFilter);
                }
                
                var totalCount = elements.Count;
                var resultElements = elements.Take(maxResults).ToList();
                
                var elementSummary = resultElements.Select(e => new Dictionary<string, object>
                {
                    ["id"] = RevitCompat.GetIdValue(e.Id),
                    ["category"] = e.Category?.Name ?? "",
                    ["name"] = e.Name,
                    ["type"] = GetTypeName(doc, e)
                }).ToList();
                
                var typeGroups = resultElements
                    .GroupBy(e => GetTypeName(doc, e))
                    .ToDictionary(g => g.Key ?? "(unknown)", g => g.Count());
                
                return ToolResult.Ok($"Found {totalCount} elements", new Dictionary<string, object>
                {
                    ["total_count"] = totalCount,
                    ["returned_count"] = resultElements.Count,
                    ["truncated"] = totalCount > maxResults,
                    ["by_type"] = typeGroups,
                    ["element_ids"] = resultElements.Select(e => RevitCompat.GetIdValue(e.Id)).ToList(),
                    ["elements"] = elementSummary
                });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error searching elements: {ex.Message}");
            }
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
        
        private string GetTypeName(Document doc, Element e)
        {
            try
            {
                if (e is FamilyInstance fi)
                    return fi.Symbol?.Name;
                
                var typeId = e.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    return doc.GetElement(typeId)?.Name;
            }
            catch { }
            return null;
        }
        
        private List<Element> ApplyParameterFilter(List<Element> elements, Dictionary<string, object> filter)
        {
            if (!filter.TryGetValue("name", out var nameObj) || nameObj == null)
                return elements;
            
            string paramName = nameObj.ToString();
            string op = "equals";
            object filterValue = null;
            
            if (filter.TryGetValue("operator", out var opObj))
                op = opObj?.ToString() ?? "equals";
            if (filter.TryGetValue("value", out var valObj))
                filterValue = valObj;
            
            return elements.Where(e =>
            {
                try
                {
                    var param = e.LookupParameter(paramName);
                    if (param == null || !param.HasValue) return false;
                    
                    var paramValue = GetParameterValueAsString(param);
                    if (paramValue == null) return false;
                    
                    switch (op.ToLower())
                    {
                        case "equals":
                            return paramValue.Equals(filterValue?.ToString(), StringComparison.OrdinalIgnoreCase);
                        case "contains":
                            return paramValue.IndexOf(filterValue?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
                        case "greater":
                            if (double.TryParse(paramValue, out var pv1) && double.TryParse(filterValue?.ToString(), out var fv1))
                                return pv1 > fv1;
                            return false;
                        case "less":
                            if (double.TryParse(paramValue, out var pv2) && double.TryParse(filterValue?.ToString(), out var fv2))
                                return pv2 < fv2;
                            return false;
                        default:
                            return paramValue.Equals(filterValue?.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    return false;
                }
            }).ToList();
        }
        
        private string GetParameterValueAsString(Parameter param)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.AsString();
                    case StorageType.Integer:
                        return param.AsInteger().ToString();
                    case StorageType.Double:
                        return param.AsDouble().ToString();
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

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Analyzes naming patterns for a parameter across a category.
    /// Returns value distribution, prefix groups, and potential anomalies
    /// so the AI can discover naming conventions and spot issues.
    /// </summary>
    public class AnalyzeNamingPatternsTool : IAgentTool
    {
        public string Name => "AnalyzeNamingPatterns";

        public string Description =>
            "Analyze the naming patterns and value distribution for a parameter across a category. " +
            "Returns value counts, auto-detected prefix groups, potential anomalies (rare values), and empty values. " +
            "Use when checking naming conventions, finding inconsistencies, or understanding data patterns. " +
            "The AI should interpret the results to identify naming rules and violations.";

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
                        Description = "Category name (e.g., 'Cable Tray', 'Data Devices'). Required."
                    },
                    ["parameter_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Parameter name to analyze (e.g., 'Mark', 'Comments', 'FBTrunkCode'). Required."
                    },
                    ["group_by_prefix"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Auto-detect and group values by common prefix (default: true)"
                    },
                    ["prefix_length"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Fixed prefix length for grouping. If omitted, auto-detects based on delimiters."
                    },
                    ["show_element_ids"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include element IDs in distribution and anomalies (default: false)"
                    }
                },
                Required = new List<string> { "category", "parameter_name" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                string categoryName = null;
                string parameterName = null;
                bool groupByPrefix = true;
                int prefixLength = -1; // -1 = auto-detect
                bool showIds = false;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("category", out var cat))
                        categoryName = cat?.ToString();
                    if (parameters.TryGetValue("parameter_name", out var pn))
                        parameterName = pn?.ToString();
                    if (parameters.TryGetValue("group_by_prefix", out var gbp))
                        groupByPrefix = Convert.ToBoolean(gbp);
                    if (parameters.TryGetValue("prefix_length", out var pl))
                        prefixLength = Convert.ToInt32(pl);
                    if (parameters.TryGetValue("show_element_ids", out var si))
                        showIds = Convert.ToBoolean(si);
                }

                if (string.IsNullOrEmpty(categoryName) || string.IsNullOrEmpty(parameterName))
                    return ToolResult.Fail("Both category and parameter_name are required");

                // Find category
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
                    return ToolResult.Fail($"Category '{categoryName}' not found");

                // Collect elements
                var elements = new FilteredElementCollector(doc)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType()
                    .ToList();

                if (elements.Count == 0)
                    return ToolResult.WithWarning($"No elements found in category '{categoryName}'",
                        new Dictionary<string, object>());

                // Collect parameter values
                var valueGroups = new Dictionary<string, List<long>>(); // value -> element IDs
                var emptyElementIds = new List<long>();
                int elementsWithParam = 0;
                int elementsWithoutParam = 0;

                foreach (var elem in elements)
                {
                    var param = elem.LookupParameter(parameterName);

                    if (param == null)
                    {
                        elementsWithoutParam++;
                        continue;
                    }

                    elementsWithParam++;

                    string value = null;
                    if (param.HasValue)
                    {
                        value = GetParameterValueAsString(param);
                    }

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        emptyElementIds.Add(RevitCompat.GetIdValue(elem.Id));
                        continue; // Don't include empty values in pattern analysis
                    }

                    if (!valueGroups.ContainsKey(value))
                        valueGroups[value] = new List<long>();
                    valueGroups[value].Add(RevitCompat.GetIdValue(elem.Id));
                }

                if (elementsWithParam == 0)
                    return ToolResult.WithWarning(
                        $"Parameter '{parameterName}' not found on any elements in '{categoryName}'",
                        new Dictionary<string, object> { ["total_elements"] = elements.Count });

                // Build distribution (top 100, sorted by count desc)
                var distribution = valueGroups
                    .OrderByDescending(kvp => kvp.Value.Count)
                    .Take(100)
                    .Select(kvp =>
                    {
                        var item = new Dictionary<string, object>
                        {
                            ["value"] = kvp.Key,
                            ["count"] = kvp.Value.Count,
                            ["percentage"] = Math.Round((double)kvp.Value.Count / elementsWithParam * 100, 1)
                        };
                        if (showIds)
                            item["element_ids"] = kvp.Value.Take(10).ToList();
                        return item;
                    })
                    .ToList();

                // Build prefix groups
                var prefixGroups = new List<Dictionary<string, object>>();
                if (groupByPrefix && valueGroups.Count > 1)
                {
                    var nonEmptyValues = valueGroups.Keys.ToList();
                    prefixGroups = BuildPrefixGroups(nonEmptyValues, valueGroups, prefixLength, showIds);
                }

                // Detect anomalies: values with count=1 when median count > 1
                var anomalies = new List<Dictionary<string, object>>();
                if (valueGroups.Count > 3) // Need enough data to detect anomalies
                {
                    var counts = valueGroups.Values.Select(v => v.Count).OrderBy(c => c).ToList();
                    double medianCount = counts[counts.Count / 2];

                    if (medianCount > 1) // Only flag anomalies if most values appear multiple times
                    {
                        var threshold = Math.Max(1, medianCount * 0.1);
                        anomalies = valueGroups
                            .Where(kvp => kvp.Value.Count <= threshold)
                            .OrderBy(kvp => kvp.Value.Count)
                            .Take(50)
                            .Select(kvp => new Dictionary<string, object>
                            {
                                ["value"] = kvp.Key,
                                ["count"] = kvp.Value.Count,
                                ["element_ids"] = kvp.Value.Take(10).ToList()
                            })
                            .ToList();
                    }
                }

                // Also flag anomalies for prefix groups: values that don't match any major prefix
                if (prefixGroups.Count > 0 && anomalies.Count == 0)
                {
                    // Values in the smallest prefix group(s) may be anomalies
                    var totalInGroups = prefixGroups.Sum(g => (int)g["count"]);
                    anomalies = valueGroups
                        .Where(kvp => kvp.Value.Count == 1)
                        .Take(50)
                        .Select(kvp => new Dictionary<string, object>
                        {
                            ["value"] = kvp.Key,
                            ["count"] = 1,
                            ["element_ids"] = kvp.Value.Take(10).ToList()
                        })
                        .ToList();
                }

                // Find duplicates (values appearing on multiple elements - may indicate copy-paste issues)
                var duplicates = valueGroups
                    .Where(kvp => kvp.Value.Count > 1)
                    .OrderByDescending(kvp => kvp.Value.Count)
                    .Take(20)
                    .Select(kvp =>
                    {
                        var item = new Dictionary<string, object>
                        {
                            ["value"] = kvp.Key,
                            ["count"] = kvp.Value.Count
                        };
                        if (showIds)
                            item["element_ids"] = kvp.Value.Take(10).ToList();
                        return item;
                    })
                    .ToList();

                // Build result
                var resultData = new Dictionary<string, object>
                {
                    ["category"] = categoryName,
                    ["parameter_name"] = parameterName,
                    ["total_elements"] = elements.Count,
                    ["elements_with_parameter"] = elementsWithParam,
                    ["elements_without_parameter"] = elementsWithoutParam,
                    ["empty_values_count"] = emptyElementIds.Count,
                    ["unique_values_count"] = valueGroups.Count,
                    ["distribution"] = distribution,
                    ["distribution_truncated"] = valueGroups.Count > 100
                };

                if (prefixGroups.Count > 0)
                    resultData["prefix_groups"] = prefixGroups;

                if (anomalies.Count > 0)
                    resultData["potential_anomalies"] = anomalies;

                if (duplicates.Count > 0)
                    resultData["duplicated_values"] = duplicates;

                if (emptyElementIds.Count > 0 && showIds)
                    resultData["empty_value_element_ids"] = emptyElementIds.Take(50).ToList();

                string message = $"Analyzed '{parameterName}' across {elements.Count} elements in '{categoryName}': " +
                    $"{valueGroups.Count} unique values, {emptyElementIds.Count} empty, {anomalies.Count} potential anomalies";

                if (anomalies.Count > 0 || emptyElementIds.Count > 0)
                    return ToolResult.WithWarning(message, resultData);

                return ToolResult.Ok(message, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error analyzing naming patterns: {ex.Message}");
            }
        }

        private List<Dictionary<string, object>> BuildPrefixGroups(
            List<string> values, Dictionary<string, List<long>> valueGroups,
            int fixedPrefixLength, bool showIds)
        {
            var groups = new Dictionary<string, List<string>>(); // prefix -> list of full values

            if (fixedPrefixLength > 0)
            {
                // Use fixed prefix length
                foreach (var val in values)
                {
                    string prefix = val.Length >= fixedPrefixLength
                        ? val.Substring(0, fixedPrefixLength)
                        : val;
                    if (!groups.ContainsKey(prefix))
                        groups[prefix] = new List<string>();
                    groups[prefix].Add(val);
                }
            }
            else
            {
                // Auto-detect using delimiter-based grouping
                groups = AutoDetectPrefixGroups(values);
            }

            if (groups.Count <= 1 || groups.Count >= values.Count)
                return new List<Dictionary<string, object>>(); // Grouping not useful

            return groups
                .OrderByDescending(g => g.Value.Count)
                .Take(30)
                .Select(g =>
                {
                    int totalElements = g.Value.Sum(v => valueGroups.ContainsKey(v) ? valueGroups[v].Count : 0);
                    var result = new Dictionary<string, object>
                    {
                        ["prefix"] = g.Key,
                        ["unique_values"] = g.Value.Count,
                        ["count"] = totalElements,
                        ["sample_values"] = g.Value.Take(10).ToList()
                    };
                    return result;
                })
                .ToList();
        }

        private Dictionary<string, List<string>> AutoDetectPrefixGroups(List<string> values)
        {
            // Strategy 1: Find common delimiters
            var delimiters = new[] { '-', '_', '.', ' ' };
            foreach (var delim in delimiters)
            {
                int valuesWithDelim = values.Count(v => v.Contains(delim));
                if (valuesWithDelim < values.Count * 0.5) continue; // Less than 50% have this delimiter

                // Try grouping by segments before the delimiter
                // Try 1 segment, then 2 segments
                for (int segments = 1; segments <= 3; segments++)
                {
                    var groups = new Dictionary<string, List<string>>();
                    foreach (var val in values)
                    {
                        var parts = val.Split(delim);
                        string prefix;
                        if (parts.Length > segments)
                            prefix = string.Join(delim.ToString(), parts.Take(segments)) + delim;
                        else
                            prefix = val;

                        if (!groups.ContainsKey(prefix))
                            groups[prefix] = new List<string>();
                        groups[prefix].Add(val);
                    }

                    int groupCount = groups.Count;
                    if (groupCount >= 2 && groupCount <= Math.Max(20, values.Count / 2))
                        return groups;
                }
            }

            // Strategy 2: Character-based prefix (try lengths 2-6)
            for (int len = 2; len <= Math.Min(6, values.Min(v => v.Length)); len++)
            {
                var groups = new Dictionary<string, List<string>>();
                foreach (var val in values)
                {
                    string prefix = val.Substring(0, len);
                    if (!groups.ContainsKey(prefix))
                        groups[prefix] = new List<string>();
                    groups[prefix].Add(val);
                }

                int groupCount = groups.Count;
                if (groupCount >= 2 && groupCount <= 20)
                    return groups;
            }

            // Fallback: no useful grouping found
            return new Dictionary<string, List<string>>();
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
                        string valStr = param.AsValueString();
                        if (valStr == "Yes" || valStr == "No") return valStr;
                        return param.AsInteger().ToString();
                    case StorageType.Double:
                        return param.AsValueString() ?? Math.Round(param.AsDouble(), 4).ToString();
                    case StorageType.ElementId:
                        return param.AsValueString() ?? "";
                    default:
                        return param.AsValueString() ?? "";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] GetParameterValueAsString error: {ex.Message}");
                return "";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Finds parameter values that are suspiciously similar (likely typos)
    /// using Levenshtein edit distance. Also detects case and whitespace inconsistencies.
    /// Returns pairs for the AI to judge which are actual errors.
    /// </summary>
    public class FindSimilarValuesTool : IAgentTool
    {
        public string Name => "FindSimilarValues";

        public string Description =>
            "Find parameter values that are suspiciously similar to each other (likely typos). " +
            "Uses edit distance to compare all unique values within a category. " +
            "Also detects case-only differences and whitespace issues. " +
            "Returns pairs of similar values with their element counts and IDs for review.";

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
                        Description = "Parameter name to check for typos (e.g., 'Mark'). Required."
                    },
                    ["max_distance"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum edit distance to consider as similar (default: 2, range: 1-3)"
                    },
                    ["check_case"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Also flag values that differ only in letter case (default: true)"
                    },
                    ["check_whitespace"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Also flag values that differ only in whitespace (default: true)"
                    },
                    ["max_pairs"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum number of similar pairs to return (default: 100)"
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
                int maxDistance = 2;
                bool checkCase = true;
                bool checkWhitespace = true;
                int maxPairs = 100;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("category", out var cat))
                        categoryName = cat?.ToString();
                    if (parameters.TryGetValue("parameter_name", out var pn))
                        parameterName = pn?.ToString();
                    if (parameters.TryGetValue("max_distance", out var md))
                        maxDistance = Math.Max(1, Math.Min(3, Convert.ToInt32(md)));
                    if (parameters.TryGetValue("check_case", out var cc))
                        checkCase = Convert.ToBoolean(cc);
                    if (parameters.TryGetValue("check_whitespace", out var cw))
                        checkWhitespace = Convert.ToBoolean(cw);
                    if (parameters.TryGetValue("max_pairs", out var mp))
                        maxPairs = Math.Max(1, Math.Min(500, Convert.ToInt32(mp)));
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

                // Collect elements and their parameter values
                var elements = new FilteredElementCollector(doc)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType()
                    .ToList();

                if (elements.Count == 0)
                    return ToolResult.WithWarning($"No elements found in category '{categoryName}'",
                        new Dictionary<string, object>());

                // Group elements by parameter value
                var valueGroups = new Dictionary<string, List<long>>(); // value -> element IDs

                foreach (var elem in elements)
                {
                    var param = elem.LookupParameter(parameterName);
                    if (param == null || !param.HasValue) continue;

                    string value = GetParameterValueAsString(param);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (!valueGroups.ContainsKey(value))
                        valueGroups[value] = new List<long>();
                    valueGroups[value].Add(RevitCompat.GetIdValue(elem.Id));
                }

                if (valueGroups.Count < 2)
                    return ToolResult.Ok(
                        $"Only {valueGroups.Count} unique value(s) found for '{parameterName}' - no comparison possible",
                        new Dictionary<string, object>
                        {
                            ["category"] = categoryName,
                            ["parameter_name"] = parameterName,
                            ["total_unique_values"] = valueGroups.Count,
                            ["similar_pairs"] = new List<object>(),
                            ["case_differences"] = new List<object>(),
                            ["whitespace_differences"] = new List<object>()
                        });

                var uniqueValues = valueGroups.Keys.OrderBy(v => v).ToList();

                // Find edit distance pairs
                var similarPairs = new List<Dictionary<string, object>>();
                FindEditDistancePairs(uniqueValues, valueGroups, maxDistance, maxPairs, similarPairs);

                // Find case differences
                var caseDifferences = new List<Dictionary<string, object>>();
                if (checkCase)
                    FindCaseDifferences(uniqueValues, valueGroups, caseDifferences);

                // Find whitespace differences
                var whitespaceDifferences = new List<Dictionary<string, object>>();
                if (checkWhitespace)
                    FindWhitespaceDifferences(uniqueValues, valueGroups, whitespaceDifferences);

                int totalIssues = similarPairs.Count + caseDifferences.Count + whitespaceDifferences.Count;

                var resultData = new Dictionary<string, object>
                {
                    ["category"] = categoryName,
                    ["parameter_name"] = parameterName,
                    ["total_unique_values"] = uniqueValues.Count,
                    ["similar_pairs_count"] = similarPairs.Count,
                    ["similar_pairs"] = similarPairs,
                    ["case_differences"] = caseDifferences,
                    ["whitespace_differences"] = whitespaceDifferences
                };

                string message = $"Compared {uniqueValues.Count} unique values for '{parameterName}': " +
                    $"{similarPairs.Count} similar pairs, {caseDifferences.Count} case differences, " +
                    $"{whitespaceDifferences.Count} whitespace differences";

                if (totalIssues > 0)
                    return ToolResult.WithWarning(message, resultData);

                return ToolResult.Ok(message, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error finding similar values: {ex.Message}");
            }
        }

        private void FindEditDistancePairs(
            List<string> values, Dictionary<string, List<long>> valueGroups,
            int maxDistance, int maxPairs, List<Dictionary<string, object>> results)
        {
            for (int i = 0; i < values.Count && results.Count < maxPairs; i++)
            {
                for (int j = i + 1; j < values.Count && results.Count < maxPairs; j++)
                {
                    string a = values[i];
                    string b = values[j];

                    // Quick length filter
                    if (Math.Abs(a.Length - b.Length) > maxDistance) continue;

                    // Skip if only case-different (handled separately)
                    if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) continue;

                    int distance = LevenshteinDistance(a, b, maxDistance);
                    if (distance <= maxDistance)
                    {
                        // Put the more common value as value_a (likely correct)
                        string valA = a, valB = b;
                        if (valueGroups[a].Count < valueGroups[b].Count)
                        {
                            valA = b; valB = a;
                        }

                        results.Add(new Dictionary<string, object>
                        {
                            ["value_a"] = valA,
                            ["value_b"] = valB,
                            ["edit_distance"] = distance,
                            ["value_a_count"] = valueGroups[valA].Count,
                            ["value_b_count"] = valueGroups[valB].Count,
                            ["value_a_element_ids"] = valueGroups[valA].Take(10).ToList(),
                            ["value_b_element_ids"] = valueGroups[valB].Take(10).ToList(),
                            ["difference_type"] = "edit_distance"
                        });
                    }
                }
            }
        }

        private void FindCaseDifferences(
            List<string> values, Dictionary<string, List<long>> valueGroups,
            List<Dictionary<string, object>> results)
        {
            // Group by case-insensitive value
            var caseGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var val in values)
            {
                if (!caseGroups.ContainsKey(val))
                    caseGroups[val] = new List<string>();
                caseGroups[val].Add(val);
            }

            foreach (var group in caseGroups.Where(g => g.Value.Count > 1))
            {
                var variants = group.Value.OrderByDescending(v => valueGroups[v].Count).ToList();
                string mostCommon = variants[0];

                for (int i = 1; i < variants.Count; i++)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["value_a"] = mostCommon,
                        ["value_b"] = variants[i],
                        ["value_a_count"] = valueGroups[mostCommon].Count,
                        ["value_b_count"] = valueGroups[variants[i]].Count,
                        ["value_b_element_ids"] = valueGroups[variants[i]].Take(10).ToList(),
                        ["difference_type"] = "case_only"
                    });
                }
            }
        }

        private void FindWhitespaceDifferences(
            List<string> values, Dictionary<string, List<long>> valueGroups,
            List<Dictionary<string, object>> results)
        {
            // Group by trimmed value
            var trimGroups = new Dictionary<string, List<string>>();
            foreach (var val in values)
            {
                string trimmed = val.Trim();
                if (!trimGroups.ContainsKey(trimmed))
                    trimGroups[trimmed] = new List<string>();
                trimGroups[trimmed].Add(val);
            }

            foreach (var group in trimGroups.Where(g => g.Value.Count > 1))
            {
                var variants = group.Value.OrderByDescending(v => valueGroups[v].Count).ToList();
                // Only report if the variants are actually different (not just case)
                for (int i = 1; i < variants.Count; i++)
                {
                    if (variants[0] == variants[i]) continue; // Same string, skip

                    results.Add(new Dictionary<string, object>
                    {
                        ["value_a"] = variants[0],
                        ["value_b"] = variants[i],
                        ["value_a_count"] = valueGroups[variants[0]].Count,
                        ["value_b_count"] = valueGroups[variants[i]].Count,
                        ["value_b_element_ids"] = valueGroups[variants[i]].Take(10).ToList(),
                        ["difference_type"] = "whitespace"
                    });
                }
            }
        }

        /// <summary>
        /// Compute Levenshtein edit distance with early termination.
        /// Returns maxDistance+1 if distance exceeds threshold.
        /// </summary>
        private static int LevenshteinDistance(string s, string t, int maxDistance)
        {
            int n = s.Length;
            int m = t.Length;

            // Quick rejection by length difference
            if (Math.Abs(n - m) > maxDistance) return maxDistance + 1;

            // Ensure s is the shorter string for space optimization
            if (n > m)
            {
                var temp = s; s = t; t = temp;
                n = s.Length; m = t.Length;
            }

            var previous = new int[n + 1];
            var current = new int[n + 1];

            for (int i = 0; i <= n; i++)
                previous[i] = i;

            for (int j = 1; j <= m; j++)
            {
                current[0] = j;
                int minInRow = j;

                for (int i = 1; i <= n; i++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    current[i] = Math.Min(Math.Min(
                        previous[i] + 1,        // deletion
                        current[i - 1] + 1),    // insertion
                        previous[i - 1] + cost   // substitution
                    );
                    minInRow = Math.Min(minInRow, current[i]);
                }

                // Early termination: if minimum in this row exceeds threshold
                if (minInRow > maxDistance) return maxDistance + 1;

                // Swap rows
                var temp2 = previous;
                previous = current;
                current = temp2;
            }

            return previous[n];
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

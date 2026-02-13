using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class GetWarningsTool : IAgentTool
    {
        public string Name => "GetWarnings";
        
        public string Description => 
            "Get all warnings in the current Revit model. " +
            "Shows warning descriptions, affected elements, and counts by type. " +
            "Use when user asks 'any warnings', 'model issues', 'what problems exist', etc.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["include_element_ids"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include element IDs for each warning (default: true)"
                    },
                    ["max_warnings"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum number of warnings to return (default: 100)"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                bool includeIds = true;
                int maxWarnings = 100;
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("include_element_ids", out var ii))
                        includeIds = Convert.ToBoolean(ii);
                    if (parameters.TryGetValue("max_warnings", out var mw))
                        maxWarnings = Convert.ToInt32(mw);
                }
                
                var warnings = doc.GetWarnings();
                var totalCount = warnings.Count;
                
                if (totalCount == 0)
                {
                    return ToolResult.Ok("No warnings found in the model", 
                        new Dictionary<string, object>
                        {
                            ["total_warnings"] = 0
                        });
                }
                
                var warningGroups = warnings
                    .GroupBy(w => w.GetDescriptionText())
                    .OrderByDescending(g => g.Count())
                    .ToDictionary(g => g.Key, g => g.Count());
                
                var warningDetails = warnings
                    .Take(maxWarnings)
                    .Select(w =>
                    {
                        var detail = new Dictionary<string, object>
                        {
                            ["description"] = w.GetDescriptionText(),
                            ["severity"] = w.GetSeverity().ToString()
                        };
                        
                        if (includeIds)
                        {
                            var failingIds = w.GetFailingElements()?.Select(id => id.Value).ToList() ?? new List<long>();
                            var additionalIds = w.GetAdditionalElements()?.Select(id => id.Value).ToList() ?? new List<long>();
                            
                            detail["failing_element_ids"] = failingIds;
                            detail["additional_element_ids"] = additionalIds;
                            detail["element_count"] = failingIds.Count + additionalIds.Count;
                        }
                        
                        return detail;
                    })
                    .ToList();
                
                var severityCounts = warnings
                    .GroupBy(w => w.GetSeverity().ToString())
                    .ToDictionary(g => g.Key, g => g.Count());
                
                return ToolResult.WithWarning($"Found {totalCount} warnings in the model",
                    new Dictionary<string, object>
                    {
                        ["total_warnings"] = totalCount,
                        ["returned_count"] = warningDetails.Count,
                        ["truncated"] = totalCount > maxWarnings,
                        ["by_severity"] = severityCounts,
                        ["by_type"] = warningGroups,
                        ["warnings"] = warningDetails
                    });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting warnings: {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class GetModelOverviewTool : IAgentTool
    {
        public string Name => "GetModelOverview";
        
        public string Description => 
            "Get comprehensive overview of the current Revit model including: " +
            "document info, element counts by category, levels, and view counts. " +
            "Use this when user asks about model statistics, element counts, " +
            "or general model information.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["include_views"] = new PropertySchema
                    {
                        Type = "boolean",
                        Description = "Include view statistics (default: true)"
                    },
                    ["top_categories"] = new PropertySchema
                    {
                        Type = "integer",
                        Description = "Number of top categories to show (default: 20)"
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                bool includeViews = true;
                int topCategories = 20;
                
                if (parameters != null)
                {
                    if (parameters.TryGetValue("include_views", out var iv))
                        includeViews = Convert.ToBoolean(iv);
                    if (parameters.TryGetValue("top_categories", out var tc))
                        topCategories = Convert.ToInt32(tc);
                }

                var projectInfo = doc.ProjectInformation;

                // ── Per-category counting via collector — never loads all elements into memory ──
                var categoryStats = new Dictionary<string, int>();
                int totalElements = 0;

                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat == null) continue;
                    try
                    {
                        int count = new FilteredElementCollector(doc)
                            .OfCategoryId(cat.Id)
                            .WhereElementIsNotElementType()
                            .GetElementCount();
                        if (count > 0)
                        {
                            categoryStats[cat.Name] = count;
                            totalElements += count;
                        }
                    }
                    catch { }
                }

                // Sort by count descending, take top N
                categoryStats = categoryStats
                    .OrderByDescending(kv => kv.Value)
                    .Take(topCategories)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new Dictionary<string, object>
                    {
                        ["name"] = l.Name,
                        ["elevation_ft"] = Math.Round(l.Elevation, 2),
                        ["elevation_m"] = Math.Round(l.Elevation * 0.3048, 2)
                    })
                    .ToList();

                var viewStats = new Dictionary<string, int>();
                if (includeViews)
                {
                    // Single pass without intermediate ToList()
                    foreach (var elem in new FilteredElementCollector(doc).OfClass(typeof(View)))
                    {
                        var view = (View)elem;
                        if (view.IsTemplate) continue;
                        var typeName = view.ViewType.ToString();
                        if (viewStats.ContainsKey(typeName))
                            viewStats[typeName]++;
                        else
                            viewStats[typeName] = 1;
                    }
                }
                
                var currentView = doc.ActiveView;
                
                var links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(l => l.Name)
                    .ToList();

                return ToolResult.Ok("Model overview retrieved", new Dictionary<string, object>
                {
                    ["document"] = new Dictionary<string, object>
                    {
                        ["title"] = doc.Title,
                        ["path"] = doc.PathName,
                        ["project_name"] = projectInfo?.Name ?? "",
                        ["project_number"] = projectInfo?.Number ?? ""
                    },
                    ["statistics"] = new Dictionary<string, object>
                    {
                        ["total_elements"] = totalElements,
                        ["categories_count"] = categoryStats.Count,
                        ["levels_count"] = levels.Count,
                        ["linked_models_count"] = links.Count
                    },
                    ["elements_by_category"] = categoryStats,
                    ["levels"] = levels,
                    ["views_by_type"] = viewStats,
                    ["current_view"] = new Dictionary<string, object>
                    {
                        ["name"] = currentView?.Name,
                        ["type"] = currentView?.ViewType.ToString()
                    },
                    ["linked_models"] = links
                });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error getting model overview: {ex.Message}");
            }
        }
    }
}

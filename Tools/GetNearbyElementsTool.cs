using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public class GetNearbyElementsTool : IAgentTool
    {
        // Default MEP/low-voltage categories to search
        private static readonly BuiltInCategory[] DefaultCategories = new[]
        {
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_CommunicationDevices,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_SpecialityEquipment
        };

        public string Name => "GetNearbyElements";
        
        public string Description => 
            "Find elements near a reference element based on spatial relationship (above, below, nearby, same room). " +
            "Use current selection as reference, or specify element by name/Mark. " +
            "Returns MEP/low-voltage elements (Cable Tray, Conduit, Data Devices, etc.) by default.";

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["relationship"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Spatial relationship: 'above', 'below', 'nearby', or 'same_room'",
                        Enum = new List<string> { "above", "below", "nearby", "same_room" }
                    },
                    ["distance_feet"] = new PropertySchema
                    {
                        Type = "number",
                        Description = "Search distance in feet (default: 7). For above/below = horizontal tolerance. For nearby = max distance."
                    },
                    ["category_filter"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Comma-separated category names to filter results (e.g., 'Cable Trays,Conduits'). Default: MEP/low-voltage categories."
                    },
                    ["reference_name"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Name or Mark of reference element. If not provided, uses current selection."
                    }
                },
                Required = new List<string> { "relationship" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                // Parse parameters
                string relationship = "nearby";
                double distanceFeet = 7.0;
                string categoryFilterStr = null;
                string referenceName = null;

                if (parameters != null)
                {
                    if (parameters.TryGetValue("relationship", out var relObj))
                        relationship = relObj?.ToString()?.ToLower() ?? "nearby";
                    
                    if (parameters.TryGetValue("distance_feet", out var distObj))
                    {
                        if (distObj is double d) distanceFeet = d;
                        else if (distObj is int i) distanceFeet = i;
                        else if (distObj is long l) distanceFeet = l;
                        else double.TryParse(distObj?.ToString(), out distanceFeet);
                    }
                    
                    if (parameters.TryGetValue("category_filter", out var catObj))
                        categoryFilterStr = catObj?.ToString();
                    
                    if (parameters.TryGetValue("reference_name", out var refObj))
                        referenceName = refObj?.ToString();
                }

                // Get reference elements
                var referenceElements = GetReferenceElements(doc, uiDoc, referenceName);
                
                if (referenceElements.Count == 0)
                {
                    return ToolResult.Fail(
                        "No reference element found. Please either:\n" +
                        "1. Select element(s) in Revit before asking\n" +
                        "2. Specify the element name or Mark value");
                }

                // Parse category filter
                string[] categoryFilter = null;
                if (!string.IsNullOrEmpty(categoryFilterStr))
                {
                    categoryFilter = categoryFilterStr.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToArray();
                }

                // Get categories to search
                var categoriesToSearch = GetCategoriesToSearch(doc, categoryFilter);

                // Collect all nearby elements (with duplicates handling)
                var allFoundElements = new Dictionary<long, NearbyElementInfo>();

                foreach (var refElement in referenceElements)
                {
                    var found = FindNearbyElements(doc, refElement, relationship, distanceFeet, categoriesToSearch);
                    
                    foreach (var elem in found)
                    {
                        if (!allFoundElements.ContainsKey(elem.ElementId))
                        {
                            allFoundElements[elem.ElementId] = elem;
                        }
                        else
                        {
                            // Keep the one with shorter distance
                            if (elem.DistanceFeet < allFoundElements[elem.ElementId].DistanceFeet)
                            {
                                allFoundElements[elem.ElementId] = elem;
                            }
                        }
                    }
                }

                // Remove reference elements from results
                foreach (var refElem in referenceElements)
                {
                    allFoundElements.Remove(RevitCompat.GetIdValue(refElem.Id));
                }

                // Sort by distance
                var sortedResults = allFoundElements.Values
                    .OrderBy(e => e.DistanceFeet)
                    .ToList();

                // Build result data
                var refInfo = referenceElements.Select(e => new Dictionary<string, object>
                {
                    ["id"] = RevitCompat.GetIdValue(e.Id),
                    ["name"] = e.Name,
                    ["category"] = e.Category?.Name ?? "Unknown"
                }).ToList();

                var resultData = new Dictionary<string, object>
                {
                    ["reference_elements"] = refInfo,
                    ["reference_count"] = referenceElements.Count,
                    ["relationship"] = relationship,
                    ["search_distance_feet"] = distanceFeet,
                    ["found_count"] = sortedResults.Count,
                    ["elements"] = sortedResults.Select(e => new Dictionary<string, object>
                    {
                        ["id"] = e.ElementId,
                        ["name"] = e.Name,
                        ["category"] = e.Category,
                        ["family"] = e.Family,
                        ["type"] = e.TypeName,
                        ["distance_feet"] = Math.Round(e.DistanceFeet, 2),
                        ["direction"] = e.Direction
                    }).ToList()
                };

                // Build summary message
                string summary;
                if (sortedResults.Count == 0)
                {
                    summary = $"No elements found {relationship} the reference element(s) within {distanceFeet} feet.";
                }
                else
                {
                    var categoryCounts = sortedResults
                        .GroupBy(e => e.Category)
                        .Select(g => $"{g.Key}: {g.Count()}")
                        .ToList();

                    summary = $"Found {sortedResults.Count} element(s) {relationship} {referenceElements.Count} reference element(s):\n" +
                              string.Join(", ", categoryCounts);
                }

                return ToolResult.Ok(summary, resultData);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error finding nearby elements: {ex.Message}");
            }
        }

        private List<Element> GetReferenceElements(Document doc, UIDocument uiDoc, string referenceName)
        {
            var elements = new List<Element>();

            // Try by name/Mark first if specified
            if (!string.IsNullOrEmpty(referenceName))
            {
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType();

                foreach (var elem in collector)
                {
                    // Skip non-physical elements
                    if (elem.Category == null) continue;
                    
                    // Check element name
                    if (elem.Name.IndexOf(referenceName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        elements.Add(elem);
                        continue;
                    }

                    // Check Mark parameter
                    var markParam = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (markParam != null && !string.IsNullOrEmpty(markParam.AsString()))
                    {
                        if (markParam.AsString().IndexOf(referenceName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            elements.Add(elem);
                        }
                    }
                }

                if (elements.Count > 0)
                    return elements;
            }

            // Fall back to current selection
            if (uiDoc != null)
            {
                var selectedIds = uiDoc.Selection.GetElementIds();
                
                foreach (var id in selectedIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem != null)
                        elements.Add(elem);
                }
            }

            return elements;
        }

        private List<ElementId> GetCategoriesToSearch(Document doc, string[] categoryFilter)
        {
            var categoryIds = new List<ElementId>();

            if (categoryFilter != null && categoryFilter.Length > 0)
            {
                // Use specified categories
                foreach (var catName in categoryFilter)
                {
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat.Name.IndexOf(catName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            categoryIds.Add(cat.Id);
                            break;
                        }
                    }
                }
            }
            
            // If no categories found or none specified, use defaults
            if (categoryIds.Count == 0)
            {
                foreach (var bic in DefaultCategories)
                {
                    categoryIds.Add(RevitCompat.CreateId(bic));
                }
            }

            return categoryIds;
        }

        private List<NearbyElementInfo> FindNearbyElements(
            Document doc, 
            Element refElement, 
            string relationship, 
            double searchDistance,
            List<ElementId> categoryIds)
        {
            var results = new List<NearbyElementInfo>();

            // Get reference bounding box
            var refBB = refElement.get_BoundingBox(null);
            if (refBB == null)
                return results;

            var refCenter = (refBB.Min + refBB.Max) / 2;
            var refMinZ = refBB.Min.Z;
            var refMaxZ = refBB.Max.Z;

            // Create search bounds (expanded by search distance)
            XYZ searchMin, searchMax;

            switch (relationship)
            {
                case "above":
                    // Search above: horizontal tolerance + look up
                    searchMin = new XYZ(
                        refBB.Min.X - searchDistance,
                        refBB.Min.Y - searchDistance,
                        refMaxZ);
                    searchMax = new XYZ(
                        refBB.Max.X + searchDistance,
                        refBB.Max.Y + searchDistance,
                        refMaxZ + 200); // Look up to 200 feet above
                    break;

                case "below":
                    // Search below: horizontal tolerance + look down
                    searchMin = new XYZ(
                        refBB.Min.X - searchDistance,
                        refBB.Min.Y - searchDistance,
                        refMinZ - 200); // Look up to 200 feet below
                    searchMax = new XYZ(
                        refBB.Max.X + searchDistance,
                        refBB.Max.Y + searchDistance,
                        refMinZ);
                    break;

                default: // "nearby" or "same_room"
                    searchMin = new XYZ(
                        refBB.Min.X - searchDistance,
                        refBB.Min.Y - searchDistance,
                        refBB.Min.Z - searchDistance);
                    searchMax = new XYZ(
                        refBB.Max.X + searchDistance,
                        refBB.Max.Y + searchDistance,
                        refBB.Max.Z + searchDistance);
                    break;
            }

            var outline = new Outline(searchMin, searchMax);
            var bbFilter = new BoundingBoxIntersectsFilter(outline);

            // Collect elements
            FilteredElementCollector collector;
            
            if (categoryIds.Count > 0)
            {
                var catFilter = new ElementMulticategoryFilter(categoryIds);
                collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(catFilter)
                    .WherePasses(bbFilter);
            }
            else
            {
                collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(bbFilter);
            }

            // For same_room, get the room of reference element (including linked rooms)
            RoomInfo refRoomInfo = null;
            if (relationship == "same_room")
            {
                refRoomInfo = LinkModelHelper.GetRoomOfElement(doc, refElement);
                if (refRoomInfo == null)
                {
                    // Can't determine room, fall back to nearby
                    relationship = "nearby";
                }
            }

            foreach (var elem in collector)
            {
                if (RevitCompat.GetIdValue(elem.Id) == RevitCompat.GetIdValue(refElement.Id))
                    continue;

                var elemBB = elem.get_BoundingBox(null);
                if (elemBB == null)
                    continue;

                var elemCenter = (elemBB.Min + elemBB.Max) / 2;

                // Calculate distances
                double horizontalDist = Math.Sqrt(
                    Math.Pow(elemCenter.X - refCenter.X, 2) + 
                    Math.Pow(elemCenter.Y - refCenter.Y, 2));
                double verticalDist = elemCenter.Z - refCenter.Z;
                double totalDist = refCenter.DistanceTo(elemCenter);

                string direction = "";
                bool include = false;

                switch (relationship)
                {
                    case "above":
                        // Element must be above reference, within horizontal tolerance
                        if (elemBB.Min.Z >= refMaxZ - 0.5 && horizontalDist <= searchDistance)
                        {
                            include = true;
                            direction = verticalDist < 1 ? "directly above" : 
                                $"{Math.Round(verticalDist, 1)} ft above";
                        }
                        break;

                    case "below":
                        // Element must be below reference, within horizontal tolerance
                        if (elemBB.Max.Z <= refMinZ + 0.5 && horizontalDist <= searchDistance)
                        {
                            include = true;
                            direction = Math.Abs(verticalDist) < 1 ? "directly below" : 
                                $"{Math.Round(Math.Abs(verticalDist), 1)} ft below";
                        }
                        break;

                    case "nearby":
                        if (totalDist <= searchDistance)
                        {
                            include = true;
                            direction = GetDirectionDescription(refCenter, elemCenter);
                        }
                        break;

                    case "same_room":
                        if (refRoomInfo != null)
                        {
                            var elemRoomInfo = LinkModelHelper.GetRoomOfElement(doc, elem);
                            if (elemRoomInfo != null && 
                                elemRoomInfo.RoomId == refRoomInfo.RoomId &&
                                elemRoomInfo.LinkName == refRoomInfo.LinkName)
                            {
                                include = true;
                                direction = $"in {refRoomInfo.DisplayName}";
                                if (refRoomInfo.IsFromLink)
                                {
                                    direction += $" (from {refRoomInfo.LinkName})";
                                }
                            }
                        }
                        break;
                }

                if (include)
                {
                    results.Add(new NearbyElementInfo
                    {
                        ElementId = RevitCompat.GetIdValue(elem.Id),
                        Name = elem.Name,
                        Category = elem.Category?.Name ?? "Unknown",
                        Family = GetFamilyName(elem),
                        TypeName = GetTypeName(doc, elem),
                        DistanceFeet = Math.Round(totalDist, 2),
                        Direction = direction
                    });
                }
            }

            return results;
        }

        // GetRoomOfElement moved to LinkModelHelper

        private string GetDirectionDescription(XYZ from, XYZ to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var dz = to.Z - from.Z;

            var parts = new List<string>();

            if (Math.Abs(dz) > 0.5)
                parts.Add(dz > 0 ? "above" : "below");

            if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
            {
                var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (angle >= -22.5 && angle < 22.5) parts.Add("east");
                else if (angle >= 22.5 && angle < 67.5) parts.Add("northeast");
                else if (angle >= 67.5 && angle < 112.5) parts.Add("north");
                else if (angle >= 112.5 && angle < 157.5) parts.Add("northwest");
                else if (angle >= 157.5 || angle < -157.5) parts.Add("west");
                else if (angle >= -157.5 && angle < -112.5) parts.Add("southwest");
                else if (angle >= -112.5 && angle < -67.5) parts.Add("south");
                else parts.Add("southeast");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "adjacent";
        }

        private string GetFamilyName(Element elem)
        {
            if (elem is FamilyInstance fi)
                return fi.Symbol?.Family?.Name ?? "";
            return "";
        }

        private string GetTypeName(Document doc, Element elem)
        {
            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var type = doc.GetElement(typeId);
                return type?.Name ?? "";
            }
            return "";
        }

        private class NearbyElementInfo
        {
            public long ElementId { get; set; }
            public string Name { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string TypeName { get; set; }
            public double DistanceFeet { get; set; }
            public string Direction { get; set; }
        }
    }
}

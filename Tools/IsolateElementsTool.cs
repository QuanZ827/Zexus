using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    /// <summary>
    /// Atomic tool: temporarily isolate or hide elements in the active view.
    /// Uses Revit's TemporaryHideIsolate mode. User resets via
    /// View tab → "Reset Temporary Hide/Isolate".
    /// </summary>
    public class IsolateElementsTool : IAgentTool
    {
        public string Name => "IsolateElements";

        public string Description =>
            "Temporarily isolate or hide elements in the active view. " +
            "Isolate = show ONLY specified elements (hide everything else). " +
            "Hide = hide ONLY specified elements (show everything else). " +
            "Reset = restore normal view visibility. " +
            "Uses Revit's Temporary Hide/Isolate mode — does not modify the model.";

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
                        Description = "Element IDs to isolate or hide. Not needed for 'reset' mode."
                    },
                    ["mode"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Visibility mode: 'isolate' (show only these), 'hide' (hide only these), 'reset' (restore all). Default: 'isolate'",
                        Enum = new List<string> { "isolate", "hide", "reset" }
                    }
                },
                Required = new List<string>()
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            try
            {
                string mode = "isolate";
                var elementIds = new List<ElementId>();

                if (parameters != null)
                {
                    if (parameters.TryGetValue("mode", out var mObj))
                        mode = mObj?.ToString()?.ToLower() ?? "isolate";

                    if (parameters.TryGetValue("element_ids", out var ids) && ids != null)
                    {
                        var idList = ids as System.Collections.IEnumerable;
                        if (idList != null)
                        {
                            foreach (var id in idList)
                            {
                                try { elementIds.Add(RevitCompat.CreateId(Convert.ToInt64(id))); }
                                catch { }
                            }
                        }
                    }
                }

                var activeView = doc.ActiveView;
                if (activeView == null)
                    return ToolResult.Fail("No active view.");

                if (!activeView.CanUseTemporaryVisibilityModes())
                    return ToolResult.Fail($"View '{activeView.Name}' does not support temporary hide/isolate.");

                // ── Reset mode ──
                if (mode == "reset")
                {
                    using (var trans = new Transaction(doc, "Reset Temporary Visibility"))
                    {
                        trans.Start();
                        activeView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        trans.Commit();
                    }

                    return ToolResult.Ok("Reset temporary hide/isolate — all elements visible again.",
                        new Dictionary<string, object>
                        {
                            ["mode"] = "reset",
                            ["view"] = activeView.Name
                        });
                }

                // ── Isolate or Hide mode ──
                if (elementIds.Count == 0)
                    return ToolResult.Fail("element_ids required for isolate/hide mode.");

                // Validate
                var validIds = new List<ElementId>();
                foreach (var id in elementIds)
                {
                    if (doc.GetElement(id) != null)
                        validIds.Add(id);
                }

                if (validIds.Count == 0)
                    return ToolResult.Fail("None of the provided element IDs are valid.");

                using (var trans = new Transaction(doc, mode == "isolate" ? "Isolate Elements" : "Hide Elements"))
                {
                    trans.Start();

                    if (mode == "hide")
                        activeView.HideElementsTemporary(validIds);
                    else
                        activeView.IsolateElementsTemporary(validIds);

                    trans.Commit();
                }

                string action = mode == "isolate"
                    ? $"Isolated {validIds.Count} element(s) — everything else is hidden"
                    : $"Hidden {validIds.Count} element(s) — everything else is visible";

                return ToolResult.Ok(action, new Dictionary<string, object>
                {
                    ["mode"] = mode,
                    ["element_count"] = validIds.Count,
                    ["view"] = activeView.Name
                });
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Error in isolate/hide: {ex.Message}");
            }
        }
    }
}

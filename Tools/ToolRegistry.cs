using System;
using System.Collections.Generic;
using System.Linq;

namespace Zexus.Tools
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _tools = 
            new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        public void Register(IAgentTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools[tool.Name] = tool;
        }

        public void RegisterAll(params IAgentTool[] tools)
        {
            foreach (var tool in tools)
            {
                Register(tool);
            }
        }

        public IAgentTool GetTool(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _tools.TryGetValue(name, out var tool);
            return tool;
        }

        public IEnumerable<string> GetToolNames() => _tools.Keys.ToList();

        public List<ToolDefinition> GetToolDefinitions()
        {
            return _tools.Values.Select(tool => new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.GetInputSchema()
            }).ToList();
        }

        public List<Dictionary<string, object>> GetToolDefinitionsAsDictionaries()
        {
            return GetToolDefinitions().Select(t => t.ToDictionary()).ToList();
        }

        public bool HasTool(string name) => 
            !string.IsNullOrEmpty(name) && _tools.ContainsKey(name);

        public int Count => _tools.Count;

        public static ToolRegistry CreateDefault()
        {
            var registry = new ToolRegistry();

            // === POC Phase: Lean tool set ===
            // Core tools kept for high-frequency, zero-code-generation operations.
            // Everything else is handled by ExecuteCode (Roslyn dynamic compilation).
            //
            // Tools NOT registered (code preserved, can be re-enabled by data):
            //   GetElementDetailsTool, GetCategoryParametersTool, GetWarningsTool,
            //   CheckMissingParametersTool, GetNearbyElementsTool, GetAllSheetsTool,
            //   GetViewsOnSheetTool, GetElementParametersTool, AnalyzeNamingPatternsTool,
            //   FindSimilarValuesTool, ColorElementsTool, BatchSetParameterTool

            registry.RegisterAll(
                // === Core Query Tools (fast, no code generation cost) ===
                new GetModelOverviewTool(),       // Model stats — always the first thing to call
                new SearchElementsTool(),         // Multi-criteria element search
                new GetParameterValuesTool(),     // Value distribution for a parameter
                new GetSelectionTool(),           // Read user's current Revit selection
                new GetWarningsTool(),            // Model warnings grouped by type

                // === Core Action Tools ===
                new SelectElementsTool(),         // Select + highlight + zoom
                new IsolateElementsTool(),        // Temporary isolate/hide/reset in active view
                new SetElementParameterTool(),    // Single-element write (with confirmation)
                new ActivateViewTool(),           // Open/switch to any view, schedule, sheet

                // === Parameter & Schedule Tools (atomic operations) ===
                new CreateProjectParameterTool(), // Create project parameter + bind to categories
                new AddScheduleFieldTool(),       // Add/remove/list fields in a schedule
                new ModifyScheduleFilterTool(),   // Add/remove/list/clear schedule filters
                new ModifyScheduleSortTool(),     // Add/remove/list/clear schedule sort/group

                // === Output Tools (atomic: each tool does ONE thing) ===
                new ListSheetsTool(),             // List sheets only (number, name, title block)
                new ListViewsTool(),              // List views only (name, type, level)
                new PrintSheetsTool(),            // Print to PDF with full settings control
                new ExportDocumentTool(),         // DWG/DXF/IFC/NWC/Image/CSV export

                // === Universal Tool (handles everything else) ===
                new ExecuteCodeTool()             // Dynamic C# via Roslyn — the power tool
            );

            return registry;
        }
    }
}

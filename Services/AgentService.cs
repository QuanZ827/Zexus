using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zexus.Models;
using Zexus.Tools;

namespace Zexus.Services
{
    public class AgentService : IDisposable
    {
        private ILlmClient _client;
        private readonly ToolRegistry _toolRegistry;
        private Session _currentSession;
        private List<ToolDefinition> _toolDefinitions;
        
        // Session context for progress preservation and resume capability
        private readonly SessionContext _sessionContext = SessionContext.Instance;
        
        // No limit on tool iterations - accuracy is priority
        // private const int MAX_TOOL_ITERATIONS = 5;

        /// <summary>
        /// Builds the system prompt dynamically, injecting the current Revit version
        /// and version-specific API guidance so the LLM generates correct code.
        /// </summary>
        private static string BuildSystemPrompt()
        {
            int revitVersion = App.RevitVersion;
            bool is2025Plus = App.IsRevit2025OrGreater;

            // ── Version-specific API guidance block ──
            string versionBlock;
            if (is2025Plus)
            {
                versionBlock = $@"
## ⚠️ CRITICAL: Revit {revitVersion} API (.NET 8) — Breaking Changes

You are running **Revit {revitVersion}** which uses the **new API**. You MUST follow these rules in ALL ExecuteCode:

### ElementId: Use `Value` (long), NOT `IntegerValue` (removed)
```csharp
// ✅ CORRECT for Revit 2025+
long idValue = element.Id.Value;
var elem = doc.GetElement(new ElementId((long)12345));

// ❌ WRONG — will NOT compile
long idValue = element.Id.IntegerValue;  // IntegerValue does not exist
var elem = doc.GetElement(new ElementId((int)12345)); // int constructor deprecated
```

### ElementId comparison — use Value property
```csharp
// ✅ CORRECT for Revit 2025+
if (element.Id.Value == -1) {{ /* invalid */ }}
if (param.AsElementId().Value != -1) {{ /* has value */ }}

// ❌ WRONG
if (element.Id.IntegerValue == -1) {{ }}
```

### CompoundStructure — use `GetLayers()` (returns IList<CompoundStructureLayer>)
```csharp
// ✅ CORRECT for Revit 2025+
var cs = wall.WallType.GetCompoundStructure();
if (cs != null)
{{
    var layers = cs.GetLayers();
    foreach (var layer in layers)
    {{
        var matId = layer.MaterialId;
        var mat = doc.GetElement(matId) as Material;
        output.AppendLine($""Layer: {{mat?.Name ?? ""<none>""}}, Width: {{layer.Width}}"");
    }}
}}

// ❌ WRONG — GetLayer(int) removed in Revit 2025+
var layer = cs.GetLayer(0);
```

### BuiltInParameter values — cast through ElementId.Value
```csharp
// ✅ CORRECT for Revit 2025+
var levelId = element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
if (levelId != null && levelId.Value != -1)
{{
    var level = doc.GetElement(levelId) as Level;
}}
```

### Parameter.AsInteger() still works but returns int; use carefully with ElementId
```csharp
// ✅ When you need an ElementId from parameter value
var paramElemId = param.AsElementId();  // Preferred way
// NOT: new ElementId(param.AsInteger())  // This may overflow in 2025+
```
";
            }
            else
            {
                versionBlock = $@"
## Revit {revitVersion} API (.NET Framework 4.8)

You are running **Revit {revitVersion}**. Use the classic Revit API:

### ElementId: Use `IntegerValue` (int)
```csharp
// ✅ CORRECT for Revit 2023/2024
int idValue = element.Id.IntegerValue;
var elem = doc.GetElement(new ElementId(12345));
```

### CompoundStructure — use `GetLayers()` (returns IList<CompoundStructureLayer>)
```csharp
var cs = wall.WallType.GetCompoundStructure();
if (cs != null)
{{
    var layers = cs.GetLayers();
    foreach (var layer in layers)
    {{
        var matId = layer.MaterialId;
        var mat = doc.GetElement(matId) as Material;
    }}
}}
```

### ElementId comparison
```csharp
if (element.Id.IntegerValue == -1) {{ /* invalid */ }}
```
";
            }

            return $@"You are a powerful AI Agent for Revit BIM workflows. You can query, analyze, modify, and automate virtually any Revit operation using a combination of predefined tools and dynamic C# code execution.

{versionBlock}

## Architecture: Predefined Tools + ExecuteCode

You have **20 predefined tools** for the most common operations (fast, zero code generation cost), plus **ExecuteCode** — a universal tool that lets you write and run arbitrary C# code inside Revit.

**Decision rule:**
- If a predefined tool can do it → use the predefined tool (faster, more reliable)
- If not → use ExecuteCode (you can do almost anything with the Revit API)

### Query Tools

| Tool | Purpose | When to use |
|------|---------|-------------|
| **GetModelOverview** | Model statistics (element counts, levels, views, links) | Starting a new task, understanding the model |
| **SearchElements** | Find elements by category, family, type, level, parameter values | Before any operation that needs element IDs |
| **GetParameterValues** | Show value distribution for a parameter (unique values + counts) | Checking data quality, understanding what values exist |
| **GetSelection** | Read user's current Revit selection (IDs + category + type) | When user says 'selected elements', 'these', 'what I picked' |
| **GetWarnings** | Model warnings grouped by type and severity | 'Any warnings?', 'model issues', quality checks |

### Action Tools

| Tool | Purpose | When to use |
|------|---------|-------------|
| **SelectElements** | Select + highlight + zoom elements in Revit | Showing search results in the model |
| **IsolateElements** | Temporarily isolate, hide, or reset visibility in active view | Focusing on specific elements in 3D |
| **SetElementParameter** | Modify one parameter on one element (preview=true for dry-run, then confirm) | Single-element edits with preview |
| **ActivateView** | Open/switch to any view by name or ID (plans, schedules, sheets, 3D, sections) | 'Open the schedule', 'switch to Level 1', 'show sheet A101' |

### Parameter & Schedule Tools (WRITE OPERATIONS — confirm before executing)

| Tool | Purpose | When to use |
|------|---------|-------------|
| **CreateProjectParameter** | Create a new project parameter and bind to categories | User asks to add a parameter to the project |
| **CreateSchedule** | Create a new schedule for a Revit category (Walls, Doors, Rooms, etc.) | 'Create a door schedule', 'make a room schedule' |
| **AddScheduleField** | Add/remove/reorder/list fields (columns) in a schedule | Managing schedule columns |
| **FormatScheduleField** | Set column width, alignment, header text, bold/italic per column | 'Make the Name column wider', 'bold the header' |
| **ModifyScheduleFilter** | Add/remove/list/clear filters on a schedule | Controlling which rows appear |
| **ModifyScheduleSort** | Add/remove/list/clear sort/group on a schedule | Controlling row order and grouping |

**Schedule workflow**: Use **CreateSchedule** to create a new schedule for a category (optionally with initial fields). Then use AddScheduleField(mode='list') to see current and available fields. Add fields, filters, sorts, and formatting as needed. Filters and sorts can only reference fields already in the schedule. Use **FormatScheduleField** to adjust column widths, alignment, and font styles. After creating or modifying a schedule, use **ActivateView** to open it for the user.

### ExecuteCode — The Power Tool

ExecuteCode compiles and runs C# code inside the Revit process. Use it for **anything not covered by dedicated tools**:
- Getting element details and parameter lists
- Batch reading/writing parameters across many elements
- Spatial queries (bounding boxes, room containment)
- Complex filtering and aggregation
- Creating/modifying views and schedules beyond field/filter/sort
- Anything the Revit API supports

**Do NOT use ExecuteCode** for operations that have dedicated tools (parameter creation, schedule fields/filters/sorts, printing, exporting). Dedicated tools are more reliable.

## ExecuteCode Reference

You write the **method body only**. It runs inside:
```csharp
public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output)
{{
    // YOUR CODE HERE
}}
```

**Rules:**
1. Use `output.AppendLine(""..."")` to report results (this is what the user sees)
2. End with `return ...;` or `return null;`
3. For modifications, wrap in a Transaction:
   ```csharp
   using (var t = new Transaction(doc, ""Description"")) {{ t.Start(); /* ... */ t.Commit(); }}
   ```
4. If compilation fails, read the errors, fix the code, and retry
5. **Write safety**: describe modifications and get user confirmation before executing

**Available namespaces:**
System, System.Linq, System.Collections.Generic, System.Text,
Autodesk.Revit.DB, Autodesk.Revit.UI, Autodesk.Revit.UI.Selection,
Autodesk.Revit.DB.Architecture, Autodesk.Revit.DB.Mechanical,
Autodesk.Revit.DB.Electrical, Autodesk.Revit.DB.Plumbing

**Common patterns (Revit {revitVersion}):**

Query elements:
```csharp
var walls = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType()
    .ToList();
output.AppendLine($""Found {{walls.Count}} walls"");
```

Read parameters:
```csharp
var param = element.LookupParameter(""Mark"");
if (param != null) output.AppendLine($""Mark = {{param.AsString()}}"");
```

Get all parameters of an element:
```csharp
var elem = doc.GetElement(new ElementId({(is2025Plus ? "(long)" : "")}12345));
foreach (Parameter p in elem.Parameters)
{{
    var val = p.StorageType == StorageType.String ? p.AsString()
            : p.StorageType == StorageType.Double ? p.AsDouble().ToString()
            : p.StorageType == StorageType.Integer ? p.AsInteger().ToString()
            : p.StorageType == StorageType.ElementId ? p.AsElementId().ToString()
            : """";
    output.AppendLine($""{{p.Definition.Name}} = {{val}}"");
}}
```

Get sheets:
```csharp
var sheets = new FilteredElementCollector(doc)
    .OfClass(typeof(ViewSheet))
    .Cast<ViewSheet>()
    .OrderBy(s => s.SheetNumber)
    .ToList();
foreach (var s in sheets)
    output.AppendLine($""{{s.SheetNumber}} - {{s.Name}}"");
```

Batch write with transaction:
```csharp
var elements = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Rooms)
    .WhereElementIsNotElementType()
    .ToList();
using (var t = new Transaction(doc, ""Set Department""))
{{
    t.Start();
    int count = 0;
    foreach (var e in elements)
    {{
        var p = e.LookupParameter(""Department"");
        if (p != null && !p.IsReadOnly) {{ p.Set(""Engineering""); count++; }}
    }}
    t.Commit();
    output.AppendLine($""Updated {{count}} rooms"");
}}
return count;
```

## CRITICAL: Write Safety Rules

**Any operation that modifies the model requires explicit user confirmation.**

Before executing writes (via SetElementParameter, CreateProjectParameter, Schedule tools, or ExecuteCode with Transaction):
1. **Preview first**: call SetElementParameter with `preview: true` to show what would change (no model modification)
2. **Describe** what will change (elements, parameters, old → new values, count)
3. **Wait** for user confirmation (""yes"", ""confirm"", ""go ahead"")
4. **Type parameter warning**: changing a Type parameter affects ALL instances of that type — preview shows affected instance count
5. **Batch operations (>10 elements)**: show summary count, offer to show full list
6. **Never auto-correct** without showing the user what will change first
7. **CreateProjectParameter**: confirm parameter name, type, categories, and binding (instance/type) before creating
8. **Schedule modifications**: confirm field additions, filter criteria, and sort settings before applying

## Search → Select → Isolate Workflow

When the user asks to find and show/highlight elements:
1. `SearchElements` — find by category, parameter, family, type, level
2. `SelectElements` — highlight + zoom to the results
3. `IsolateElements` — (optional) isolate in view if the user wants to see ONLY those elements

Each step is a separate atomic tool. The agent chains them as needed.
To restore normal visibility later: `IsolateElements(mode: ""reset"")`.

## Clarify Scope Before Executing

When the user's request is ambiguous about **scope**, always ask before acting:

- **Sheets**: ""Which sheets? All sheets, a specific list, or a group (e.g. E- electrical, M- mechanical)?""
- **Views**: ""Which views? All views, a specific type (floor plans, sections, 3D), or by name?""
- **Elements**: ""Which elements? All in the model, a specific category, or matching a parameter value?""
- **Export/Print**: ""Which sheets/views to include? Output path?""

Only call a tool after the scope is clear. Do NOT default to ""all"" unless the user explicitly says so.

## Output / Export Operations — Atomic Tools, Confirm Before Executing

Tools are atomic — each does ONE thing. The agent assembles them as needed.

**Discovery tools (call only what you need):**
- `ListSheets` — sheets only (number, name, title block, revision)
- `ListViews` — views only (name, type, level); use view_type and printable_only filters

**Execution tools (always confirm settings with user first):**
- `PrintSheets` — PDF print (sheet_numbers from ListSheets, output_path, paper_size, orientation, color_mode)
- `ExportDocument` — DWG/DXF/IFC/NWC/Image/CSV (format, output_folder, format-specific options)

Do NOT use ExecuteCode for standard output operations — use the dedicated tools.

## Response Guidelines
1. **Be concise** - Clear, direct answers with numbers
2. **Always report results** - After any tool call, summarize what you found
3. **Language** - Match the user's language
4. **Be proactive** - Suggest follow-up actions and insights
5. **If 0 results** - Tell the user and suggest alternatives (different category name, etc.)

## Common Revit Categories
- **Low-Voltage / Telecom**: Cable Tray, Cable Tray Fitting, Conduit, Conduit Fitting, Data Device, Communication Device
- **Electrical**: Electrical Equipment, Electrical Fixtures, Lighting Fixtures
- **Mechanical**: Mechanical Equipment, Ducts, Duct Fittings, Air Terminals
- **Plumbing**: Plumbing Fixtures, Pipes, Pipe Fittings
- **Architectural**: Walls, Doors, Windows, Rooms, Floors, Ceilings
- **Structural**: Structural Columns, Structural Framing, Structural Foundations

## Session Resume
If you see a [SYSTEM: Previous session context] block:
1. DO NOT repeat completed steps
2. Use the cached data
3. Continue from where you left off
4. Briefly acknowledge the resume";
        }

        public event Action<string> OnStreamingText;
        public event Action<string, Dictionary<string, object>> OnToolExecuting;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnProcessingStarted;
        public event Action<string, ToolResult, long> OnToolCompleted;
        public event Action<ChatMessage> OnProcessingCompleted;

        public AgentService()
        {
            Log("AgentService constructor");
            
            _toolRegistry = ToolRegistry.CreateDefault();
            _currentSession = new Session();
            _toolDefinitions = _toolRegistry.GetToolDefinitions();
            
            Log($"ToolRegistry created with {_toolRegistry.Count} tools");
            
            EnsureToolRegistryInitialized();
        }
        
        public void EnsureToolRegistryInitialized()
        {
            if (App.RevitEventHandler != null)
            {
                if (!App.RevitEventHandler.IsRegistryInitialized)
                {
                    App.RevitEventHandler.SetToolRegistry(_toolRegistry);
                    Log("ToolRegistry set on RevitEventHandler");
                }
            }
            else
            {
                Log("WARNING: RevitEventHandler is null");
            }
        }

        public void InitializeClient()
        {
            _client?.Dispose();

            var apiKey = ConfigManager.GetApiKey();
            if (!string.IsNullOrEmpty(apiKey))
            {
                var provider = ConfigManager.GetProvider();
                _client = LlmClientFactory.Create(provider, apiKey, ConfigManager.GetModel(), ConfigManager.Config.MaxTokens);
                Log($"{provider} client initialized");
            }
        }

        public bool IsReady => ConfigManager.IsConfigured();
        public Session CurrentSession => _currentSession;

        public void NewSession(string documentName = null)
        {
            _currentSession = new Session { DocumentName = documentName };
        }

        public async Task<ChatMessage> ProcessMessageAsync(string userMessage, CancellationToken cancellationToken = default)
        {
            Log($"ProcessMessageAsync: {userMessage}");
            
            if (!IsReady)
            {
                return new ChatMessage
                {
                    Role = MessageRole.System,
                    Content = "API key not configured. Please click Settings to enter your API key."
                };
            }

            if (_client == null) InitializeClient();
            
            EnsureToolRegistryInitialized();
            
            if (App.RevitEventHandler == null || !App.RevitEventHandler.IsRegistryInitialized)
            {
                return new ChatMessage
                {
                    Role = MessageRole.System,
                    Content = "Tool registry not initialized. Please restart the add-in."
                };
            }

            // Check if user wants to continue from interrupted task
            string processedMessage = userMessage;
            if (IsContinueCommand(userMessage) && _sessionContext.HasRecoverableInterrupt())
            {
                // Inject context summary for AI to understand previous progress
                var contextSummary = _sessionContext.GenerateContextSummary();
                processedMessage = $"{userMessage}\n\n[SYSTEM: Previous session context - DO NOT repeat completed steps, continue from where you left off]\n{contextSummary}";
                Log($"Injecting session context for resume: {contextSummary.Length} chars");
            }
            else if (!IsContinueCommand(userMessage))
            {
                // New task - start fresh context (but keep tool history)
                _sessionContext.StartTask(userMessage);
            }

            // Track user request for usage analytics
            UsageTracker.StartConversationTurn();
            UsageTracker.RecordUserRequest(userMessage);

            // Notify UI that processing has started (for workspace panel)
            OnProcessingStarted?.Invoke(userMessage);

            var userMsg = new ChatMessage { Role = MessageRole.User, Content = processedMessage };
            _currentSession.AddMessage(userMsg);

            try
            {
                return await ProcessWithToolLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _sessionContext.InterruptTask("User cancelled");
                return new ChatMessage { Role = MessageRole.System, Content = "Request cancelled." };
            }
            catch (Exception ex)
            {
                Log($"ProcessMessageAsync exception: {ex.Message}\n{ex.StackTrace}");

                // Check for Rate Limit error (exception-level, e.g. HttpRequestException with 429)
                bool isRateLimitEx = ex.Message.Contains("429") || ex.Message.Contains("rate_limit")
                    || ex.Message.Contains("overloaded") || ex.Message.Contains("529");
                if (isRateLimitEx)
                {
                    _sessionContext.RecordError("rate_limit", ex.Message, true);
                    return new ChatMessage
                    {
                        Role = MessageRole.System,
                        Content = FormatRateLimitError()
                    };
                }

                _sessionContext.RecordError("api_error", ex.Message, false);
                return new ChatMessage { Role = MessageRole.System, Content = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Check if user message is a "continue" command
        /// </summary>
        private bool IsContinueCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            
            var lower = message.ToLower().Trim();
            var continueKeywords = new[] 
            { 
                "continue", "go on", "keep going",
                "resume", "carry on", "proceed",
                "go ahead", "next", "keep it up"
            };
            
            foreach (var keyword in continueKeywords)
            {
                if (lower.Contains(keyword)) return true;
            }
            
            return false;
        }

        /// <summary>
        /// Format a user-friendly rate limit error message
        /// </summary>
        private string FormatRateLimitError()
        {
            var completedSteps = _sessionContext.CurrentTask?.Steps?.Count ?? 0;
            var cachedDataCount = _sessionContext.DataCache?.Count ?? 0;

            var msg = new System.Text.StringBuilder();
            msg.AppendLine("⚠️ **API Rate Limit**");
            msg.AppendLine();
            msg.AppendLine("API rate limit reached. Please wait a moment before retrying.");
            msg.AppendLine();

            if (completedSteps > 0 || cachedDataCount > 0)
            {
                msg.AppendLine("✅ **Good news: progress has been saved!**");
                msg.AppendLine($"- {completedSteps} steps completed");
                msg.AppendLine($"- {cachedDataCount} data sets cached");
                msg.AppendLine();
                msg.AppendLine("💡 Wait about 1 minute, then send **\"continue\"** to resume from where you left off.");
            }
            else
            {
                msg.AppendLine("💡 Please wait about 1 minute and try again.");
            }

            return msg.ToString();
        }

        private async Task<ChatMessage> ProcessWithToolLoopAsync(CancellationToken cancellationToken)
        {
            var conversationMessages = BuildApiMessages();
            var allToolCalls = new List<ToolCall>();
            var finalText = new System.Text.StringBuilder();
            int iteration = 0;

            while (true)
            {
                // Check cancellation before each iteration
                cancellationToken.ThrowIfCancellationRequested();
                
                iteration++;
                Log($"Tool loop iteration {iteration}");
                
                OnStatusChanged?.Invoke(iteration == 1 ? "Thinking..." : "Processing...");

                // ── API call with automatic retry on rate limit (exponential backoff) ──
                ApiResponse response = null;
                int maxRetries = 3;
                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    response = await _client.SendMessageStreamingAsync(
                        conversationMessages,
                        BuildSystemPrompt(),
                        _toolDefinitions,
                        delta => { finalText.Append(delta); OnStreamingText?.Invoke(delta); },
                        cancellationToken
                    );

                    // If success or non-rate-limit error, break out
                    bool isRateLimit = !response.Success && response.Error != null
                        && (response.Error.Contains("429") || response.Error.Contains("rate_limit")
                            || response.Error.Contains("overloaded") || response.Error.Contains("529"));

                    if (!isRateLimit || attempt == maxRetries)
                        break;

                    // Rate limited — wait with exponential backoff (10s, 30s, 60s)
                    int waitSeconds = attempt == 0 ? 10 : attempt == 1 ? 30 : 60;
                    Log($"Rate limited (attempt {attempt + 1}/{maxRetries}), waiting {waitSeconds}s before retry...");
                    OnStatusChanged?.Invoke($"Rate limited — retrying in {waitSeconds}s...");

                    await System.Threading.Tasks.Task.Delay(waitSeconds * 1000, cancellationToken);
                }

                Log($"API Response: Success={response.Success}, StopReason={response.StopReason}, ToolCalls={response.ToolCalls.Count}, Text={response.Text?.Length ?? 0} chars");

                if (!response.Success)
                {
                    // All retries exhausted for rate limit, or other API error
                    if (response.Error != null && (response.Error.Contains("429") || response.Error.Contains("rate_limit")
                        || response.Error.Contains("overloaded") || response.Error.Contains("529")))
                    {
                        _sessionContext.RecordError("rate_limit", response.Error, true);
                        return new ChatMessage { Role = MessageRole.System, Content = FormatRateLimitError() };
                    }
                    return new ChatMessage { Role = MessageRole.System, Content = $"Error: {response.Error}" };
                }

                // No tool calls - we're done
                if (response.ToolCalls.Count == 0)
                {
                    var assistantMsg = new ChatMessage 
                    { 
                        Role = MessageRole.Assistant, 
                        Content = finalText.ToString(),
                        ToolCalls = allToolCalls.Count > 0 ? allToolCalls : null
                    };
                    _currentSession.AddMessage(assistantMsg);
                    
                    // Mark task as completed
                    _sessionContext.CompleteTask(finalText.ToString());

                    // Notify UI that processing completed (for workspace panel)
                    OnProcessingCompleted?.Invoke(assistantMsg);

                    OnStatusChanged?.Invoke("Complete");
                    return assistantMsg;
                }

                // Execute tools and collect results
                var toolCallResults = new List<ToolCallResult>();

                foreach (var toolUse in response.ToolCalls)
                {
                    // Check cancellation before each tool execution
                    cancellationToken.ThrowIfCancellationRequested();

                    Log($"Executing tool: {toolUse.Name} with input: {JsonSerializer.Serialize(toolUse.Input)}");
                    OnStatusChanged?.Invoke($"Running {toolUse.Name}...");
                    OnToolExecuting?.Invoke(toolUse.Name, toolUse.Input);

                    var toolCall = new ToolCall
                    {
                        Id = toolUse.Id,
                        Name = toolUse.Name,
                        Input = toolUse.Input,
                        Status = ToolCallStatus.Executing
                    };

                    ToolResult result;
                    var toolStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var resultObj = await App.RevitEventHandler.ExecuteToolAsync(toolUse.Name, toolUse.Input);
                        result = resultObj as ToolResult ?? ToolResult.Fail("Tool returned invalid result");
                        toolStopwatch.Stop();
                        Log($"Tool {toolUse.Name} completed: Success={result.Success}, Message={result.Message}");

                        // Track usage data with error classification
                        string errCategory = null;
                        string errSnippet = null;
                        if (!result.Success)
                        {
                            errSnippet = result.Message;
                            if (result.Data != null && result.Data.ContainsKey("failure_type"))
                                errCategory = result.Data["failure_type"]?.ToString();
                            else if (result.Message?.StartsWith("Compilation failed") == true)
                                errCategory = "compilation";
                            else if (result.Message?.StartsWith("Runtime error") == true)
                                errCategory = "runtime";
                            else
                                errCategory = "tool_error";
                        }
                        UsageTracker.RecordToolCall(toolUse.Name, toolUse.Input, result.Success,
                            errorCategory: errCategory,
                            errorSnippet: errSnippet,
                            resultMessage: result.Message,
                            durationMs: toolStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        toolStopwatch.Stop();
                        Log($"Tool execution exception: {ex.Message}");
                        result = ToolResult.Fail($"Tool execution error: {ex.Message}");

                        UsageTracker.RecordToolCall(toolUse.Name, toolUse.Input, false,
                            errorType: ex.GetType().Name,
                            errorCategory: "exception",
                            errorSnippet: ex.Message,
                            durationMs: toolStopwatch.ElapsedMilliseconds);
                    }

                    toolCall.Result = result;
                    toolCall.Status = result.Success ? ToolCallStatus.Completed : ToolCallStatus.Failed;
                    allToolCalls.Add(toolCall);

                    OnToolCompleted?.Invoke(toolUse.Name, result, toolStopwatch.ElapsedMilliseconds);

                    _sessionContext.RecordToolCall(toolUse.Name, toolUse.Input, result.Data, result.Success);
                    _sessionContext.AddStep($"{toolUse.Name}", result.Success ? "completed" : "failed");
                    _sessionContext.UpdateCurrentStep(result.Success ? "completed" : "failed", result.Message);

                    if (result.Success && result.Data != null)
                    {
                        if (result.Data.ContainsKey("elements") || result.Data.ContainsKey("element_ids"))
                            _sessionContext.CacheData($"{toolUse.Name}_result", result.Data);
                        if (result.Data.ContainsKey("sheets"))
                            _sessionContext.CacheData("sheets_data", result.Data);
                        if (result.Data.ContainsKey("parameters"))
                        {
                            var elemId = toolUse.Input != null && toolUse.Input.ContainsKey("element_id")
                                ? toolUse.Input["element_id"]?.ToString() ?? "unknown"
                                : "unknown";
                            _sessionContext.CacheData($"params_{elemId}", result.Data);
                        }
                    }

                    // Serialize result for provider message formatting
                    var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = false
                    });

                    toolCallResults.Add(new ToolCallResult
                    {
                        ToolCallId = toolUse.Id,
                        ToolName = toolUse.Name,
                        Input = toolUse.Input,
                        ResultJson = resultJson
                    });
                }

                // Provider formats assistant + tool result messages in its own wire format
                conversationMessages.Add(_client.FormatAssistantMessage(response.Text, response.ToolCalls));
                var resultMessages = _client.FormatToolResultMessages(toolCallResults);
                foreach (var msg in resultMessages)
                    conversationMessages.Add(msg);

                // Clear the text buffer for next response
                finalText.Clear();
            }
            // Loop only exits via return (no tool calls) or exception (cancellation/error)
        }

        /// <summary>
        /// Builds the API message list with a sliding window to prevent token exhaustion.
        /// Keeps the first user message (for context) + the most recent N message pairs.
        /// Estimates ~4 chars per token; reserves space for system prompt (~4K tokens)
        /// and output (max_tokens). The input budget is roughly 200K - system - output.
        /// </summary>
        private List<Dictionary<string, object>> BuildApiMessages()
        {
            var allMessages = new List<Dictionary<string, object>>();

            foreach (var msg in _currentSession.Messages)
            {
                if (msg.Role == MessageRole.System) continue;

                allMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = msg.Role == MessageRole.User ? "user" : "assistant",
                    ["content"] = msg.Content ?? ""
                });
            }

            // Sliding window: keep total estimated tokens under budget
            // Budget = 200K context - ~5K system prompt - 16K max_tokens = ~179K input tokens
            // Use conservative 3 chars/token estimate
            const int maxInputTokens = 150_000;
            const int charsPerToken = 3;
            const int maxInputChars = maxInputTokens * charsPerToken;

            if (allMessages.Count <= 2)
                return allMessages; // Nothing to trim

            // Calculate total character count
            int totalChars = 0;
            foreach (var m in allMessages)
                totalChars += ((string)m["content"]).Length;

            if (totalChars <= maxInputChars)
                return allMessages; // Fits within budget

            // Trim from the middle: keep first message + as many recent messages as fit
            var result = new List<Dictionary<string, object>>();

            // Always keep the first user message for original context
            var firstMsg = allMessages[0];
            int budget = maxInputChars - ((string)firstMsg["content"]).Length;
            result.Add(firstMsg);

            // Add a context-trimmed notice
            result.Add(new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = "[Earlier conversation history was trimmed to stay within context limits]"
            });
            result.Add(new Dictionary<string, object>
            {
                ["role"] = "assistant",
                ["content"] = "Understood. I'll continue based on the recent conversation context."
            });
            budget -= 200; // Approximate tokens for the notice pair

            // Walk backwards from most recent, collecting messages that fit
            var recentMessages = new List<Dictionary<string, object>>();
            for (int i = allMessages.Count - 1; i >= 1; i--)
            {
                int msgLen = ((string)allMessages[i]["content"]).Length;
                if (budget - msgLen < 0) break;
                budget -= msgLen;
                recentMessages.Insert(0, allMessages[i]);
            }

            result.AddRange(recentMessages);

            Log($"BuildApiMessages: trimmed {allMessages.Count} messages to {result.Count} (saved ~{(totalChars - maxInputChars) / charsPerToken} tokens)");
            return result;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
        
        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[Zexus] {DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }
}

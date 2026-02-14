# Architecture

## System Overview

```
User (Chat)
     │
     ▼
┌──────────────────────────────────────────────────────┐
│                    ChatWindow (WPF)                    │
│  Split View: Conversation Panel + Workspace Panel     │
│  Streaming text, tool call cards, thinking chain      │
└─────────────────────┬────────────────────────────────┘
                      │
                      ▼
┌──────────────────────────────────────────────────────┐
│                   AgentService                        │
│  - Builds dynamic System Prompt (version-aware)       │
│  - Manages conversation history (sliding window)      │
│  - Agentic tool loop (no iteration limit)             │
│  - Rate limit auto-retry (exponential backoff)        │
│  - Session context for resume after interruption      │
└───────────┬──────────────────────┬───────────────────┘
            │                      │
            ▼                      ▼
┌────────────────────┐  ┌──────────────────────────────┐
│    ILlmClient      │  │       ToolRegistry            │
│  ┌───────────────┐ │  │  20 registered IAgentTools    │
│  │ AnthropicClient│ │  │  + ExecuteCode (Roslyn)       │
│  │ OpenAiClient   │ │  │                              │
│  │ GeminiClient   │ │  │  RegisterAll() at startup    │
│  └───────────────┘ │  │  GetTool(name) at runtime     │
└────────────────────┘  └──────────────┬───────────────┘
                                       │
                                       ▼
                        ┌──────────────────────────────┐
                        │    RevitEventHandler          │
                        │  ExternalEvent + queue        │
                        │  Executes tools on Revit      │
                        │  main thread (thread-safe)    │
                        └──────────────┬───────────────┘
                                       │
                                       ▼
                        ┌──────────────────────────────┐
                        │         Revit API             │
                        │  Document, UIDocument,        │
                        │  FilteredElementCollector,    │
                        │  Transaction, View, Export    │
                        └──────────────────────────────┘
```

## Key Design Decisions

### 1. Atomic Tool Principle

Every tool does **one thing**. The AI agent composes tools to solve complex tasks.

**Why:**
- LLMs are better at selecting from a menu of simple tools than orchestrating complex multi-step APIs
- Atomic tools are individually testable, debuggable, and replaceable
- The AI can chain tools in novel ways the developer never anticipated

**Example — "Create a door schedule sorted by level":**
```
1. CreateSchedule(category="Doors")
2. AddScheduleField(mode="add", field_name="Level")
3. AddScheduleField(mode="add", field_name="Family and Type")
4. AddScheduleField(mode="add", field_name="Mark")
5. ModifyScheduleSort(mode="add", field_name="Level")
6. ActivateView(view_name="Door Schedule")
```

Six atomic tool calls, composed by the AI from a single natural language instruction.

### 2. Predefined Tools + ExecuteCode Hybrid

```
┌─────────────────────────────────────────────┐
│  User Request                                │
│  "How many walls are on Level 1?"            │
└─────────────────┬───────────────────────────┘
                  │
                  ▼
          ┌───────────────┐
          │ Can a          │
          │ predefined     │──── Yes ──→ SearchElements(category="Walls", level="Level 1")
          │ tool do it?    │             Zero token cost for code generation
          └───────┬───────┘
                  │ No
                  ▼
          ExecuteCode(code="...")
          LLM generates C#, Roslyn compiles, executes in Revit
          ~500-2000 tokens, possible retry
```

**Decision rule — when to make something an atomic tool vs. leave it to ExecuteCode:**

| Factor | Atomic Tool | ExecuteCode |
|--------|-------------|-------------|
| Frequency | >10% of sessions | <10% of sessions |
| ExecuteCode success rate | <70% | >90% |
| API complexity | Hidden constraints, multi-step | Simple 1-2 API calls |
| UIDocument needed | Yes (selection, view change) | Usually no |
| Token cost per use | 0 (predefined) | 500-2000 |

### 3. Version-Aware System Prompt

Revit 2025 introduced breaking API changes (`ElementId.IntegerValue` → `ElementId.Value`). Rather than maintaining separate prompts:

```csharp
// AgentService.cs
private static string BuildSystemPrompt()
{
    int revitVersion = App.RevitVersion;  // Detected at startup
    bool is2025Plus = App.IsRevit2025OrGreater;

    // Inject version-specific API guidance
    if (is2025Plus)
        // ElementId.Value (long), GetLayers(), new ElementId((long)xxx)
    else
        // ElementId.IntegerValue (int), classic API
}
```

The system prompt is built **once per API call** with the correct Revit version injected. This means the LLM always generates code that compiles on the running Revit version.

### 4. Thread Safety via ExternalEvent

Revit's API is strictly single-threaded. All tool execution goes through:

```
AgentService (background thread)
    → RevitEventHandler.ExecuteToolAsync(name, params)
        → ExternalEvent.Raise()  (enqueue)
        → Revit main thread picks up the event
        → IAgentTool.Execute(doc, uiDoc, params)
        → Result returned via TaskCompletionSource
    ← awaits result
```

This architecture ensures:
- UI remains responsive during LLM streaming
- All Revit API calls happen on the correct thread
- No transaction conflicts between tools

### 5. Multi-LLM Provider Abstraction

```
ILlmClient
├── AnthropicClient   (Claude — native tool_use format)
├── OpenAiClient      (GPT-4o — function_call format)
└── GeminiClient      (Gemini — functionCall format)
```

Each client translates between the internal tool format and the provider's wire format. The `AgentService` is provider-agnostic — it works with `ILlmClient.SendMessageStreamingAsync()` and `ILlmClient.FormatToolResultMessages()`.

Switching providers at runtime requires only updating `ConfigManager` — no restart needed.

## Directory Structure

```
Zexus/
├── App.cs                      # Revit add-in entry point, version detection
├── Zexus.csproj                # Dual-target: net48 + net8.0-windows
├── Zexus.addin                 # Revit add-in manifest
│
├── Services/
│   ├── AgentService.cs         # Core agent loop, dynamic system prompt
│   ├── ILlmClient.cs           # Provider interface
│   ├── AnthropicClient.cs      # Claude API client
│   ├── OpenAiClient.cs         # OpenAI API client
│   ├── GeminiClient.cs         # Google Gemini API client
│   ├── LlmClientFactory.cs     # Provider factory
│   ├── CodeExecutionService.cs  # Roslyn dynamic compilation
│   ├── ConfigManager.cs        # Settings persistence
│   ├── SessionContext.cs       # Progress tracking for resume
│   └── UsageTracker.cs         # Local analytics (JSONL)
│
├── Tools/
│   ├── IAgentTool.cs           # Tool interface
│   ├── ToolRegistry.cs         # Tool registration + discovery
│   ├── RevitCompat.cs          # Cross-version ElementId helpers
│   ├── LinkModelHelper.cs      # Linked model utilities
│   ├── [20 registered tools]   # See docs/TOOLS.md
│   └── [11 reserve tools]      # Code preserved, not registered
│
├── Models/
│   ├── ChatMessage.cs          # Conversation messages
│   ├── ToolResult.cs           # Tool execution results
│   ├── ApiResponse.cs          # LLM API response model
│   └── WorkspaceModels.cs      # UI workspace state
│
├── Views/
│   ├── ChatWindow.xaml         # WPF layout (split view)
│   └── ChatWindow.xaml.cs      # UI logic, streaming, tool cards
│
├── Revit/
│   └── RevitEventHandler.cs    # Thread-safe Revit API bridge
│
├── Installer/
│   ├── Zexus.wxs              # WiX v4 MSI definition
│   ├── build-msi.bat          # One-click MSI build script
│   └── build-installer.bat    # Inno Setup alternative
│
└── docs/
    ├── INSTALLATION.md        # Setup guide
    ├── ARCHITECTURE.md        # This file
    └── TOOLS.md               # Complete tool reference
```

## Data Flow

```
User message
    → AgentService.ProcessMessageAsync()
    → BuildSystemPrompt() (version-aware)
    → BuildApiMessages() (sliding window to fit context)
    → ILlmClient.SendMessageStreamingAsync()
        ← Streaming text (displayed in real-time)
        ← Tool calls (if any)
    → For each tool call:
        → RevitEventHandler.ExecuteToolAsync()
        → IAgentTool.Execute(doc, uiDoc, params)
        → ToolResult (success/fail/warning + data)
        → UsageTracker.RecordToolCall()
    → Format tool results → send back to LLM
    → Loop until LLM responds with text only (no more tool calls)
    → Final response displayed to user
```

There is **no hard limit** on tool iterations. The agent continues until it has a complete answer. This prioritizes accuracy over token savings.

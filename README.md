# Zexus — AI Agent Platform for Autodesk Revit

**An open-source Revit add-in that lets you query, analyze, and automate BIM workflows using natural language.**

Zexus integrates LLM-powered AI directly into Revit, giving BIM engineers an intelligent assistant that understands your model, executes Revit API operations, and chains multiple tools together autonomously — all through a chat interface.

---

## Why Zexus?

Traditional Revit automation requires writing Dynamo scripts or C# add-ins for every task. When requirements change, you rewrite code.

Zexus takes a different approach:

```
Traditional:  Hardcoded rules  →  Single function  →  Change requirements = rewrite code
Zexus:        Natural language  →  AI-composed tools  →  Change requirements = change conversation
```

**Ask in plain English (or any language), and the AI agent figures out which tools to use.**

## Key Capabilities

- **Query your model** — Element counts, parameter values, warnings, spatial relationships
- **Find and highlight elements** — Search by category, family, type, level, or parameter value, then select/isolate in view
- **Modify parameters** — Single edits with mandatory user confirmation
- **Create and manage schedules** — Create schedules, add/remove/reorder columns, format widths/alignment/fonts, filter, sort
- **Navigate views** — Open any floor plan, schedule, sheet, section, or 3D view by name
- **Create project parameters** — Bind new parameters to categories via natural language
- **Print and export** — PDF print, DWG/DXF/IFC/NWC/image/CSV export
- **Execute arbitrary C# code** — When predefined tools aren't enough, the AI writes and runs Revit API code on the fly via Roslyn

## Multi-LLM Support

| Provider | Models | Status |
|----------|--------|--------|
| **Anthropic** | Claude Sonnet, Opus, Haiku | Fully supported |
| **OpenAI** | GPT-4o, GPT-4o-mini | Fully supported |
| **Google** | Gemini 2.5 Pro, Flash | Fully supported |

Switch providers at runtime — no restart needed. Bring your own API key.

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    AI Agent (LLM)                             │
│   Understand intent → Select tools → Compose → Report        │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│                   20 Atomic Tools                             │
│  Query(5) | Action(4) | Schedule(6) | Output(4) | Code(1)    │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│                      Revit API                                │
│  FilteredElementCollector, Transaction, Export, UIDocument...  │
└──────────────────────────────────────────────────────────────┘
```

**Design principles:**
- **Atomic** — Each tool does one thing. The AI assembles them.
- **Composable** — Tools chain freely to solve complex tasks
- **Safe** — All model modifications require explicit user confirmation
- **Extensible** — Add new tools by implementing a single interface
- **Version-aware** — Auto-detects Revit version, injects correct API guidance into the LLM prompt

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full design document.

## Supported Revit Versions

| Revit Version | Runtime | Status |
|---------------|---------|--------|
| 2023 | .NET Framework 4.8 | Supported |
| 2024 | .NET Framework 4.8 | Supported |
| 2025 | .NET 8 | Supported |
| 2026 | .NET 8 | Supported |

Single codebase, dual target — built from one `csproj` with conditional compilation.

## Quick Start

### Install from Release

1. Download the latest `Zexus_Setup_v*.msi` from [Releases](https://github.com/QuanZ827/Zexus/releases)
2. Run the installer — no admin required, installs per-user
3. Open Revit → find the **Zexus** tab → click **Zexus Agent**
4. Enter your API key in Settings (choose Anthropic, OpenAI, or Google)
5. Start chatting

> Windows SmartScreen may show a warning — click "More info" → "Run anyway".

### Build from Source

```bash
git clone https://github.com/QuanZ827/Zexus.git
cd Zexus

dotnet build -f net48 --configuration Release          # Revit 2023/2024
dotnet build -f net8.0-windows --configuration Release  # Revit 2025/2026
```

See [docs/INSTALLATION.md](docs/INSTALLATION.md) for full setup instructions.

## Tool Reference

### Query Tools (5)
| Tool | Purpose |
|------|---------|
| `GetModelOverview` | Model statistics — element counts, levels, views, linked models |
| `SearchElements` | Find elements by category, family, type, level, parameter value |
| `GetParameterValues` | Parameter value distribution (unique values + counts) |
| `GetSelection` | Read user's current Revit selection |
| `GetWarnings` | Model warnings grouped by type and severity |

### Action Tools (4)
| Tool | Purpose |
|------|---------|
| `SelectElements` | Highlight + zoom to elements in Revit |
| `IsolateElements` | Temporarily isolate/hide/reset visibility in view |
| `SetElementParameter` | Modify a parameter value (requires confirmation) |
| `ActivateView` | Open/switch to any view, schedule, sheet, 3D, section |

### Schedule Tools (6)
| Tool | Purpose |
|------|---------|
| `CreateSchedule` | Create a new schedule for any Revit category |
| `AddScheduleField` | Add/remove/reorder/list schedule columns |
| `FormatScheduleField` | Column width, alignment, header text, bold/italic |
| `CreateProjectParameter` | Create and bind a new project parameter |
| `ModifyScheduleFilter` | Add/remove/list/clear schedule filters |
| `ModifyScheduleSort` | Add/remove/list/clear schedule sort/group |

### Output Tools (4)
| Tool | Purpose |
|------|---------|
| `ListSheets` | List sheets (number, name, title block) |
| `ListViews` | List views (name, type, level, printable) |
| `PrintSheets` | Print to PDF with full settings control |
| `ExportDocument` | Export DWG/DXF/IFC/NWC/PNG/JPG/TIFF/CSV |

### Universal Tool (1)
| Tool | Purpose |
|------|---------|
| `ExecuteCode` | Dynamic C# execution via Roslyn — handles anything the Revit API supports |

See [docs/TOOLS.md](docs/TOOLS.md) for full parameter reference and example prompts.

## Safety Model

### Write Operations

All model modifications require explicit user confirmation:

1. **Confirmation required** — Write operations show what will change and wait for user approval
2. **Type parameter warnings** — Changing a Type parameter affects ALL instances of that type
3. **Batch operation preview** — For >10 elements, a summary is shown before execution
4. **No auto-corrections** — The AI always shows proposed changes before applying them

### ⚠️ ExecuteCode Safety

The `ExecuteCode` tool compiles and runs arbitrary C# code inside the Revit process via Roslyn. This is the platform's most powerful capability — and its most significant risk surface.

**What you should know:**
- ExecuteCode is **always enabled** when Zexus is loaded. There is no separate toggle.
- Code runs with **full Revit API access** — it can read, modify, and delete elements.
- The AI agent is instructed to **request confirmation before write operations**, but this is an LLM behavioral constraint, not a hard technical barrier.
- All code execution is logged locally for audit purposes.

**Recommendations:**
- **Always save your project** before using Zexus for automation tasks
- **Review the AI's proposed changes** before confirming write operations
- **Use Revit's Undo** (`Ctrl+Z`) immediately if an operation produces unexpected results
- For critical production models, test on a copy first

## Extending Zexus

Adding a new tool takes three steps:

1. **Create a tool** — Implement `IAgentTool` in `Tools/`
2. **Register it** — Add to `ToolRegistry.CreateDefault()`
3. **Guide the AI** — Update the system prompt in `AgentService.cs`

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full development guide.

## Roadmap

- [ ] Plugin architecture for domain-specific tool packs
- [ ] Multi-model / linked model operations
- [ ] Visual diff for before/after parameter changes
- [ ] Community tool gallery
- [ ] Usage analytics dashboard

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Disclaimer

This is an **independent, personal open-source project**. It is not affiliated with, endorsed by, or sponsored by any employer, organization, or company. See [DISCLAIMER.md](DISCLAIMER.md) for full details.

**Use at your own risk.** Always save your Revit project before using AI-assisted automation. See [SECURITY.md](SECURITY.md) for security practices.

## License

[Apache License 2.0](LICENSE) — Free to use, modify, and distribute. See LICENSE for full terms.

---

**Built by [Zhequan Zhang](https://github.com/QuanZ827)**

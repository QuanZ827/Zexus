# Contributing to Zexus

Thank you for your interest in contributing to Zexus! This document explains how to get involved.

## How to Contribute

### Reporting Issues

- Use [GitHub Issues](https://github.com/QuanZ827/Zexus/issues) to report bugs or request features
- Check existing issues before creating a new one
- Use the provided issue templates when available
- Include your Revit version, Zexus version, and steps to reproduce

### Submitting Pull Requests

1. **Fork** the repository
2. **Create a branch** from `main`:
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make your changes** following the code style guidelines below
4. **Build and test** both targets:
   ```bash
   dotnet build -f net48 --configuration Release
   dotnet build -f net8.0-windows --configuration Release
   ```
5. **Commit** with a clear message:
   ```bash
   git commit -m "Add: brief description of what changed"
   ```
6. **Push** and open a Pull Request against `main`

### PR Guidelines

- Keep PRs focused — one feature or fix per PR
- Describe what changed and why in the PR description
- Ensure both `net48` and `net8.0-windows` targets build with 0 errors
- If adding a new tool, include it in the tool registry and update the system prompt

## Development Setup

### Prerequisites

- **Visual Studio 2022** (or later) with .NET desktop development workload
- **.NET 8 SDK** (for net8.0-windows target)
- **.NET Framework 4.8 Developer Pack** (for net48 target)
- **Revit 2023, 2024, 2025, or 2026** (at least one version for testing)
- **Anthropic API key** (for runtime testing)

### Building

```bash
# Clone the repository
git clone https://github.com/QuanZ827/Zexus.git
cd Zexus

# Build both targets
dotnet build -f net48 --configuration Release
dotnet build -f net8.0-windows --configuration Release
```

See [docs/INSTALLATION.md](docs/INSTALLATION.md) for detailed setup instructions.

### Project Structure

```
Zexus/
├── App.cs                  # Revit Add-in entry point
├── Services/               # Core services (AgentService, AnthropicClient, etc.)
├── Tools/                  # All 17 agent tools (IAgentTool implementations)
├── Models/                 # Data models (ChatMessage, ToolResult, etc.)
├── Views/                  # WPF UI (ChatWindow)
├── Revit/                  # Revit API thread safety (ExternalEventHandler)
└── Installer/              # WiX MSI + Inno Setup installer scripts
```

## Code Style

### Naming Conventions

- **PascalCase** for classes, methods, properties, events
- **camelCase** for local variables and parameters
- **_camelCase** for private fields
- **UPPER_CASE** for constants

### Architecture Principles

1. **Atomic tools** — Each tool does ONE thing. The AI agent composes them.
2. **IAgentTool interface** — All tools implement `Name`, `Description`, `GetInputSchema()`, `Execute()`
3. **ToolResult pattern** — Return `ToolResult.Ok()`, `ToolResult.Fail()`, or `ToolResult.WithWarning()`
4. **RevitCompat** — Use `RevitCompat.GetIdValue()` and `RevitCompat.CreateId()` for ElementId cross-version compatibility
5. **Write safety** — Any model modification must be confirmed by the user before execution

### Adding a New Tool

1. Create a new class in `Tools/` implementing `IAgentTool`
2. Register it in `ToolRegistry.CreateDefault()`
3. Add a thinking chain mapping in `ChatWindow.xaml.cs` → `MapToolToThinkingNode()`
4. Update the system prompt in `AgentService.cs` if the AI needs guidance on when/how to use it
5. Build both targets and verify 0 errors

### Multi-Version Compatibility

- Use `#if REVIT_2025_OR_GREATER` for API differences between Revit 2023/2024 and 2025/2026
- Use `RevitCompat` helper methods instead of direct `ElementId` constructors
- Test on at least one net48 version (2023/2024) and one net8 version (2025/2026) if possible

## Community Standards

- Be respectful and constructive in all interactions
- Focus on technical merit in code reviews
- Help newcomers get started

## Questions?

Open a [Discussion](https://github.com/QuanZ827/Zexus/discussions) or reach out via Issues.

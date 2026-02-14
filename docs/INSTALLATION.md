# Installation Guide

## Option A: MSI Installer (Recommended)

1. Download the latest `Zexus_Setup_v*.msi` from [GitHub Releases](https://github.com/QuanZ827/Zexus/releases)
2. Double-click to install — no admin required (installs per-user)
3. Open Revit and find the **Zexus** tab on the ribbon
4. Click **Zexus Agent** → **Settings** → choose your LLM provider and enter your API key
5. Start chatting with your Revit model

> Windows SmartScreen may show a warning — click "More info" → "Run anyway".

The installer auto-detects installed Revit versions (2023–2026) and deploys the correct runtime.

## Option B: Build from Source

### Prerequisites

| Requirement | Details |
|-------------|---------|
| Visual Studio 2022+ | With ".NET desktop development" workload |
| .NET 8 SDK | For `net8.0-windows` target (Revit 2025/2026) |
| .NET Framework 4.8 Developer Pack | For `net48` target (Revit 2023/2024) |
| Revit 2023–2026 | At least one version installed |
| LLM API key | Anthropic, OpenAI, or Google (see below) |

### Build Steps

```bash
# Clone the repository
git clone https://github.com/QuanZ827/Zexus.git
cd Zexus

# Build for Revit 2023/2024 (.NET Framework 4.8)
dotnet build -f net48 --configuration Release

# Build for Revit 2025/2026 (.NET 8)
dotnet build -f net8.0-windows --configuration Release
```

### Manual Deployment

After building, copy files to the Revit Addins folder:

**For Revit 2024 (net48):**
```
%ProgramData%\Autodesk\Revit\Addins\2024\Zexus\     ← All DLLs from bin\Release\net48\
%ProgramData%\Autodesk\Revit\Addins\2024\Zexus.addin ← The .addin manifest file
```

**For Revit 2025/2026 (net8.0-windows):**
```
%ProgramData%\Autodesk\Revit\Addins\2025\Zexus\     ← All DLLs from bin\Release\net8.0-windows\
%ProgramData%\Autodesk\Revit\Addins\2025\Zexus.addin ← The .addin manifest file
```

> **Note:** The PostBuild target in `Zexus.csproj` automates this copy step for detected Revit versions.

## API Key Configuration

### Supported Providers

| Provider | Get API Key | Models |
|----------|-------------|--------|
| **Anthropic** | [console.anthropic.com](https://console.anthropic.com/) | Claude Sonnet, Opus, Haiku |
| **OpenAI** | [platform.openai.com](https://platform.openai.com/api-keys) | GPT-4o, GPT-4o-mini |
| **Google** | [aistudio.google.com](https://aistudio.google.com/apikey) | Gemini 2.5 Pro, Flash |

### In-App Configuration (Recommended)

On first launch, click **Settings** in the Zexus window to:
1. Select your LLM provider from the dropdown
2. Enter your API key
3. Choose a model
4. Click Save

Settings are stored locally and never transmitted anywhere except to the selected provider's API.

### Config File (Advanced)

Alternatively, create a local config file next to the Zexus DLL (this file is git-ignored):

```json
// appsettings.local.json
{
  "Provider": "Anthropic",
  "ApiKey": "sk-ant-your-key-here",
  "Model": "claude-sonnet-4-20250514",
  "MaxTokens": 16384
}
```

> **Never commit your API key to version control.** The `.gitignore` already excludes `appsettings.local.json`.

## Uninstall

**MSI installer version:** Use Windows "Add or Remove Programs" → "Zexus for Revit"

**Manual:** Delete the `Zexus` folder and `Zexus.addin` file from:
```
%ProgramData%\Autodesk\Revit\Addins\<year>\
```

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Zexus tab doesn't appear in Revit | Check that `Zexus.addin` is in the correct Addins folder and the `Assembly` path points to the DLL |
| "API key not configured" error | Click Settings in the Zexus window and enter your API key |
| Build fails with Revit API errors | Ensure NuGet packages are restored (`dotnet restore`) |
| net8.0-windows build fails | Install [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| net48 build fails | Install [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48) |
| DLL load error in Revit | Ensure correct DLL version — net48 for Revit 2023/2024, net8.0 for Revit 2025/2026 |
| Rate limit errors | Wait 1 minute and retry, or upgrade your API tier with your provider |
| Provider dropdown text unreadable | Update to the latest version (fixed in v0.1.0+) |

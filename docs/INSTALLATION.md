# Installation Guide

## Option A: Installer (Recommended)

1. Download the latest `Zexus_Setup_vX.X.X.exe` from [GitHub Releases](https://github.com/QuanZ827/Zexus/releases)
2. Run the installer — it auto-detects installed Revit versions (2023, 2024, 2025, 2026)
3. Open Revit and find the **Zexus** tab on the ribbon
4. Click **Zexus Agent** → **Settings** → enter your [Anthropic API key](https://console.anthropic.com/)
5. Start chatting with your Revit model

## Option B: Build from Source

### Prerequisites

| Requirement | Details |
|-------------|---------|
| Visual Studio 2022+ | With ".NET desktop development" workload |
| .NET 8 SDK | For `net8.0-windows` target (Revit 2025/2026) |
| .NET Framework 4.8 Developer Pack | For `net48` target (Revit 2023/2024) |
| Revit 2023–2026 | At least one version installed |
| Anthropic API key | From [console.anthropic.com](https://console.anthropic.com/) |

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

**For Revit 2025 (net8.0-windows):**
```
%ProgramData%\Autodesk\Revit\Addins\2025\Zexus\     ← All DLLs from bin\Release\net8.0-windows\
%ProgramData%\Autodesk\Revit\Addins\2025\Zexus.addin ← The .addin manifest file
```

> **Note:** The PostBuild target in `Zexus.csproj` automates this copy step for detected Revit versions.

### API Key Configuration

On first launch, Zexus will prompt you to enter your Anthropic API key.

Alternatively, create a local config file (git-ignored):

```json
// appsettings.local.json (place next to Zexus.dll)
{
  "ApiKey": "sk-ant-your-key-here",
  "Model": "claude-sonnet-4-20250514",
  "MaxTokens": 16384
}
```

> **Never commit your API key to version control.** The `.gitignore` already excludes `appsettings.local.json`.

## Uninstall

**Installer version:** Use Windows "Add or Remove Programs" → "Zexus for Revit"

**Manual:** Delete the `Zexus` folder and `Zexus.addin` file from:
```
%ProgramData%\Autodesk\Revit\Addins\<year>\
```

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Zexus tab doesn't appear in Revit | Check that `Zexus.addin` is in the correct Addins folder and the `Assembly` path points to the DLL |
| "API key not configured" error | Click Settings in the Zexus window and enter your Anthropic API key |
| Build fails with Revit API errors | Ensure you have the correct Revit API NuGet packages restored (`dotnet restore`) |
| net8.0-windows build fails | Install .NET 8 SDK from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |
| net48 build fails | Install .NET Framework 4.8 Developer Pack from [Microsoft](https://dotnet.microsoft.com/download/dotnet-framework/net48) |
| DLL load error in Revit | Ensure you're using the correct DLL version — net48 for Revit 2023/2024, net8.0 for Revit 2025/2026 |

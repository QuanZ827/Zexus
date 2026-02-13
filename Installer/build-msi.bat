@echo off
setlocal enabledelayedexpansion
title Zexus - Build MSI Installer (Multi-Version)

echo.
echo ==========================================
echo   Zexus - Build MSI Installer
echo   Supports Revit 2023-2026
echo ==========================================
echo.

:: Step 1: Determine project root (one level up from Installer folder)
set "PROJECT_DIR=%~dp0.."
pushd "%PROJECT_DIR%"
set "PROJECT_DIR=%CD%"
popd

echo Project: %PROJECT_DIR%
echo.

:: Step 2: Build net48 (Revit 2023/2024)
echo.
echo [1/4] Building Release - net48 (Revit 2023/2024)...
echo.
dotnet build "%PROJECT_DIR%" -f net48 --configuration Release
if errorlevel 1 (
    echo.
    echo [ERROR] net48 build failed! Fix errors above and retry.
    pause
    exit /b 1
)
echo.
echo [OK] net48 build succeeded.

:: Step 3: Build net8.0-windows (Revit 2025/2026)
echo.
echo [2/4] Building Release - net8.0-windows (Revit 2025/2026)...
echo.
dotnet build "%PROJECT_DIR%" -f net8.0-windows --configuration Release
if errorlevel 1 (
    echo.
    echo [ERROR] net8.0-windows build failed! Fix errors above and retry.
    pause
    exit /b 1
)
echo.
echo [OK] net8.0-windows build succeeded.

:: Step 4: Generate team_config.json from api_key.txt (if present)
set "API_KEY_FILE=%~dp0api_key.txt"
set "TEAM_CONFIG=%~dp0team_config.json"

if exist "%API_KEY_FILE%" (
    set /p API_KEY=<"%API_KEY_FILE%"
    for /f "tokens=* delims= " %%a in ("!API_KEY!") do set "API_KEY=%%a"

    if "!API_KEY!"=="" (
        echo [SKIP] api_key.txt is empty - no team_config.json will be generated.
        if exist "%TEAM_CONFIG%" del "%TEAM_CONFIG%"
    ) else if "!API_KEY!"=="PASTE_YOUR_API_KEY_HERE" (
        echo [SKIP] api_key.txt still has placeholder - no team_config.json will be generated.
        echo        Edit Installer\api_key.txt with your real API key to embed it in the installer.
        if exist "%TEAM_CONFIG%" del "%TEAM_CONFIG%"
    ) else (
        echo [OK] Found API key in api_key.txt - generating team_config.json...
        (
            echo {
            echo   "ApiKey": "!API_KEY!",
            echo   "Model": "claude-sonnet-4-20250514",
            echo   "MaxTokens": 4096,
            echo   "EnableStreaming": true
            echo }
        ) > "%TEAM_CONFIG%"
        echo      team_config.json created. It will be embedded in the MSI.
    )
) else (
    echo [SKIP] No api_key.txt found - installer will require users to enter their own API key.
    if exist "%TEAM_CONFIG%" del "%TEAM_CONFIG%"
)
echo.

:: Step 5: Compile MSI with WiX v4
echo.
echo [3/4] Creating MSI installer with WiX...
echo.

:: Check if team_config.json exists to decide WiX flags
set "WIX_SRC=%~dp0Zexus.wxs"

:: If no team_config.json, create a dummy so WiX doesn't fail on the Source reference
if not exist "%TEAM_CONFIG%" (
    echo [INFO] Creating placeholder team_config.json for build...
    echo {} > "%TEAM_CONFIG%"
    set "CLEANUP_TEAM_CONFIG=1"
) else (
    set "CLEANUP_TEAM_CONFIG=0"
)

:: Create output directory
if not exist "%~dp0Output" mkdir "%~dp0Output"

:: Run WiX build
wix build "%WIX_SRC%" -o "%~dp0Output\Zexus_Setup_v2.0.1.msi" -arch x64
if errorlevel 1 (
    echo.
    echo [ERROR] MSI compilation failed!
    if "!CLEANUP_TEAM_CONFIG!"=="1" del "%TEAM_CONFIG%" 2>nul
    pause
    exit /b 1
)

:: Clean up placeholder team_config.json if we created it
if "!CLEANUP_TEAM_CONFIG!"=="1" (
    del "%TEAM_CONFIG%" 2>nul
    echo [INFO] Cleaned up placeholder team_config.json.
)

echo.
echo [4/4] Verifying MSI...
if exist "%~dp0Output\Zexus_Setup_v2.0.1.msi" (
    echo.
    echo ==========================================
    echo   SUCCESS!
    echo.
    echo   MSI Installer: Installer\Output\Zexus_Setup_v2.0.1.msi
    echo.
    echo   This MSI supports Revit 2023-2026.
    echo   Supports silent install: msiexec /i Zexus_Setup.msi /qn
    echo   Users can also double-click to install.
    echo ==========================================
) else (
    echo [ERROR] MSI file not found after build!
)
echo.
pause

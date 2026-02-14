@echo off
setlocal enabledelayedexpansion
title Zexus - Build Installer (Multi-Version)

echo.
echo ==========================================
echo   Zexus - Build Installer
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
echo [1/3] Building Release - net48 (Revit 2023/2024)...
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
echo [2/3] Building Release - net8.0-windows (Revit 2025/2026)...
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

:: Step 4: Find Inno Setup compiler
set "ISCC="
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
) else if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
)

if "%ISCC%"=="" (
    echo.
    echo [WARNING] Inno Setup 6 not found!
    echo.
    echo   Build succeeded, but cannot create installer EXE.
    echo   To generate the installer:
    echo     1. Download Inno Setup 6 from https://jrsoftware.org/isdl.php
    echo     2. Install it (default location)
    echo     3. Run this script again
    echo.
    echo   OR open Installer\Zexus_Setup.iss directly in Inno Setup.
    echo.
    pause
    exit /b 0
)

:: Step 4b: Generate team_config.json from api_key.txt (if present)
set "API_KEY_FILE=%~dp0api_key.txt"
set "TEAM_CONFIG=%~dp0team_config.json"

if exist "%API_KEY_FILE%" (
    set /p API_KEY=<"%API_KEY_FILE%"
    :: Trim leading/trailing spaces
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
        echo      team_config.json created. It will be embedded in the installer.
    )
) else (
    echo [SKIP] No api_key.txt found - installer will require users to enter their own API key.
    if exist "%TEAM_CONFIG%" del "%TEAM_CONFIG%"
)
echo.

:: Step 5: Compile installer
echo.
echo [3/3] Creating installer EXE...
echo      Using: %ISCC%
echo.
"%ISCC%" "%~dp0Zexus_Setup.iss"
if errorlevel 1 (
    echo.
    echo [ERROR] Installer compilation failed!
    pause
    exit /b 1
)

echo.
echo ==========================================
echo   SUCCESS!
echo.
echo   Installer: Installer\Output\Zexus_Setup_v0.2.0.exe
echo.
echo   This single EXE supports Revit 2023-2026.
echo   Distribute to users as needed.
echo   Users just double-click to install.
echo ==========================================
echo.
pause

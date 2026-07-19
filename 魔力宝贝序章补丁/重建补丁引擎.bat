@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."
if errorlevel 1 (
  echo [FAIL] Cannot cd to game root from %~dp0
  pause
  exit /b 1
)

set "BUILD=%~dp0patcher\_staging_build_%RANDOM%%RANDOM%"
set "STAGING=%~dp0patcher\_staging"
set "TARGET=%~dp0patcher\HotfixPatcher.exe"
set "TARGET_NEW=%~dp0patcher\HotfixPatcher.exe.new"
set "POINTER=%~dp0patcher\_staging_latest.txt"
set "DEV_OUT=tools\hotfix_patcher\bin\publish_test"

echo [BUILD] HotfixPatcher ...
mkdir "%BUILD%" 2>nul

dotnet publish "tools\hotfix_patcher\HotfixPatcher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:InvariantGlobalization=true -o "%BUILD%"
if errorlevel 1 (
  echo [FAIL] dotnet publish failed
  pause
  exit /b 1
)

if not exist "%BUILD%\HotfixPatcher.exe" (
  echo [FAIL] HotfixPatcher.exe not found in build dir
  pause
  exit /b 1
)

for %%I in ("%BUILD%") do set "BUILD_NAME=%%~nxI"
> "%POINTER%" echo %BUILD_NAME%

if not exist "%DEV_OUT%" mkdir "%DEV_OUT%"
copy /Y "%BUILD%\HotfixPatcher.exe" "%DEV_OUT%\HotfixPatcher.exe" >nul
if errorlevel 1 (
  echo [WARN] Could not copy to %DEV_OUT%
) else (
  echo [OK] Updated %DEV_OUT%\HotfixPatcher.exe
)

xcopy /E /I /Y "%BUILD%\*" "%STAGING%\" >nul 2>&1
if errorlevel 1 (
  echo [WARN] patcher\_staging is locked; GUI will use %BUILD_NAME%
) else (
  echo [OK] Synced to patcher\_staging
)

copy /Y "%BUILD%\HotfixPatcher.exe" "%TARGET%" >nul 2>&1
if errorlevel 1 (
  copy /Y "%BUILD%\HotfixPatcher.exe" "%TARGET_NEW%" >nul
  if errorlevel 1 (
    echo [WARN] patcher\HotfixPatcher.exe is locked and .exe.new copy failed.
    echo        Close patch GUI / terminal using the patcher, then run this bat again.
    echo [OK] Fresh build is in patcher\%BUILD_NAME%
    echo      Python GUI will use it automatically.
    pause
    exit /b 0
  )
  echo [WARN] patcher\HotfixPatcher.exe is locked by another process.
  echo        Updated patcher\HotfixPatcher.exe.new instead.
  echo        Close patch GUI, delete/rename old exe, rename .exe.new -^> .exe
  echo [OK] Python GUI can use patcher\%BUILD_NAME% immediately.
  pause
  exit /b 0
)

if exist "%TARGET_NEW%" del /f /q "%TARGET_NEW%"
echo [OK] HotfixPatcher.exe updated in patcher\
echo [OK] Latest build: patcher\%BUILD_NAME%
pause

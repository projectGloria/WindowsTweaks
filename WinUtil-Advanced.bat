@echo off
setlocal
title WinUtil - Advanced preset

net session >nul 2>&1
if %errorlevel% neq 0 (
    set "SELF=%~f0"
    powershell -NoProfile -Command "try { Start-Process -FilePath $env:SELF -Verb RunAs } catch { exit 1 }"
    if %errorlevel% neq 0 (
        echo Elevation was cancelled or failed.
        pause
    )
    exit /b %errorlevel%
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop'; " ^
  "try { " ^
  "  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " ^
  "  $script = Invoke-RestMethod -Uri 'https://christitus.com/win'; " ^
  "  & ([ScriptBlock]::Create($script)) -Preset Advanced -Run; " ^
  "  if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE } " ^
  "  exit 0 " ^
  "} catch { " ^
  "  Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red; " ^
  "  exit 1 " ^
  "}"

set "EXITCODE=%errorlevel%"
echo.
if %EXITCODE% equ 0 (
    echo Finished cleanly ^(exit code 0^). Reboot recommended.
) else (
    echo Finished with errors ^(exit code %EXITCODE%^). See output above.
)
pause
exit /b %EXITCODE%

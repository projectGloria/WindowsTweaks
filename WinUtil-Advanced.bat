@echo off
title WinUtil - Advanced preset

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; & ([ScriptBlock]::Create((Invoke-RestMethod 'https://christitus.com/win'))) -Preset Advanced; exit $LASTEXITCODE"

echo.
echo Finished with exit code %errorlevel% (0 = clean, 1 = errors, see log). Reboot recommended.
pause

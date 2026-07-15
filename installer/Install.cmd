@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-KiloviewSetup.ps1" -Source "%~dp0"
pause

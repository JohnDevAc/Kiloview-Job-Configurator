@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-NDIJobConfigurator.ps1" -Source "%~dp0"
exit /b %ERRORLEVEL%

@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Install.ps1"
exit /b %ERRORLEVEL%

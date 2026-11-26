@echo off
rem Builds MacDock and deploys it to %LOCALAPPDATA%\MacDock (see bi.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bi.ps1"
pause

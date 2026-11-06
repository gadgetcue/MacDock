@echo off
rem Brings the Windows taskbar back if MacDock was killed while it was hidden.
cd /d "%~dp0"
taskkill /im MacDock.exe /f >nul 2>&1
MacDock.exe /restore
echo Taskbar restored.

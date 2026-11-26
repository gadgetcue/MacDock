@echo off
rem Brings the Windows taskbar back if MacDock was killed while it was hidden.
taskkill /im MacDock.exe /f >nul 2>&1
"%LOCALAPPDATA%\MacDock\MacDock.exe" /restore
echo Taskbar restored.

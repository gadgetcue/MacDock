@echo off
rem Detached dock restart. Other threads: use this instead of launching MacDock.exe directly.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restart-dock.ps1"

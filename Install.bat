@echo off
rem Installs MacDock for the current user (no admin needed) into %LOCALAPPDATA%\MacDock and starts it.
setlocal
set "SRC=%~dp0"
set "DEST=%LOCALAPPDATA%\MacDock"
if not exist "%SRC%bin\MacDock.exe" (
    echo Run this from the unzipped release folder: bin\MacDock.exe was not found.
    pause & exit /b 1
)
taskkill /f /im MacDock.exe >nul 2>&1
mkdir "%DEST%" 2>nul
xcopy "%SRC%bin" "%DEST%\bin\" /E /I /Y /Q >nul
if exist "%SRC%icons" xcopy "%SRC%icons" "%DEST%\icons\" /E /I /Y /Q >nul
if exist "%SRC%dark-icons" xcopy "%SRC%dark-icons" "%DEST%\dark-icons\" /E /I /Y /Q >nul
copy /Y "%SRC%Uninstall.bat" "%DEST%\Uninstall.bat" >nul 2>&1
rem Start Menu shortcut
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\MacDock.lnk');$s.TargetPath='%DEST%\bin\MacDock.exe';$s.WorkingDirectory='%DEST%\bin';$s.Save()" >nul 2>&1
start "" "%DEST%\bin\MacDock.exe"
echo MacDock installed to %DEST% and started. It starts at login by default (toggle in Settings).
pause

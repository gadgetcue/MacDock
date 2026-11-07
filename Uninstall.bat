@echo off
rem Removes MacDock: restores the taskbar, stops it, removes autostart, the guard task, the shortcut and the install folder.
set "DEST=%LOCALAPPDATA%\MacDock"
schtasks /Change /TN MacDockGuard /DISABLE >nul 2>&1
if exist "%DEST%\bin\MacDock.exe" "%DEST%\bin\MacDock.exe" /restore
taskkill /f /im MacDock.exe >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v MacDock /f >nul 2>&1
schtasks /Delete /TN MacDockGuard /F >nul 2>&1
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\MacDock.lnk" >nul 2>&1
start "" cmd /c "timeout /t 2 >nul & rmdir /s /q "%DEST%""
echo MacDock removed. Your pins and settings remain in %APPDATA%\MacDock (delete that folder to remove them).
pause

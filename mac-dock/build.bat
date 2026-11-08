@echo off
rem Builds MacDock.exe with the .NET Framework compiler that ships with Windows (no SDK needed).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:macdock.ico /win32manifest:app.manifest /unsafe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Data.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll /resource:localsend.png,localsend.png /out:MacDock.exe *.cs
if not errorlevel 1 powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-icon.ps1" MacDock.exe
if errorlevel 1 (
    echo Build failed.
    pause
) else (
    echo Built MacDock.exe
)


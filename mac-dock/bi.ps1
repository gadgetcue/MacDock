$ErrorActionPreference='Continue'
cd "D:\#AI workflows\Claude work\Windows Apps\MacDock\mac-dock"; $w=$env:WINDIR; $g="$w\Microsoft.NET\assembly\GAC_MSIL"
Remove-Item MacDock.new.exe -ea 0
& "$w\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:macdock.ico /win32manifest:app.manifest /unsafe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Data.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll "/r:$g\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll" "/r:$g\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll" "/r:$g\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll" /resource:localsend.png,localsend.png /out:MacDock.new.exe *.cs | Select-String "error"
if (Test-Path MacDock.new.exe) { & "$PSScriptRoot/check-icon.ps1" MacDock.new.exe; if ($LASTEXITCODE -ne 0) { Remove-Item MacDock.new.exe; "ABORTED: no icon"; return } }
# Single install: %LOCALAPPDATA%\MacDock\MacDock.exe (never a bin subfolder, never a copy on D:). Optional arg: backup path for the old exe.
if (Test-Path MacDock.new.exe) { & "$PSScriptRoot\deploy-live.ps1" -Source "$PSScriptRoot\MacDock.new.exe" -Backup "$($args[0])" }

# Builds the ONE installer: ..\release\MacDock-Setup.exe (C# v2.4 dock, icon embedded). Payload is gathered from the live mac-dock folder.
param([string]$Exe = "$PSScriptRoot\..\mac-dock\MacDock.new.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path "$PSScriptRoot\..").Path; $md = "$root\mac-dock"
& "$md\check-icon.ps1" $Exe; if ($LASTEXITCODE -ne 0) { throw "no icon" }
$out = "$root\release"; New-Item -ItemType Directory -Force $out | Out-Null
$tmp = "$PSScriptRoot\payload.zip"; Remove-Item $tmp -ea 0
$z = [IO.Compression.ZipFile]::Open($tmp, 'Create')
function Add($file, $name) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $file, $name, 'Optimal') }
Add (Resolve-Path $Exe).Path 'bin/MacDock.exe'
Add "$md\macdock.ico" 'bin/macdock.ico'
foreach ($f in Get-ChildItem "$md\fonts" -File) { Add $f.FullName "bin/fonts/$($f.Name)" }
foreach ($f in Get-ChildItem "$md\icons" -File) { Add $f.FullName "bin/icons/$($f.Name)" }
foreach ($f in Get-ChildItem "$md\icons-dark" -File) { Add $f.FullName "bin/icons-dark/$($f.Name)" }
foreach ($d in 'icons','dark-icons') { foreach ($f in Get-ChildItem "$root\$d" -File) { Add $f.FullName "$d/$($f.Name)" } }
$z.Dispose()
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ "/win32icon:$md\macdock.ico" "/win32manifest:$PSScriptRoot\setup.manifest" /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/resource:$tmp,payload.zip" "/out:$out\MacDock-Setup.exe" "$PSScriptRoot\Setup.cs"
Remove-Item $tmp
& "$md\check-icon.ps1" "$out\MacDock-Setup.exe"
"{0} MB -> $out\MacDock-Setup.exe" -f [math]::Round((Get-Item "$out\MacDock-Setup.exe").Length/1MB,1)

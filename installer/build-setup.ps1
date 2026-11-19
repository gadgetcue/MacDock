# Builds installer\MacDock-Setup.exe: one file, icon + version info + asInvoker manifest, payload zipped inside.
# Usage: .\build-setup.ps1 [-Build ..\build-v2.3]
param([string]$Build = "$PSScriptRoot\..\build-v2.3")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = "$PSScriptRoot\payload.zip"; Remove-Item $tmp -ea 0
$z = [IO.Compression.ZipFile]::Open($tmp, 'Create')
foreach ($d in 'bin','icons','dark-icons') {
  Get-ChildItem "$Build\$d" -Recurse -File | % { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $_.FullName.Substring((Resolve-Path $Build).Path.Length+1).Replace([string][char]92,'/'), 'Optimal') }
}
$z.Dispose()
$w = $env:WINDIR
& "$w\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ "/win32icon:$PSScriptRoot\..\mac-dock\macdock.ico" "/win32manifest:$PSScriptRoot\setup.manifest" /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/resource:$tmp,payload.zip" "/out:$PSScriptRoot\MacDock-Setup.exe" "$PSScriptRoot\Setup.cs"
Remove-Item $tmp
"{0} MB" -f [math]::Round((Get-Item "$PSScriptRoot\MacDock-Setup.exe").Length/1MB,1)

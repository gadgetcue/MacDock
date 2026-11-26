# Builds MacDock (Rust) -> mac-dock-rs\MacDock-rs.exe (the ONE Rust build output; moved out of target\, never copied).
# This script does NOT install: it never touches the live %LOCALAPPDATA%\MacDock\MacDock.exe, stops no process and starts no task.
# Install is a separate, explicit step that only happens when the person asks for the Rust build.
$ErrorActionPreference = 'Stop'
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
Set-Location $PSScriptRoot
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
cargo build --release 2>&1 | Where-Object { $_ -match '^error|Finished' } | Out-Host
$code = $LASTEXITCODE; $ErrorActionPreference = $prev
if ($code -ne 0) { throw "cargo build failed (exit $code)" }
& "$PSScriptRoot\..\mac-dock\check-icon.ps1" "$PSScriptRoot\target\release\macdock.exe"
if ($LASTEXITCODE -ne 0) { throw "no embedded icon: aborted" }
Move-Item target\release\macdock.exe MacDock-rs.exe -Force
Write-Host ("Built: {0} ({1} MB)" -f "$PSScriptRoot\MacDock-rs.exe", [math]::Round((Get-Item MacDock-rs.exe).Length/1MB,2))

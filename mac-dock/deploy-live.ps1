# Installs MacDock to the ONE live location, %LOCALAPPDATA%\MacDock (flat, no bin folder), and (re)starts it via MacDockGuard.
# -Source <exe> deploys that build and deletes it afterwards; without -Source it only restarts the live dock.
# A packaged app's shell (Claude) has a virtualised %LOCALAPPDATA%, so the script then re-runs itself outside it through WMI.
param([string]$Source, [string]$Backup, [string]$Log)
$ErrorActionPreference = 'Stop'
$live = Join-Path $env:LOCALAPPDATA 'MacDock'
New-Item -ItemType Directory -Force $live | Out-Null
if (-not $Log) {
    $probe = "probe-$PID.tmp"
    Set-Content "$live\$probe" x
    $virtual = Test-Path "$env:LOCALAPPDATA\Packages\*\LocalCache\Local\MacDock\$probe"
    Remove-Item "$live\$probe"
    if ($virtual) {
        New-Item -ItemType Directory -Force "$PSScriptRoot\scratch" | Out-Null
        $out = "$PSScriptRoot\scratch\deploy.log"; Remove-Item $out -ea 0
        $cmd = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Log `"$out`""
        if ($Source) { $cmd += " -Source `"$((Resolve-Path -LiteralPath $Source).Path)`"" }
        if ($Backup) { $cmd += " -Backup `"$Backup`"" }
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = $PSScriptRoot }
        if ($r.ReturnValue -ne 0) { throw "could not start deploy outside the package ($($r.ReturnValue))" }
        Wait-Process -Id $r.ProcessId -Timeout 180 -ea 0
        $text = Get-Content $out; $text
        if (-not ($text -match '^OK ')) { throw 'deploy failed' }
        return
    }
}
function Say($m) { if ($Log) { Add-Content -LiteralPath $Log $m } else { $m } }
try {
    $exe = "$live\MacDock.exe"; $root = Split-Path $PSScriptRoot
    if (-not $Source -and -not (Test-Path $exe)) { throw 'no live install and no -Source' }
    if ($Source) {
        Copy-Item -LiteralPath $Source "$exe.tmp" -Force
        if ($Backup -and (Test-Path $exe)) { Copy-Item $exe $Backup -Force }
    }
    # Stop: pause the guard, ask the dock to quit (restores the taskbar), end only MacDock if it does not.
    Disable-ScheduledTask -TaskName MacDockGuard -ea SilentlyContinue | Out-Null
    try {
        $p = @(Get-Process MacDock -ea 0)
        if ($p) {
            Start-Process $p[0].Path -ArgumentList '--quit' -Wait
            $p | Wait-Process -Timeout 20 -ea 0
            Get-Process MacDock -ea 0 | Stop-Process -Force
            for ($i = 0; $i -lt 40 -and (Get-Process MacDock -ea 0); $i++) { Start-Sleep -Milliseconds 500 }
        }
        if ($Source) {
            for ($i = 0; ; $i++) { try { Move-Item "$exe.tmp" $exe -Force; break } catch { if ($i -ge 20) { throw }; Start-Sleep 1 } }
            # Files the dock reads next to its exe.
            foreach ($d in 'fonts', 'icons', 'icons-dark', 'icons-theme', 'scratch') { New-Item -ItemType Directory -Force "$live\$d" | Out-Null }
            foreach ($d in 'fonts', 'icons', 'icons-dark') { Copy-Item "$PSScriptRoot\$d\*" "$live\$d" -Recurse -Force }
            foreach ($d in 'icons', 'dark-icons') { Copy-Item "$root\$d\*" "$live\icons-theme" -Force }
            Copy-Item "$PSScriptRoot\macdock.ico" $live -Force
            # Settings kept next to the exe: migrated once, never overwritten.
            foreach ($f in 'dock.txt', 'iconmap.txt', 'docksize.txt', 'fanstyle.txt', 'foldercolors.txt', 'itemnames.txt', 'launchpad-folders.txt', 'launchpad-order.txt', 'launchpad-pins.txt', 'launchpad-sort.txt', 'taskbar-state.txt') {
                if ((Test-Path "$PSScriptRoot\$f") -and -not (Test-Path "$live\$f")) { Copy-Item "$PSScriptRoot\$f" "$live\$f" }
            }
        }
        # Guard: starts at logon and every minute, on battery too, no run time limit.
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $triggers = (New-ScheduledTaskTrigger -AtLogOn -User $me), (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1))
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName MacDockGuard -Force -Action (New-ScheduledTaskAction -Execute "`"$exe`"" -WorkingDirectory $live) -Trigger $triggers -Settings $set -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive) | Out-Null
        # Autostart value (only if present) and GPU preference follow the exe.
        $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if ((Get-ItemProperty $run).MacDock) { Set-ItemProperty $run MacDock "`"$exe`"" }
        $gpu = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
        if (Test-Path $gpu) { Set-ItemProperty $gpu $exe 'GpuPreference=1;' }
        $send = 'HKCU:\Software\Classes\Directory\shell\MacDockLocalSend\command'
        if (Test-Path -LiteralPath $send) { Set-ItemProperty -LiteralPath $send '(default)' "`"$exe`" --send `"%1`"" }
        if (Test-Path $gpu) { Remove-ItemProperty $gpu "$PSScriptRoot\MacDock.exe" -ea SilentlyContinue }
    }
    finally { Enable-ScheduledTask -TaskName MacDockGuard -ea SilentlyContinue | Out-Null }
    Start-ScheduledTask -TaskName MacDockGuard
    for ($i = 0; $i -lt 120 -and -not (Get-Process MacDock -ea 0); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep 5
    $now = @(Get-Process MacDock -ea 0)
    if (-not $now -or $now[0].Path -ne $exe) { throw "dock not running from $exe (found: $($now.Path))" }
    if ($Source) { Remove-Item -LiteralPath $Source }
    Say "OK pid=$($now[0].Id) $($now[0].Path)"
}
catch { Say "FAILED: $($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"; if (-not $Log) { throw } }

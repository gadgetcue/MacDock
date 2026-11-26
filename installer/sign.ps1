# Optional Authenticode signing + SHA-256 checksum. Without a certificate this only writes the checksum.
# To sign: set MACDOCK_PFX (path to the .pfx) and MACDOCK_PFX_PASS, and have signtool.exe (Windows SDK) on PATH.
param([Parameter(Mandatory)][string[]]$Files)
foreach ($f in $Files) {
    $f = (Resolve-Path $f).Path
    if ($env:MACDOCK_PFX -and (Get-Command signtool.exe -ErrorAction SilentlyContinue)) {
        signtool.exe sign /f $env:MACDOCK_PFX /p $env:MACDOCK_PFX_PASS /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d "MacDock" $f
        if ($LASTEXITCODE -ne 0) { throw "signing failed: $f" }
        Write-Host "signed: $f"
    } else { Write-Host "not signed (no MACDOCK_PFX / signtool): $f" }
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    $sum = Join-Path (Split-Path $f) 'SHA256SUMS.txt'
    $lines = @(Get-Content $sum -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch ([regex]::Escape((Split-Path $f -Leaf)) + '$') })
    Set-Content $sum ($lines + "$h  $(Split-Path $f -Leaf)") -Encoding ascii
    Write-Host "sha256 $h"
}

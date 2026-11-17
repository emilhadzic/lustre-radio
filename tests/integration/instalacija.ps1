# B6: instalacioni paket na GitHub racunaru (G): INSTALIRAJ -> zadatak postoji, nadzor, audio proces i ekran rade ->
# DEINSTALIRAJ -> zadatka i programa nema, podaci ostaju. Bez zvucnog uredjaja i bez muzike.
param([string]$Package)
$ErrorActionPreference = 'Stop'
function WaitFor([scriptblock]$Condition, [double]$Seconds, [string]$What) {
    $limit = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $limit) { if (& $Condition) { return }; Start-Sleep -Milliseconds 500 }
    throw "NIJE ISPUNJENO za $Seconds s: $What"
}
function Count([string]$Name) { @(Get-Process -Name $Name -ErrorAction SilentlyContinue).Count }
$dst = Join-Path $env:LOCALAPPDATA 'Programs\LustreRadio'
Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Package 'INSTALIRAJ.ps1')
    if ($LASTEXITCODE -ne 0) { throw "INSTALIRAJ: kod $LASTEXITCODE" }
    schtasks /Query /TN 'LustreRadio Watchdog' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'zadatak za automatsko pokretanje ne postoji' }
    WaitFor { (Count 'LustreRadio.Watchdog') -eq 1 -and (Count 'LustreRadio.Engine') -eq 1 } 60 'nadzor i audio proces rade iz instaliranog foldera'
    WaitFor { (Count 'LustreRadio.UI') -eq 1 } 60 'nadzor je otvorio ekran'
    $e = Get-Process -Name 'LustreRadio.Engine'
    if (-not $e.Path.StartsWith($dst, [StringComparison]::OrdinalIgnoreCase)) { throw "audio proces nije iz instaliranog foldera: $($e.Path)" }
    Write-Host 'REZULTAT | B6 instalacija | INSTALIRAJ: zadatak, nadzor, audio proces i ekran | PASS'

    Get-Process -Name 'LustreRadio.UI' | Stop-Process -Force
    WaitFor { (Count 'LustreRadio.UI') -eq 1 } 45 'zatvoren ekran se vratio'
    Write-Host 'REZULTAT | B6 instalacija | zatvoren ekran se vraca sam | PASS'

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Package 'DEINSTALIRAJ.ps1')
    if ($LASTEXITCODE -ne 0) { throw "DEINSTALIRAJ: kod $LASTEXITCODE" }
    schtasks /Query /TN 'LustreRadio Watchdog' 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'zadatak je ostao posle uklanjanja' }
    $global:LASTEXITCODE = 0
    if (Test-Path $dst) { throw 'program je ostao posle uklanjanja' }
    if ((Count 'LustreRadio.Engine') + (Count 'LustreRadio.Watchdog') + (Count 'LustreRadio.UI') -ne 0) { throw 'programi rade posle uklanjanja' }
    if (-not (Test-Path (Join-Path $env:ProgramData 'LustreRadio\lustre.db'))) { throw 'baza je obrisana pri uklanjanju' }
    Write-Host 'REZULTAT | B6 instalacija | DEINSTALIRAJ: nema zadatka ni programa, baza ostaje | PASS'
}
finally {
    Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    schtasks /Delete /TN 'LustreRadio Watchdog' /F 2>$null | Out-Null
    $global:LASTEXITCODE = 0
}

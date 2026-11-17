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

    # FINAL 4: nadogradnja preko postojece instalacije ne brise podatke (baza, istorija u dnevniku, muzika van programa)
    $journal = Get-ChildItem (Join-Path $env:ProgramData 'LustreRadio\journal') -Filter 'emitovanje*.jsonl' -ErrorAction SilentlyContinue | Select-Object -First 1
    $music = Join-Path $env:RUNNER_TEMP 'muzika-provera'
    New-Item -ItemType Directory -Force $music | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $music 'pesma.wav'), [byte[]](1..200))
    $musicHash = (Get-FileHash (Join-Path $music 'pesma.wav')).Hash
    $journalBefore = if ($journal) { [IO.File]::ReadAllBytes($journal.FullName) } else { @() }
    $dbBefore = (Get-Item (Join-Path $env:ProgramData 'LustreRadio\lustre.db')).Length
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Package 'INSTALIRAJ.ps1')
    if ($LASTEXITCODE -ne 0) { throw "nadogradnja (INSTALIRAJ ponovo): kod $LASTEXITCODE" }
    WaitFor { (Count 'LustreRadio.Engine') -eq 1 } 60 'audio proces radi posle nadogradnje'
    if ((Get-FileHash (Join-Path $music 'pesma.wav')).Hash -ne $musicHash) { throw 'muzika je promenjena pri nadogradnji' }
    if (-not (Test-Path (Join-Path $env:ProgramData 'LustreRadio\lustre.db'))) { throw 'baza je nestala pri nadogradnji' }
    if ((Get-Item (Join-Path $env:ProgramData 'LustreRadio\lustre.db')).Length -lt $dbBefore) { throw 'baza je manja posle nadogradnje' }
    if ($journal) {
        $after = [IO.File]::ReadAllBytes($journal.FullName)
        if ($after.Length -lt $journalBefore.Length -or -not [Linq.Enumerable]::SequenceEqual([byte[]]$journalBefore, [byte[]]($after[0..($journalBefore.Length - 1)]))) { throw 'istorija u dnevniku emitovanja je promenjena pri nadogradnji' }
    }
    Write-Host "REZULTAT | FINAL 4 | nadogradnja ne brise bazu, istoriju ni muziku (dnevnik $($journalBefore.Length) B sacuvan) | PASS"

    # FINAL 4: pravila zastitnog zida (G radi kao administrator, pa se Windows ne pita)
    $program = Join-Path $env:LOCALAPPDATA 'Programs\LustreRadio'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Package 'ZASTITNI-ZID.ps1') -Program $program
    if ($LASTEXITCODE -ne 0) { throw "ZASTITNI-ZID: kod $LASTEXITCODE" }
    foreach ($name in 'LustreRadio Engine - izlaz', 'LustreRadio Engine - ulaz', 'LustreRadio Watchdog - izlaz', 'LustreRadio Watchdog - ulaz') {
        netsh advfirewall firewall show rule name="$name" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "nema pravila zastitnog zida: $name" }
    }
    WaitFor { (Count 'LustreRadio.Engine') -eq 1 } 30 'audio proces radi uz pravila zastitnog zida'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Package 'ZASTITNI-ZID.ps1') -Program $program -Ukloni
    netsh advfirewall firewall show rule name='LustreRadio Engine - izlaz' 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'pravilo zastitnog zida je ostalo posle uklanjanja' }
    $global:LASTEXITCODE = 0
    Write-Host 'REZULTAT | FINAL 4 | pravila zastitnog zida: napravljena, radio radi, uklonjena | PASS'

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

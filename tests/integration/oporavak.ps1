# B6 (B-196 skraceno): automatski oporavak posle zaglavljenog dekodera, sa PRAVIM nadzorom.
# Paket: objavljeni Watchdog + probni Engine (tests/LustreRadio.TestEngine) pod imenom LustreRadio.Engine.exe.
# Probni Engine se nikad ne nalazi u paketu za korisnika (proverava se na kraju).
# Tok: prvo pokretanje -> svako otvaranje fajla se zaglavi -> 3 napustene niti + zaglavljena aktivna ->
# Engine izlazi sa kodom 4 -> nadzor ga pokrece ponovo -> zaglavljene pesme su u karantinu, radio pusta druge.
param([string]$Bin, [string]$TestEngineBin)
$ErrorActionPreference = 'Stop'

$data = Join-Path $env:ProgramData 'LustreRadio'
$logs = Join-Path $data 'logs'
function WaitFor([scriptblock]$Condition, [double]$Seconds, [string]$What) {
    $limit = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $limit) { if (& $Condition) { return }; Start-Sleep -Milliseconds 500 }
    throw "NIJE ISPUNJENO za $Seconds s: $What"
}
function Lines([string]$Name) {
    Get-ChildItem (Join-Path $logs "$Name-*.log") -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }
}
function Names([string]$File) {
    $p = Join-Path $data $File
    if (Test-Path $p) { @(Get-Content $p | ForEach-Object { $_ -split ' ' }) } else { @() }
}
function Stop-All { Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }

Stop-All
Start-Sleep 1
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force $data | Out-Null
'probni engine' | Set-Content (Join-Path $data 'probni-engine.txt')

$pkg = Join-Path $env:RUNNER_TEMP 'paket-oporavak'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
Copy-Item $Bin $pkg -Recurse
Remove-Item (Join-Path $pkg 'LustreRadio.Engine.exe')
Copy-Item (Join-Path $TestEngineBin 'LustreRadio.TestEngine*') $pkg
Rename-Item (Join-Path $pkg 'LustreRadio.TestEngine.exe') 'LustreRadio.Engine.exe'

# priprema (muzika, skeniranje, izjava o licenci) pre nadzora; tek posle nje znak da se prvo pokretanje zaglavi
$prep = Start-Process (Join-Path $pkg 'LustreRadio.Engine.exe') -ArgumentList '--priprema' -Wait -PassThru -NoNewWindow
if ($prep.ExitCode -ne 0) { throw "priprema probnog Engine-a: kod $($prep.ExitCode)" }
New-Item -ItemType File (Join-Path $data 'zaglavi-sve.flag') | Out-Null

try {
    $t0 = Get-Date
    Start-Process (Join-Path $pkg 'LustreRadio.Watchdog.exe')
    WaitFor { @(Lines 'engine' | Where-Object { $_ -match 'Kontrolisano ponovno pokretanje' }).Count -ge 1 } 120 'Engine je zatrazio kontrolisano ponovno pokretanje (kod 4)'
    $restartAt = (Get-Date) - $t0
    $stuck = @(Names 'zaglavljeni.txt' | Where-Object { $_ -like 'ton-*' } | Sort-Object -Unique)
    $firstPid = (Get-Content (Join-Path $data 'zaglavljeni.txt') | Select-Object -First 1).Split(' ')[0]
    Write-Host "zaglavljenih pesama u prvom pokretanju: $($stuck.Count) ($($stuck -join ', '))"
    if ($stuck.Count -lt 4) { throw "ocekivano 4 zaglavljena poziva (3 napustene + aktivna), bilo: $($stuck.Count)" }

    WaitFor { @(Lines 'watchdog' | Where-Object { $_ -match 'Pokrenut audio proces' }).Count -ge 2 } 60 'nadzor je ponovo pokrenuo Engine'
    # Na G nema zvucnog uredjaja, pa pesma ne odmice; dokaz da radio radi dalje je da novi Engine priprema (otvara) sledece pesme.
    function Opened2 { @(Get-Content (Join-Path $data 'otvoreni.txt') | Where-Object { -not $_.StartsWith("$firstPid ") } | ForEach-Object { $_.Split(' ')[1] }) }
    WaitFor { (Opened2).Count -ge 1 } 60 'posle ponovnog pokretanja Engine priprema pesme'
    $back = (Get-Date) - $t0
    Start-Sleep 5
    $opened2 = Opened2
    Write-Host "posle ponovnog pokretanja otvoreno: $($opened2 -join ', ')"
    $bad = @($opened2 | Where-Object { $stuck -contains $_ })
    if ($bad.Count -gt 0) { throw "pesme iz karantina su ponovo otvorene: $($bad -join ', ')" }

    $journal = Get-ChildItem (Join-Path $data 'journal') -Filter '*.jsonl' -Recurse | ForEach-Object { Get-Content $_.FullName }
    $shutdown = @($journal | Where-Object { $_ -match '"vrsta":"gasenje"' -and $_ -match 'zastoj' })
    if ($shutdown.Count -lt 1) { throw 'u dnevniku emitovanja nema zapisa gasenje - zastoj dekodera' }
    $quarantine = @($journal | Where-Object { $_ -match 'karantin' })
    Write-Host "zapisa o karantinu u dnevniku: $($quarantine.Count)"
    if ($quarantine.Count -lt 1) { throw 'karantin nije upisan u dnevnik emitovanja' }

    if (Get-ChildItem $Bin -Filter 'LustreRadio.TestEngine*') { throw 'probni Engine je u paketu za korisnika' }

    Write-Host ("REZULTAT | B-196 skraceno | od pokretanja do zahteva za ponovno pokretanje | {0:N1} s" -f $restartAt.TotalSeconds)
    Write-Host ("REZULTAT | B-196 skraceno | od pokretanja do pripreme pesama posle oporavka | {0:N1} s" -f $back.TotalSeconds)
    Write-Host 'OPORAVAK: SVI KORACI PROSLI'
}
finally {
    Stop-All
    Write-Host '--- dnevnik nadzora ---'
    Lines 'watchdog' | Select-Object -Last 40
    Write-Host '--- dnevnik audio procesa ---'
    Lines 'engine' | Select-Object -Last 120
}

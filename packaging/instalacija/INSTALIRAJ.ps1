# LUSTRE RADIO - instalacija za korisnika koji je sada prijavljen (bez administratorskih prava).
# Program: %LOCALAPPDATA%\Programs\LustreRadio. Podaci (baza, dnevnici): C:\ProgramData\LustreRadio - ne brisu se.
# Ponovno pokretanje ove skripte = nadogradnja: stari program se zaustavi i zameni, podaci ostaju.
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'program'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\LustreRadio'
$wd = Join-Path $dst 'LustreRadio.Watchdog.exe'
if (-not (Test-Path (Join-Path $src 'LustreRadio.Engine.exe'))) { throw "Nema foldera 'program' pored ove skripte. Raspakujte ceo ZIP." }

function Run([string]$Exe, [string]$Arg) {
    $p = Start-Process $Exe -ArgumentList $Arg -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

Write-Host 'LUSTRE RADIO - instalacija'
if (Test-Path $wd) {
    Write-Host 'Zaustavljam postojecu verziju...'
    Run $wd '--stop' | Out-Null
    Start-Sleep 8
}
Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1

Write-Host "Kopiram program u $dst ..."
New-Item -ItemType Directory -Force $dst | Out-Null
robocopy $src $dst /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Kopiranje nije uspelo (robocopy $LASTEXITCODE)." }

Write-Host 'Pravim zadatak za automatsko pokretanje posle prijave...'
$code = Run $wd '--install-task'
if ($code -ne 0) { throw "Zadatak za automatsko pokretanje nije napravljen (kod $code). Pogledajte C:\ProgramData\LustreRadio\logs." }
Run $wd '--start' | Out-Null

if (-not $NoStart) {
    Start-Process $wd -WorkingDirectory $dst
    Write-Host 'Radio je pokrenut. Ekran se otvara za nekoliko sekundi.'
}
Write-Host 'INSTALACIJA ZAVRSENA.'
exit 0

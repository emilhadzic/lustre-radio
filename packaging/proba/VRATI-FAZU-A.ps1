# Vraca bazu na stanje pre nadogradnje na semu 2, da bi radila stabilna verzija faze A.
# NISTA NE BRISE: trenutna baza, njen WAL i dnevnik emitovanja se PREMESTAJU u poseban folder.
param(
    [string]$DataRoot = (Join-Path $env:ProgramData 'LustreRadio'),
    [switch]$NoStop
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $NoStop) {
    Write-Host 'Zaustavljam LUSTRE RADIO...'
    $watchdog = Join-Path $here 'LustreRadio.Watchdog.exe'
    if (Test-Path $watchdog) { Start-Process $watchdog -ArgumentList '--stop' -Wait }
    Start-Sleep -Seconds 8
    Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
if (Get-Process -Name 'LustreRadio.Engine' -ErrorAction SilentlyContinue) { throw 'LUSTRE RADIO jos radi. Pokrenite ZAUSTAVI.cmd pa probajte ponovo.' }

$backups = Join-Path $DataRoot 'backups'
$copy = Get-ChildItem $backups -Filter 'pre-sema2-*.db' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $copy) { throw "Nema kopije pre nadogradnje (pre-sema2-*.db) u $backups. Ako baza nije ni nadogradjena, nista ne treba vracati." }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$aside = Join-Path $backups "pre-povratka-$stamp"
New-Item -ItemType Directory -Force $aside | Out-Null
$db = Join-Path $DataRoot 'lustre.db'
foreach ($name in 'lustre.db', 'lustre.db-wal', 'lustre.db-shm') {
    $file = Join-Path $DataRoot $name
    if (Test-Path $file) {
        Move-Item $file (Join-Path $aside $name)
        Write-Host "Premesteno: $name -> $aside"
    }
}
Copy-Item $copy.FullName $db
Write-Host "Vracena baza: $($copy.Name) -> lustre.db"
$journal = Join-Path $DataRoot 'journal'
if (Test-Path $journal) {
    Move-Item $journal (Join-Path $DataRoot "journal.novo-$stamp")
    Write-Host "Dnevnik emitovanja premesten u journal.novo-$stamp"
}
foreach ($name in 'lustre.db-wal', 'lustre.db-shm') {
    if (Test-Path (Join-Path $DataRoot $name)) { throw "Pored vracene baze je ostao $name; povratak nije zavrsen." }
}
Write-Host ''
Write-Host 'Gotovo. Sada pokrenite POKRENI.cmd iz foldera FAZE A.'
Write-Host 'Provera: u dnevniku engine-<datum>.log treba da pise "Baza: SQLite 3.53.3, sema 1".'

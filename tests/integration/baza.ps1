# Faza B, korak B1: nadogradnja baze, rad bez nadogradnje i povratak na fazu A, sa PRAVIM objavljenim programima.
# B-48, B-155, B-185, B-110, B-112, B-157. Mesto: G (bez zvuka). Paket faze A je skinut iz trajnog izdanja.
param([string]$Bin, [string]$PhaseA, [string]$Fixture)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'LustreRadio'
$logs = Join-Path $data 'logs'
$db = Join-Path $data 'lustre.db'
$backups = Join-Path $data 'backups'
$results = [ordered]@{}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Lite {
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibraryW(string path);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
    static byte[] U(string s) { return System.Text.Encoding.UTF8.GetBytes(s + "\0"); }
    public static long Value(string file, string sql) {
        IntPtr db; if (sqlite3_open_v2(U(file), out db, 1, IntPtr.Zero) != 0) throw new Exception("open " + file);
        try { IntPtr st; if (sqlite3_prepare_v2(db, U(sql), -1, out st, IntPtr.Zero) != 0) throw new Exception("sql " + sql);
              try { if (sqlite3_step(st) != 100) throw new Exception("nema reda: " + sql); return sqlite3_column_int64(st, 0); } finally { sqlite3_finalize(st); } }
        finally { sqlite3_close_v2(db); }
    }
}
"@
[void][Lite]::LoadLibraryW((Join-Path $Bin 'e_sqlite3.dll'))

function StopAll {
    foreach ($dir in $Bin, $PhaseA) { Start-Process (Join-Path $dir 'LustreRadio.Watchdog.exe') -ArgumentList '--stop' -Wait -ErrorAction SilentlyContinue }
    Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 2
    Remove-Item (Join-Path $data 'stop.flag') -ErrorAction SilentlyContinue
}
function EngineLines([datetime]$After) {
    Get-ChildItem (Join-Path $logs 'engine-*.log') -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName } |
        Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)' -and [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null) -ge $After.AddSeconds(-1) }
}
function RunVersion([string]$Dir, [string]$Expect, [int]$Seconds = 45) {
    $start = Get-Date
    Start-Process (Join-Path $Dir 'LustreRadio.Watchdog.exe')
    $limit = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $limit) {
        $lines = @(EngineLines $start)
        if ($lines | Where-Object { $_ -match [regex]::Escape($Expect) }) { Start-Sleep 3; StopAll; return $lines }
        Start-Sleep -Milliseconds 500
    }
    $lines = @(EngineLines $start)
    StopAll
    $lines | Select-Object -Last 15 | ForEach-Object { Write-Host "   $_" }
    throw "u dnevniku nema '$Expect' za $Seconds s ($Dir)"
}
function Counts { "{0} pesama, {1} istorije, {2} podesavanja" -f [Lite]::Value($db, 'SELECT count(*) FROM tracks'), [Lite]::Value($db, 'SELECT count(*) FROM play_history'), [Lite]::Value($db, 'SELECT count(*) FROM settings') }
function Sha([string]$f) { if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } else { '-' } }

StopAll
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item $Fixture $db

Write-Host '== faza A radi sa bazom seme 1'
[void](RunVersion $PhaseA 'Baza: SQLite')
if ([Lite]::Value($db, 'PRAGMA user_version') -ne 1) { throw 'faza A je promenila semu' }
$before = Counts
Write-Host "   pre nadogradnje: $before"

Write-Host '== B-48: nova verzija nadogradjuje bazu'
$lines = RunVersion $Bin 'Baza nadogradjena na semu 2'
if (-not ($lines | Where-Object { $_ -match 'Kopija baze proverena' })) { throw 'nema reda o proverenoj kopiji' }
if ([Lite]::Value($db, 'PRAGMA user_version') -ne 2) { throw 'baza nije sema 2' }
$copies = @(Get-ChildItem $backups -Filter 'pre-sema2-*.db')
if ($copies.Count -ne 1) { throw "ocekivana jedna kopija, ima $($copies.Count)" }
if ([Lite]::Value($copies[0].FullName, 'PRAGMA user_version') -ne 1) { throw 'kopija nije sema 1' }
[void](RunVersion $Bin 'Baza: SQLite')
if (@(Get-ChildItem $backups -Filter 'pre-sema2-*.db').Count -ne 1) { throw 'drugo pokretanje je pravilo novu kopiju' }
$results['B-48'] = "PASS: sema 2, kopija $($copies[0].Name) (sema 1), drugo pokretanje ne dira"

Write-Host '== B-112 i B-110: povratak na fazu A'
$walBefore = Test-Path "$db-wal"
& (Join-Path $Bin 'VRATI-FAZU-A.ps1') -DataRoot $data -NoStop
$aside = @(Get-ChildItem $backups -Directory -Filter 'pre-povratka-*')
if ($aside.Count -ne 1 -or -not (Test-Path (Join-Path $aside[0].FullName 'lustre.db'))) { throw 'stara baza nije premestena' }
if (-not (Test-Path $copies[0].FullName)) { throw 'kopija pre-sema2 je obrisana' }
if ((Test-Path "$db-wal") -or (Test-Path "$db-shm")) { throw 'pored vracene baze je ostao -wal ili -shm' }
$results['B-112'] = "PASS: nista nije obrisano; stara baza u $($aside[0].Name); nema starog -wal/-shm pored vracene baze"
$lines = RunVersion $PhaseA 'Baza: SQLite'
if ($lines | Where-Object { $_ -match 'novije verzije' }) { throw 'faza A odbija bazu' }
if (-not ($lines | Where-Object { $_ -match 'Baza: SQLite 3\.53\.3, sema 1' })) { throw 'faza A ne prijavljuje semu 1' }
$after = Counts
if ($after -ne $before) { throw "podaci posle povratka se razlikuju: pre '$before', posle '$after'" }
$results['B-110'] = "PASS: faza A radi sa vracenom bazom; $after (isto kao pre nadogradnje)"

Write-Host '== B-155 (prvi deo) i B-157: kopija ne moze da se napravi -> rad bez nadogradnje'
Rename-Item $backups "$backups.sacuvano"
Set-Content $backups 'fajl umesto foldera'
$dbSha = Sha $db; $walSha = Sha "$db-wal"
$lines = RunVersion $Bin 'Baza NIJE nadogradjena'
if (-not ($lines | Where-Object { $_ -match 'sema 1 \(NIJE nadogradjena' })) { throw 'nema reda o radu bez nadogradnje' }
if ((Sha $db) -ne $dbSha) { throw 'fajl baze se promenio u radu bez nadogradnje' }
if ($walSha -ne '-' -and (Sha "$db-wal") -ne $walSha) { throw 'WAL se promenio u radu bez nadogradnje' }
$results['B-155a'] = 'PASS: "Baza NIJE nadogradjena", rad bez nadogradnje; bajtovi baze isti'
$lines = RunVersion $PhaseA 'Baza: SQLite'
if (-not ($lines | Where-Object { $_ -match 'sema 1' }) -or ($lines | Where-Object { $_ -match 'novije verzije' })) { throw 'faza A ne radi posle rada bez nadogradnje' }
$results['B-157'] = 'PASS: faza A otvara bazu posle rada nove verzije bez nadogradnje'

Write-Host '== B-185: Engine nasilno prekinut tokom ucitavanja, 20 puta'
$dbSha = Sha $db; $walSha = Sha "$db-wal"
$engineExe = Join-Path $Bin 'LustreRadio.Engine.exe'
$rnd = [Random]::new(5)
for ($i = 1; $i -le 20; $i++) {
    $p = Start-Process $engineExe -PassThru
    Start-Sleep -Milliseconds ($rnd.Next(50, 1500))
    if (-not $p.HasExited) { $p | Stop-Process -Force }
    $p.WaitForExit()
    Start-Sleep -Milliseconds 300
    if ((Sha $db) -ne $dbSha) { throw "posle prekida $i se fajl baze promenio" }
    if ($walSha -ne '-' -and (Sha "$db-wal") -ne $walSha) { throw "posle prekida $i se WAL promenio" }
}
[void](RunVersion $Bin 'Baza NIJE nadogradjena')
$results['B-185'] = 'PASS: 20 prekida; bajtovi baze i WAL-a isti; sledece pokretanje radi'

Write-Host '== B-155 (drugi deo): kopija ponovo moguca -> nadogradnja'
Remove-Item $backups
Rename-Item "$backups.sacuvano" $backups
[void](RunVersion $Bin 'Baza nadogradjena na semu 2')
if ([Lite]::Value($db, 'PRAGMA user_version') -ne 2) { throw 'baza nije sema 2' }
$results['B-155b'] = 'PASS: posle vracanja foldera za kopije baza je nadogradjena na semu 2'

foreach ($k in $results.Keys) { Write-Host "REZULTAT | $k | $($results[$k])" }
StopAll

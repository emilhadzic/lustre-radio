# Integracioni test nadzora sa PRAVIM procesima (T-WD-01, 02, 03; T-SES-03, 04, 05).
# Radi bez zvucnog uredjaja: audio proces je ziv i odgovara, samo nema sta da pusta.
param([string]$Bin)
$ErrorActionPreference = 'Stop'
$env:LUSTRE_DATA_ROOT = Join-Path $env:TEMP ("lustre-nadzor-" + [guid]::NewGuid().ToString('N'))
$env:LUSTRE_TEST_ALLOW_SESSION0 = '1'   # GitHub runner radi u sesiji 0; u lokalu se ovo NE koristi
New-Item -ItemType Directory -Force $env:LUSTRE_DATA_ROOT | Out-Null
$watchdog = Join-Path $Bin 'LustreRadio.Watchdog.exe'
$engine = Join-Path $Bin 'LustreRadio.Engine.exe'

function Engines { @(Get-Process -Name 'LustreRadio.Engine' -ErrorAction SilentlyContinue) }
function Watchdogs { @(Get-Process -Name 'LustreRadio.Watchdog' -ErrorAction SilentlyContinue) }
function WaitFor([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $limit = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $limit) { if (& $Condition) { return }; Start-Sleep -Milliseconds 500 }
    throw "NIJE ISPUNJENO za $Seconds s: $What"
}
function Step([string]$Text) { Write-Host "== $Text" }

Add-Type -Namespace Native -Name Nt -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
'@

try {
    Step 'T-WD: nadzor pokrece tacno jedan audio proces'
    Start-Process $watchdog
    WaitFor { (Engines).Count -eq 1 } 30 'audio proces pokrenut'
    Start-Sleep 3
    if ((Watchdogs).Count -ne 1) { throw "nadzora: $((Watchdogs).Count)" }
    $first = (Engines)[0].Id

    Step 'T-WD-03: 10 dvostrukih pokretanja ne pravi duplikate'
    1..10 | ForEach-Object {
        $w = Start-Process $watchdog -PassThru; $e = Start-Process $engine -PassThru
        $w.WaitForExit(15000) | Out-Null; $e.WaitForExit(15000) | Out-Null
        if ($w.ExitCode -ne 3) { throw "drugi nadzor: izlazni kod $($w.ExitCode), ocekivan 3" }
        if ($e.ExitCode -ne 3) { throw "drugi audio proces: izlazni kod $($e.ExitCode), ocekivan 3" }
    }
    if ((Engines).Count -ne 1 -or (Watchdogs).Count -ne 1) { throw "posle dvostrukih pokretanja: $((Engines).Count) audio, $((Watchdogs).Count) nadzor" }
    if ((Engines)[0].Id -ne $first) { throw 'audio proces je zamenjen bez razloga' }

    Step 'T-WD-01: ubijen audio proces se vraca'
    $t = Get-Date
    Stop-Process -Id $first -Force
    WaitFor { (Engines).Count -eq 1 -and (Engines)[0].Id -ne $first } 30 'novi audio proces posle pada'
    Write-Host ("   vracen za {0:N1} s" -f ((Get-Date) - $t).TotalSeconds)
    $second = (Engines)[0].Id

    Step 'T-SES-04: pad nadzora ne gasi audio proces; novi nadzor ga preuzima'
    Stop-Process -Id (Watchdogs)[0].Id -Force
    Start-Sleep 5
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $second) { throw 'audio proces nije preziveo pad nadzora' }
    Start-Process $watchdog
    WaitFor { (Watchdogs).Count -eq 1 } 15 'novi nadzor'
    Start-Sleep 12
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $second) { throw 'novi nadzor je pokrenuo drugi audio proces umesto da preuzme postojeci' }

    Step 'T-WD-02: zamrznut audio proces se otkriva i zamenjuje'
    $t = Get-Date
    $victim = Get-Process -Id $second
    $status = [Native.Nt]::NtSuspendProcess($victim.Handle)
    if ($status -ne 0) { throw ("NtSuspendProcess nije uspeo: 0x{0:X8}" -f $status) }
    Start-Sleep 1
    $victim.Refresh()
    $waiting = @($victim.Threads | Where-Object { $_.ThreadState -eq 'Wait' -and $_.WaitReason -eq 'Suspended' }).Count
    Write-Host "   zamrznuto niti: $waiting od $($victim.Threads.Count)"
    if ($waiting -ne $victim.Threads.Count) { throw 'audio proces nije zaista zamrznut; test ne bi nista dokazao' }
    WaitFor { (Engines).Count -eq 1 -and (Engines)[0].Id -ne $second } 75 'zamena zamrznutog audio procesa'
    Write-Host ("   zamenjen za {0:N1} s" -f ((Get-Date) - $t).TotalSeconds)

    Step 'T-SES-05: namerno zaustavljanje i ponovno pokretanje'
    Start-Process $watchdog -ArgumentList '--stop' -Wait
    WaitFor { (Engines).Count -eq 0 } 30 'audio proces zaustavljen'
    Start-Sleep 12
    if ((Engines).Count -ne 0) { throw 'audio proces je pokrenut uprkos namernom zaustavljanju' }
    if ((Watchdogs).Count -ne 1) { throw 'nadzor ne radi tokom namernog zaustavljanja' }
    Start-Process $watchdog -ArgumentList '--start' -Wait
    WaitFor { (Engines).Count -eq 1 } 30 'audio proces vracen posle --start'

    Step 'T-SES-03: zadatak u Task Scheduler-u'
    $p = Start-Process $watchdog -ArgumentList '--install-task' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "--install-task: izlazni kod $($p.ExitCode)" }
    $xml = (schtasks /Query /TN 'LustreRadio Watchdog' /XML) -join "`n"
    foreach ($needed in 'InteractiveToken', '<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>', '<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>', '<Priority>4</Priority>', '<Interval>PT5M</Interval>') {
        if ($xml -notmatch [regex]::Escape($needed)) { throw "zadatak nema: $needed" }
    }
    $p = Start-Process $watchdog -ArgumentList '--check-task' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "--check-task: izlazni kod $($p.ExitCode)" }
    $p = Start-Process $watchdog -ArgumentList '--remove-task' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "--remove-task: izlazni kod $($p.ExitCode)" }

    Write-Host 'NADZOR: SVI KORACI PROSLI'
}
finally {
    Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    schtasks /Delete /TN 'LustreRadio Watchdog' /F 2>$null | Out-Null
    Write-Host '--- dnevnik nadzora (kraj) ---'
    Get-ChildItem "$env:ProgramData\LustreRadio\logs\watchdog-*.log" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Tail 60 }
}

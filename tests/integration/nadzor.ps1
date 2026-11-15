# Integracioni test nadzora sa PRAVIM, OBJAVLJENIM programima (isti fajlovi koji idu u paket).
# T-WD-01, 02, 03; T-SES-03, 04, 05; provera da nema skrivenih prekidaca.
# Radi bez zvucnog uredjaja: audio proces je ziv i odgovara, samo nema sta da pusta.
# Koristi pravi folder C:\ProgramData\LustreRadio (GitHub racunar je za jednokratnu upotrebu).
param([string]$Bin)
$ErrorActionPreference = 'Stop'

# Zamka: ove promenljive su do 15.11.2026. menjale ponasanje programa. Sada moraju biti bez dejstva.
$trap = Join-Path $env:TEMP ("lustre-zamka-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $trap | Out-Null
$env:LUSTRE_DATA_ROOT = $trap
$env:LUSTRE_TEST_ALLOW_SESSION0 = '1'

$watchdog = Join-Path $Bin 'LustreRadio.Watchdog.exe'
$engine = Join-Path $Bin 'LustreRadio.Engine.exe'
$ui = Join-Path $Bin 'LustreRadio.UI.exe'
$data = Join-Path $env:ProgramData 'LustreRadio'
$logs = Join-Path $data 'logs'
$results = [ordered]@{}

function Engines { @(Get-Process -Name 'LustreRadio.Engine' -ErrorAction SilentlyContinue) }
function Watchdogs { @(Get-Process -Name 'LustreRadio.Watchdog' -ErrorAction SilentlyContinue) }
function Uis { @(Get-Process -Name 'LustreRadio.UI' -ErrorAction SilentlyContinue) }
function WaitFor([scriptblock]$Condition, [double]$Seconds, [string]$What) {
    $limit = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $limit) { if (& $Condition) { return }; Start-Sleep -Milliseconds 200 }
    throw "NIJE ISPUNJENO za $Seconds s: $What"
}
function Step([string]$Text) { Write-Host "== $Text" }
function LogLines([string]$Name) {
    Get-ChildItem (Join-Path $logs "$Name-*.log") -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { Get-Content $_.FullName }
}
# Redovi dnevnika nadzora posle zadatog trenutka koji sadrze tekst; vraca vremena.
function WatchdogEvents([DateTimeOffset]$After, [string]$Text) {
    foreach ($line in (LogLines 'watchdog')) {
        if ($line -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d) ' -and $line.Contains($Text)) {
            $at = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture)
            if ($at -ge $After) { $at }
        }
    }
}

Add-Type -Namespace Native -Name Nt -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
'@

function Freeze([int]$ProcessId) {
    $victim = Get-Process -Id $ProcessId
    $status = [Native.Nt]::NtSuspendProcess($victim.Handle)
    $at = [DateTimeOffset]::Now
    if ($status -ne 0) { throw ("NtSuspendProcess nije uspeo: 0x{0:X8}" -f $status) }
    Start-Sleep -Milliseconds 700
    $victim.Refresh()
    $waiting = @($victim.Threads | Where-Object { $_.ThreadState -eq 'Wait' -and $_.WaitReason -eq 'Suspended' }).Count
    if ($waiting -ne $victim.Threads.Count) { throw "audio proces nije zaista zamrznut ($waiting od $($victim.Threads.Count) niti); test ne bi nista dokazao" }
    return @{ At = $at; Process = $victim }
}

try {
    Step 'Nadzor pokrece tacno jedan audio proces'
    $t = Get-Date
    Start-Process $watchdog
    WaitFor { (Engines).Count -eq 1 } 30 'audio proces pokrenut'
    WaitFor { @(LogLines 'watchdog' | Where-Object { $_ -match 'Odluka: "?None"? \(U redu' }).Count -ge 1 } 60 'audio proces odgovara nadzoru'
    $results['Od pokretanja nadzora do prvog odgovora audio procesa'] = '{0:N1} s' -f ((Get-Date) - $t).TotalSeconds
    if ((Watchdogs).Count -ne 1) { throw "nadzora: $((Watchdogs).Count)" }
    $first = (Engines)[0].Id

    Step 'Bez skrivenih prekidaca: LUSTRE_DATA_ROOT nema dejstva'
    $start = @(LogLines 'engine' | Where-Object { $_ -match 'LUSTRE RADIO Engine' -and $_ -notmatch 'smoke' })[-1]
    Write-Host "   $start"
    if ($start -notmatch [regex]::Escape("podaci: $data;")) { throw "audio proces ne koristi $data" }
    if (@(Get-ChildItem $trap -Force).Count -ne 0) { throw "u folderu iz LUSTRE_DATA_ROOT je nesto napravljeno" }
    if (-not (Test-Path (Join-Path $data 'lustre.db'))) { throw "baza nije u $data" }

    Step 'SQLite u objavljenom audio procesu (GHSA-2m69-gcr7-jv3q: mora biti 3.50.2 ili noviji)'
    $line = @(LogLines 'engine' | Where-Object { $_ -match 'Baza: SQLite (\d+\.\d+\.\d+)' })[-1]
    if (-not $line) { throw 'audio proces nije upisao verziju SQLite-a' }
    $null = $line -match 'Baza: SQLite (\d+\.\d+\.\d+)'
    $results['SQLite koji je ucitao objavljeni audio proces'] = $Matches[1]
    if ([version]$Matches[1] -lt [version]'3.50.2') { throw "ranjiv SQLite: $($Matches[1])" }

    Step 'T-WD-03: 20 brzih pokretanja; uvek po jedan audio proces, nadzor i interfejs'
    Start-Process $ui
    WaitFor { (Uis).Count -eq 1 -and (Uis)[0].MainWindowHandle -ne 0 } 60 'interfejs otvoren'
    $uiId = (Uis)[0].Id
    1..20 | ForEach-Object {
        $w = Start-Process $watchdog -PassThru; $e = Start-Process $engine -PassThru; $u = Start-Process $ui -PassThru
        foreach ($p in $w, $e, $u) { if (-not $p.WaitForExit(20000)) { throw "pokretanje $_ : $($p.ProcessName) nije izasao" } }
        if ($w.ExitCode -ne 3) { throw "drugi nadzor: izlazni kod $($w.ExitCode), ocekivan 3" }
        if ($e.ExitCode -ne 3) { throw "drugi audio proces: izlazni kod $($e.ExitCode), ocekivan 3" }
        if ($u.ExitCode -ne 0) { throw "drugi interfejs: izlazni kod $($u.ExitCode), ocekivan 0" }
        if ((Engines).Count -ne 1 -or (Watchdogs).Count -ne 1 -or (Uis).Count -ne 1) { throw "posle pokretanja $_ : $((Engines).Count) audio, $((Watchdogs).Count) nadzor, $((Uis).Count) interfejs" }
    }
    if ((Engines)[0].Id -ne $first) { throw 'audio proces je zamenjen bez razloga' }
    if ((Uis)[0].Id -ne $uiId) { throw 'interfejs je zamenjen bez razloga' }
    $results['T-WD-03: 20 pokretanja x 3 programa'] = 'uvek 1 + 1 + 1'

    Step 'T-WD-04 (deo): ubijen interfejs ne dira audio proces'
    Stop-Process -Id $uiId -Force
    Start-Sleep 4
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $first) { throw 'audio proces nije preziveo pad interfejsa' }

    Step 'T-WD-01: ubijen audio proces se vraca (rok 15 s)'
    $t = Get-Date
    Stop-Process -Id $first -Force
    WaitFor { (Engines).Count -eq 1 -and (Engines)[0].Id -ne $first } 15 'novi audio proces posle pada'
    $results['T-WD-01: novi proces posle ubijanja'] = '{0:N1} s (rok 15)' -f ((Get-Date) - $t).TotalSeconds
    $second = (Engines)[0].Id

    Step 'T-SES-04: pad nadzora ne gasi audio proces; novi nadzor ga preuzima'
    Stop-Process -Id (Watchdogs)[0].Id -Force
    Start-Sleep 5
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $second) { throw 'audio proces nije preziveo pad nadzora' }
    Start-Process $watchdog
    WaitFor { (Watchdogs).Count -eq 1 } 15 'novi nadzor'
    Start-Sleep 10
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $second) { throw 'novi nadzor je pokrenuo drugi audio proces umesto da preuzme postojeci' }
    $current = $second

    Step 'Kratko zamrzavanje (10 s) NIJE kvar: proces se ne dira'
    $frozen = Freeze $current
    Start-Sleep 10
    if ([Native.Nt]::NtResumeProcess($frozen.Process.Handle) -ne 0) { throw 'NtResumeProcess nije uspeo' }
    Start-Sleep 25
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $current) { throw 'nadzor je zamenio proces koji je cutao samo 10 s' }
    if (@(WatchdogEvents $frozen.At 'KillAndRestart').Count -ne 0) { throw 'nadzor je prekinuo proces koji je cutao samo 10 s' }
    $results['Zamrzavanje od 10 s'] = 'proces nije diran'

    Step 'Opterecen racunar (45 s, svi procesori zauzeti) NIJE kvar'
    $loadStart = [DateTimeOffset]::Now
    $burners = 1..([Environment]::ProcessorCount * 3) | ForEach-Object { Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'while ($true) { }' -PassThru -WindowStyle Hidden }
    try { Start-Sleep 45 } finally { $burners | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }
    Start-Sleep 6
    if ((Engines).Count -ne 1 -or (Engines)[0].Id -ne $current) { throw 'nadzor je zamenio zdrav proces dok je racunar bio opterecen' }
    if (@(WatchdogEvents $loadStart 'KillAndRestart').Count -ne 0) { throw 'lazno prekidanje pod opterecenjem' }
    $results['Opterecenje 45 s (' + ([Environment]::ProcessorCount * 3) + ' zauzetih petlji na ' + [Environment]::ProcessorCount + ' procesora)'] = 'proces nije diran'

    Step 'T-WD-02: zamrznut audio proces: otkrivanje najvise 30,0 s, novi proces najvise 35,0 s (tri puta)'
    $worstDetect = 0.0; $worstReplace = 0.0
    foreach ($round in 1..3) {
        $frozen = Freeze $current
        WaitFor { (Engines).Count -eq 1 -and (Engines)[0].Id -ne $current } 60 "zamena zamrznutog audio procesa ($round)"
        $replaced = ([DateTimeOffset]::Now - $frozen.At).TotalSeconds
        $kills = @(WatchdogEvents $frozen.At 'KillAndRestart')
        if ($kills.Count -ne 1) { throw "ocekivana tacno jedna odluka o prekidu, nadjeno $($kills.Count)" }
        $detect = ($kills[0] - $frozen.At).TotalSeconds
        Write-Host ("   {0}. put: otkriveno za {1:N1} s, novi proces za {2:N1} s" -f $round, $detect, $replaced)
        if ($detect -gt 30.0) { throw ("T-WD-02 PAO: otkrivanje je trajalo {0:N1} s (rok 30,0)" -f $detect) }
        if ($replaced -gt 35.0) { throw ("T-WD-02 PAO: novi proces tek posle {0:N1} s (rok 35,0)" -f $replaced) }
        if ($detect -lt 17.0) { throw ("prekinut posle samo {0:N1} s; pravilo je najmanje 20 s cutanja" -f $detect) }
        $worstDetect = [Math]::Max($worstDetect, $detect); $worstReplace = [Math]::Max($worstReplace, $replaced)
        $current = (Engines)[0].Id
        Start-Sleep 8
        if ((Engines).Count -ne 1) { throw "posle zamene: $((Engines).Count) audio procesa" }
    }
    $results['T-WD-02: najduze otkrivanje zamrzavanja (3 pokusaja)'] = '{0:N1} s (rok 30,0)' -f $worstDetect
    $results['T-WD-02: najduze do novog procesa (3 pokusaja)'] = '{0:N1} s (rok 35,0)' -f $worstReplace

    Step 'Sigurni rezim: posle vise prekida za kratko vreme audio proces je pokrenut sa --safe'
    $lastStart = @(LogLines 'engine' | Where-Object { $_ -match 'LUSTRE RADIO Engine' })[-1]
    Write-Host "   $lastStart"
    if ($lastStart -notmatch 'sigurni rezim: true') { throw 'posle 3 prekida za nekoliko minuta ocekivan je sigurni rezim' }
    $results['Sigurni rezim posle 3 prekida za nekoliko minuta'] = 'audio proces pokrenut u sigurnom rezimu'

    Step 'T-SES-05: namerno zaustavljanje i ponovno pokretanje'
    Start-Process $watchdog -ArgumentList '--stop' -Wait
    WaitFor { (Engines).Count -eq 0 } 15 'audio proces zaustavljen'
    Start-Sleep 10
    if ((Engines).Count -ne 0) { throw 'audio proces je pokrenut uprkos namernom zaustavljanju' }
    if ((Watchdogs).Count -ne 1) { throw 'nadzor ne radi tokom namernog zaustavljanja' }
    Start-Process $watchdog -ArgumentList '--start' -Wait
    WaitFor { (Engines).Count -eq 1 } 15 'audio proces vracen posle --start'

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

    if (@(Get-ChildItem $trap -Force).Count -ne 0) { throw "u folderu iz LUSTRE_DATA_ROOT je nesto napravljeno" }

    Write-Host '--- IZMERENO ---'
    foreach ($key in $results.Keys) { Write-Host ("REZULTAT | {0} | {1}" -f $key, $results[$key]) }
    Write-Host 'NADZOR: SVI KORACI PROSLI'
}
finally {
    Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    schtasks /Delete /TN 'LustreRadio Watchdog' /F 2>$null | Out-Null
    $global:LASTEXITCODE = 0   # brisanje vec obrisanog zadatka nije greska; pravi neuspeh javlja throw
    Write-Host '--- dnevnik nadzora (kraj) ---'
    LogLines 'watchdog' | Select-Object -Last 80
    Write-Host '--- dnevnik audio procesa (pokretanja) ---'
    LogLines 'engine' | Where-Object { $_ -match 'LUSTRE RADIO Engine|Baza: SQLite|FTL|ERR' } | Select-Object -Last 20
}

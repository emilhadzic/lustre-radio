# Faza B0: merenje memorije OBJAVLJENIH programa na GitHub racunaru (G), bez zvucnog uredjaja i bez muzike.
# B-101: privatna memorija Engine-a u mirovanju posle 5 minuta (prag 80 MB, "Engine u mirovanju, G").
# B-102: zbir privatne memorije Engine + UI + Watchdog u mirovanju posle 5 minuta (samo se belezi; budzet od 240 MB se NE potvrdjuje).
# B-103: Engine sam upisuje memoriju i brojace audio-niti u dnevnik (na pokretanju i na 5 minuta).
param([string]$Bin, [int]$Seconds = 315)
$ErrorActionPreference = 'Stop'

$watchdog = Join-Path $Bin 'LustreRadio.Watchdog.exe'
$ui = Join-Path $Bin 'LustreRadio.UI.exe'
$logs = Join-Path $env:ProgramData 'LustreRadio\logs'
function Procs([string]$Name) { @(Get-Process -Name $Name -ErrorAction SilentlyContinue) }
function Mb([long]$Bytes) { [math]::Round($Bytes / 1MB, 1) }

$started = Get-Date
try {
    Start-Process $watchdog
    $limit = (Get-Date).AddSeconds(60)
    while ((Procs 'LustreRadio.Engine').Count -eq 0 -and (Get-Date) -lt $limit) { Start-Sleep -Milliseconds 500 }
    if ((Procs 'LustreRadio.Engine').Count -ne 1) { throw 'Engine se nije pokrenuo (ili ih ima vise).' }
    Start-Process $ui
    Write-Host "== cekam $Seconds s u mirovanju"
    Start-Sleep -Seconds $Seconds

    $engine = Procs 'LustreRadio.Engine'; $w = Procs 'LustreRadio.Watchdog'; $u = Procs 'LustreRadio.UI'
    if ($engine.Count -ne 1 -or $w.Count -ne 1 -or $u.Count -ne 1) { throw "Ocekivan po jedan proces; Engine=$($engine.Count) Watchdog=$($w.Count) UI=$($u.Count)" }
    $e = $engine[0].PrivateMemorySize64; $wd = $w[0].PrivateMemorySize64; $ux = $u[0].PrivateMemorySize64
    Write-Host ("REZULTAT | B-101 | Engine u mirovanju, G | privatna {0} MB | radni skup {1} MB | prag 80 MB | {2}" -f (Mb $e), (Mb $engine[0].WorkingSet64), $(if ($e -le 80MB) { 'PASS' } else { 'FAIL' }))
    Write-Host ("REZULTAT | B-102 | zbir u mirovanju, G | Engine {0} + UI {1} + Watchdog {2} = {3} MB | samo se belezi; budzet 240 MB nije potvrdjen" -f (Mb $e), (Mb $ux), (Mb $wd), (Mb ($e + $ux + $wd)))

    $lines = Get-ChildItem (Join-Path $logs 'engine-*.log') | ForEach-Object { Get-Content $_.FullName } |
        Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)' -and [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null) -ge $started.AddSeconds(-2) }
    $health = @($lines | Where-Object { $_ -match 'Zdravlje:' })
    $audio = @($lines | Where-Object { $_ -match 'Audio-nit:' })
    $guard = @($lines | Where-Object { $_ -match 'Cuvar audio-niti' })
    $health + $audio + $guard + @($lines | Where-Object { $_ -match 'Podesavanja \.NET' }) | ForEach-Object { Write-Host "   $_" }
    $ok = $health.Count -ge 2 -and $audio.Count -ge 2 -and $guard.Count -ge 2
    Write-Host ("REZULTAT | B-103 | brojaci u dnevniku na 5 min | zapisa: zdravlje {0}, audio-nit {1}, cuvar {2} | {3}" -f $health.Count, $audio.Count, $guard.Count, $(if ($ok) { 'PASS' } else { 'FAIL' }))
    if ($e -gt 80MB) { throw 'B-101 PAO' }
    if (-not $ok) { throw 'B-103 PAO' }
}
finally {
    Start-Process $watchdog -ArgumentList '--stop' -Wait -ErrorAction SilentlyContinue
    Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

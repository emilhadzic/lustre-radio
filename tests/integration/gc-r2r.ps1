# Provera odstupanja od specifikacije 3.4 (Workstation GC bez concurrent rezima, ReadyToRun).
# Uporedjuje tri varijante objavljenog Engine-a i dve varijante interfejsa. Mesto: G (bez zvuka, bez muzike).
# Ne menja podesavanja programa: varijante se prave samo za ovo merenje.
$ErrorActionPreference = 'Stop'
$variants = [ordered]@{
    'A-sadasnje'          = @()
    'B-bez-concurrent'    = @('-p:ConcurrentGarbageCollection=false')
    'C-bez-concurrent-R2R' = @('-p:ConcurrentGarbageCollection=false', '-p:PublishReadyToRun=true')
}
function Median([double[]]$v) { $s = $v | Sort-Object; $s[[int][math]::Floor($s.Count / 2)] }
function SizeMb([string]$dir) { [math]::Round(((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1) }

$rows = @()
foreach ($name in $variants.Keys) {
    $out = Join-Path $env:RUNNER_TEMP "gc-$name"
    foreach ($p in 'Engine', 'UI') {
        dotnet publish "src/LustreRadio.$p/LustreRadio.$p.csproj" -c Release -r win-x64 --self-contained true -o $out @($variants[$name]) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "publish $p $name nije uspeo" }
    }
    $cfg = Get-Content (Join-Path $out 'LustreRadio.Engine.runtimeconfig.json') -Raw | ConvertFrom-Json
    $concurrent = $cfg.runtimeOptions.configProperties.'System.GC.Concurrent'

    $times = @{ Engine = @(); UI = @() }
    foreach ($p in 'Engine', 'UI') {
        $exe = Join-Path $out "LustreRadio.$p.exe"
        1..7 | ForEach-Object {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $proc = Start-Process $exe -ArgumentList '--smoke' -PassThru
            if (-not $proc.WaitForExit(60000)) { $proc.Kill(); throw "$p --smoke nije zavrsio" }
            $sw.Stop()
            if ($proc.ExitCode -ne 0) { throw "$p --smoke: izlazni kod $($proc.ExitCode)" }
            $times[$p] += $sw.Elapsed.TotalMilliseconds
        }
    }

    Get-Process -Name 'LustreRadio.Engine' -ErrorAction SilentlyContinue | Stop-Process -Force
    $engine = Start-Process (Join-Path $out 'LustreRadio.Engine.exe') -PassThru
    Start-Sleep 60
    $engine.Refresh()
    if ($engine.HasExited) { throw "Engine $name se ugasio" }
    $private = [math]::Round($engine.PrivateMemorySize64 / 1MB, 1)
    $working = [math]::Round($engine.WorkingSet64 / 1MB, 1)
    $threads = $engine.Threads.Count
    $engine | Stop-Process -Force
    Start-Sleep 2

    $rows += [pscustomobject]@{
        Varijanta = $name; Concurrent = $(if ($null -eq $concurrent) { 'podrazumevano (da)' } else { $concurrent })
        EngineSmokeMs = [math]::Round((Median $times.Engine)); UiSmokeMs = [math]::Round((Median $times.UI))
        EnginePrivatnaMb = $private; EngineRadniSkupMb = $working; EngineNiti = $threads; PaketMb = (SizeMb $out)
    }
}
foreach ($r in $rows) {
    Write-Host ("REZULTAT | GC/R2R | {0} | concurrent={1} | Engine pokretanje (medijana 7, --smoke) {2} ms | UI pokretanje {3} ms | Engine posle 60 s: privatna {4} MB, radni skup {5} MB, niti {6} | Engine+UI folder {7} MB" -f `
        $r.Varijanta, $r.Concurrent, $r.EngineSmokeMs, $r.UiSmokeMs, $r.EnginePrivatnaMb, $r.EngineRadniSkupMb, $r.EngineNiti, $r.PaketMb)
}

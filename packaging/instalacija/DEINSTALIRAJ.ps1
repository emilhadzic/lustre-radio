# LUSTRE RADIO - uklanjanje programa i zadatka za automatsko pokretanje. Podaci u C:\ProgramData\LustreRadio OSTAJU.
$ErrorActionPreference = 'Stop'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\LustreRadio'
$wd = Join-Path $dst 'LustreRadio.Watchdog.exe'
Write-Host 'LUSTRE RADIO - uklanjanje'
if (Test-Path $wd) {
    Start-Process $wd -ArgumentList '--stop' -Wait -WindowStyle Hidden
    Start-Sleep 8
}
Get-Process -Name 'LustreRadio.Watchdog', 'LustreRadio.Engine', 'LustreRadio.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
schtasks /Delete /TN 'LustreRadio Watchdog' /F 2>$null | Out-Null
# pravila zastitnog zida (brisu se samo ako je pokrenuto kao administrator; inace: ZASTITNI-ZID.cmd ukloni)
foreach ($name in 'LustreRadio Engine - izlaz', 'LustreRadio Engine - ulaz', 'LustreRadio Watchdog - izlaz', 'LustreRadio Watchdog - ulaz') {
    netsh advfirewall firewall delete rule name="$name" 2>$null | Out-Null
}
$global:LASTEXITCODE = 0
Start-Sleep 1
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
Write-Host 'Program je uklonjen. Baza, istorija i dnevnici su ostali u C:\ProgramData\LustreRadio.'
exit 0

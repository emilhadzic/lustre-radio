# Pravila zastitnog zida za LUSTRE RADIO (spec. D12): Engine i Watchdog su odseceni od mreze (ulaz i izlaz).
# Ekran (UI) nije blokiran: kasnije mu treba internet samo za generisanje najava.
param([Parameter(Mandatory)][string]$Program, [switch]$Ukloni)
$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Program', "`"$Program`"")
    if ($Ukloni) { $args += '-Ukloni' }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $p.ExitCode
}

$rules = @(
    @{ Name = 'LustreRadio Engine - izlaz'; Exe = 'LustreRadio.Engine.exe'; Dir = 'out' },
    @{ Name = 'LustreRadio Engine - ulaz'; Exe = 'LustreRadio.Engine.exe'; Dir = 'in' },
    @{ Name = 'LustreRadio Watchdog - izlaz'; Exe = 'LustreRadio.Watchdog.exe'; Dir = 'out' },
    @{ Name = 'LustreRadio Watchdog - ulaz'; Exe = 'LustreRadio.Watchdog.exe'; Dir = 'in' }
)
foreach ($r in $rules) {
    netsh advfirewall firewall delete rule name="$($r.Name)" 2>$null | Out-Null
    if (-not $Ukloni) {
        $exe = Join-Path $Program $r.Exe
        netsh advfirewall firewall add rule name="$($r.Name)" dir=$($r.Dir) action=block program="$exe" enable=yes | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Pravilo '$($r.Name)' nije napravljeno." }
    }
}
if ($Ukloni) { Write-Host 'Pravila zastitnog zida za LUSTRE RADIO su uklonjena.' }
else { Write-Host 'Zastitni zid: radio i nadzor su odseceni od mreze.' }
exit 0

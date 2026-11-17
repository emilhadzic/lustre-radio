@echo off
rem Pravilo Windows zastitnog zida: radio (Engine) i nadzor (Watchdog) nemaju pristup mrezi (spec. D12).
rem Trazi administratorska prava jednom (Windows ce pitati). Uklanjanje: ZASTITNI-ZID.cmd ukloni
if /I "%1"=="ukloni" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ZASTITNI-ZID.ps1" -Program "%LOCALAPPDATA%\Programs\LustreRadio" -Ukloni
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ZASTITNI-ZID.ps1" -Program "%LOCALAPPDATA%\Programs\LustreRadio"
)
pause

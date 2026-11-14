@echo off
echo Zaustavljam LUSTRE RADIO...
"%~dp0LustreRadio.Watchdog.exe" --stop
timeout /t 8 /nobreak >nul
taskkill /IM LustreRadio.Watchdog.exe /F >nul 2>&1
taskkill /IM LustreRadio.Engine.exe /F >nul 2>&1
taskkill /IM LustreRadio.UI.exe /F >nul 2>&1
echo Zaustavljeno.
timeout /t 3 >nul

@echo off
"%~dp0LustreRadio.Watchdog.exe" --start
start "" "%~dp0LustreRadio.Watchdog.exe"
start "" "%~dp0LustreRadio.UI.exe"

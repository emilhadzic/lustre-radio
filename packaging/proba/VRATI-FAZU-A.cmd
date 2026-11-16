@echo off
rem Vraca bazu na stanje pre nadogradnje na semu 2 (povratak na fazu A). Nista se ne brise; sve se premesta.
rem Uputstvo: POVRATAK.txt (u paketu faze A) i docs/POVRATAK.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0VRATI-FAZU-A.ps1" %*
echo.
pause

@echo off
dotnet build LustreRadio.slnx -c Release || exit /b 1
dotnet test LustreRadio.slnx -c Release --no-build
exit /b %ERRORLEVEL%

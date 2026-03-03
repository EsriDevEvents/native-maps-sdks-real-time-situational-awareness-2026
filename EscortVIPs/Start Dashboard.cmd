@echo off
setlocal
set "ROOT=%~dp0"

echo Starting Command Dashboard...
start "Command Dashboard" powershell -NoProfile -WindowStyle Hidden -Command "Set-Location '%ROOT%'; dotnet run --project ./CommandDashboard/CommandDashboard.csproj --no-build"

echo Dashboard launcher started.
endlocal

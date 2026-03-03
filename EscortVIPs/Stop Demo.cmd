@echo off
setlocal

echo Stopping demo processes...
taskkill /IM SimulationEngine.exe /F >nul 2>&1
taskkill /IM CommandDashboard.exe /F >nul 2>&1
taskkill /IM FieldMobileApp.exe /F >nul 2>&1

powershell -NoProfile -Command "$targets = Get-CimInstance Win32_Process | Where-Object { $_.Name -ieq 'dotnet.exe' -and $_.CommandLine -and ($_.CommandLine -match 'SimulationEngine[\\/]+SimulationEngine\.csproj|CommandDashboard[\\/]+CommandDashboard\.csproj') }; $targets | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1

echo Stop command sent.
endlocal

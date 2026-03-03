@echo off
setlocal

echo Starting Simulation Engine and Field Apps (tiled layout)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-FieldAppsTiled.ps1"

echo Field Apps launcher started.
endlocal

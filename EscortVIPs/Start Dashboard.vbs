Option Explicit

Dim shell
Dim scriptDir
Dim cmd
Dim psCmd

Set shell = CreateObject("WScript.Shell")
scriptDir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)

psCmd = "$ErrorActionPreference='Stop'; Set-Location -LiteralPath '" _
	& Replace(scriptDir, "'", "''") _
	& "'; if (-not $env:ARCGIS_API_KEY) { $env:ARCGIS_API_KEY = [Environment]::GetEnvironmentVariable('ARCGIS_API_KEY','User') }; if (-not $env:ARCGIS_API_KEY) { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('ARCGIS_API_KEY is not set. Set it in your user environment.','Command Dashboard Launcher'); exit 1 }; dotnet run --project ./CommandDashboard/CommandDashboard.csproj --no-build"

cmd = "powershell.exe -NoProfile -WindowStyle Hidden -Command """ & psCmd & """"
shell.Run cmd, 0, False

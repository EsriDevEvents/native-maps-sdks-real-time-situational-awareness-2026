Option Explicit

Dim shell
Dim scriptDir
Dim scriptPath
Dim cmd

Set shell = CreateObject("WScript.Shell")
scriptDir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
scriptPath = scriptDir & "\Start Dashboard.cmd"
cmd = "cmd.exe /c """ & scriptPath & """"
shell.Run cmd, 0, False

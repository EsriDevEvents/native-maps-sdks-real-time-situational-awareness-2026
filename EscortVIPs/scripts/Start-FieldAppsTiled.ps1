param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

function Start-DotnetRun {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [string]$Framework,
        [string[]]$AppArgs = @(),
        [switch]$PassThru,
        [switch]$Hidden
    )

    $args = @("run", "--project", $Project, "--no-build")
    if (-not [string]::IsNullOrWhiteSpace($Framework)) {
        $args += @("-f", $Framework)
    }

    if ($AppArgs.Count -gt 0) {
        $args += "--"
        $args += $AppArgs
    }

    $startProcessArgs = @{
        FilePath = "dotnet"
        ArgumentList = $args
        WorkingDirectory = $Root
    }

    if ($PassThru) {
        $startProcessArgs["PassThru"] = $true
    }

    if ($Hidden) {
        $startProcessArgs["WindowStyle"] = "Hidden"
    }

    Start-Process @startProcessArgs
}

$Root = (Resolve-Path -Path $Root).Path

function Wait-ForMainWindowHandle {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [int]$TimeoutSeconds = 45
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $Process.Refresh()
        }
        catch {
            break
        }

        if (-not $Process.HasExited -and $Process.MainWindowHandle -ne 0) {
            return $Process.MainWindowHandle
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    return [IntPtr]::Zero
}

function Resolve-FieldAppExecutablePath {
    $searchRoot = Join-Path $Root "FieldMobileApp\\bin"
    if (-not (Test-Path -Path $searchRoot)) {
        throw "FieldMobileApp build output not found. Run 'dotnet build ../DevSummit2026.Demos.slnx --ignore-failed-sources' first."
    }

    $candidates = @(Get-ChildItem -Path $searchRoot -Recurse -Filter "FieldMobileApp.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)

    if ($candidates.Count -eq 0) {
        throw "FieldMobileApp.exe not found under '$searchRoot'. Build the solution once, then re-run this launcher."
    }

    return $candidates[0].FullName
}

function Start-FieldApp {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][string]$Device
    )

    $args = @(
        "--role", $Role,
        "--mode", "simulated",
        "--device", $Device,
        "--session", "DEVSUMMIT-2026",
        "--host", "ws://127.0.0.1:8765/ws/",
        "--sim-host", "ws://127.0.0.1:8775/ws/",
        "--autoconnect",
        "--exit-on-disconnect"
    )

    Start-Process -FilePath $ExecutablePath -ArgumentList $args -WorkingDirectory $Root -PassThru
}

Write-Host "Starting Simulation Engine..."
Start-DotnetRun -Project "./SimulationEngine/SimulationEngine.csproj" -Hidden | Out-Null
Start-Sleep -Seconds 2

Write-Host "Starting Field Apps..."
$fieldAppExePath = Resolve-FieldAppExecutablePath

$escort = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "Escort" -Device "Escort"
$vip01 = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "VIP" -Device "Einstein"
$vip02 = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "VIP" -Device "Curie"
$vip03 = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "VIP" -Device "Newton"
$vip04 = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "VIP" -Device "Tesla"
$vip05 = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "VIP" -Device "Turing"
$vip06 = Start-FieldApp -ExecutablePath $fieldAppExePath -Role "VIP" -Device "Hopper"

$ordered = @($escort, $vip01, $vip02, $vip03, $vip04, $vip05, $vip06)
$handles = @(foreach ($process in $ordered) {
    Wait-ForMainWindowHandle -Process $process -TimeoutSeconds 45
})

$missingHandles = @($handles | Where-Object { $_ -eq [IntPtr]::Zero })
if ($missingHandles.Count -gt 0) {
    Write-Warning "Some Field App windows did not expose a main window handle in time."
}

$workArea = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$windowCount = $handles.Count
if ($windowCount -eq 0) {
    throw "No Field App windows were detected to tile."
}

$rows = 2
$vipCols = 3
$escortColumnWidth = [Math]::Floor($workArea.Width * 0.24)
$vipAreaWidth = $workArea.Width - $escortColumnWidth
$vipTileWidth = [Math]::Floor($vipAreaWidth / $vipCols)
$vipTileHeight = [Math]::Floor($workArea.Height / $rows)

$positions = @(
    @{ X = $workArea.X; Y = $workArea.Y; W = $escortColumnWidth; H = $workArea.Height }
)

for ($row = 0; $row -lt $rows; $row++) {
    for ($col = 0; $col -lt $vipCols; $col++) {
        $x = $workArea.X + $escortColumnWidth + ($col * $vipTileWidth)
        $y = $workArea.Y + ($row * $vipTileHeight)
        $w = if ($col -eq $vipCols - 1) {
            $workArea.Width - ($escortColumnWidth + (($vipCols - 1) * $vipTileWidth))
        }
        else {
            $vipTileWidth
        }

        $h = if ($row -eq $rows - 1) {
            $workArea.Height - ($row * $vipTileHeight)
        }
        else {
            $vipTileHeight
        }

        $positions += @{ X = $x; Y = $y; W = $w; H = $h }
    }
}

for ($i = 0; $i -lt [Math]::Min($handles.Count, $positions.Count); $i++) {
    $hWnd = $handles[$i]
    if ($hWnd -eq [IntPtr]::Zero) {
        continue
    }

    [NativeMethods]::ShowWindow($hWnd, 9) | Out-Null
    $p = $positions[$i]
    [NativeMethods]::MoveWindow($hWnd, $p.X, $p.Y, $p.W, $p.H, $true) | Out-Null
}

Write-Host "Field Apps started and tiled (Escort full left column, VIPs in a 2x3 grid)."

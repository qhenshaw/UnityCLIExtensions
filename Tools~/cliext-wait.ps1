<#
.SYNOPSIS
Waits, from outside the Editor, until a Unity Editor is idle: not compiling, not importing, main loop ticking.

.DESCRIPTION
`unity command cliext.wait idle` waits inside the Editor, so a compile that succeeds (and reloads the domain) drops
its request. This script waits from outside instead, reading the files the package writes in
Library/CLIExtensions/, so it carries on across domain reloads and returns at once when a modal dialog blocks the
Editor instead of waiting out its timeout.

Idle means: the heartbeat was written in the last 3 seconds (the main loop is ticking), and it says neither
compiling nor importing, for -SettleChecks checks in a row.

Exit codes: 0 idle, 1 timed out, 3 blocked by a dialog (see the watchdog report printed), 5 Safe Mode, 4 no heartbeat (the package
isn't installed, or the Editor isn't running).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-wait.ps1 -Project E:/Games/MyGame -Timeout 300
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [double]$Timeout = 120,
    [int]$SettleChecks = 3,
    [double]$IntervalSeconds = 0.5
)

$ErrorActionPreference = 'Stop'
$state = Join-Path (Resolve-Path $Project).Path 'Library\CLIExtensions'
$heartbeatFile = Join-Path $state 'heartbeat.json'
$watchdogFile = Join-Path $state 'watchdog-status.json'

function Read-Json([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $item = Get-Item $path
            $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json
            $json | Add-Member -NotePropertyName ageSeconds -NotePropertyValue ([math]::Round(([DateTime]::UtcNow - $item.LastWriteTimeUtc).TotalSeconds, 1)) -Force
            return $json
        } catch { Start-Sleep -Milliseconds 50 }
    }
    return $null
}

$clock = [Diagnostics.Stopwatch]::StartNew()
$idleChecks = 0
$sawHeartbeat = $false
while ($clock.Elapsed.TotalSeconds -lt $Timeout) {
    $hb = Read-Json $heartbeatFile
    if ($hb) { $sawHeartbeat = $true }

    $wd = Read-Json $watchdogFile
    # A progress window ("Reloading Domain", "Compiling...") is matched by the watchdog's "wait" rule: keep waiting.
    $blocking = @()
    if ($wd -and $wd.ageSeconds -lt 10) { $blocking = @($wd.dialogs | Where-Object { $_.action -ne 'wait' }) }
    if ($blocking.Count -gt 0) {
        [ordered]@{ result = 'blocked-by-dialog'; waitedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); dialogs = $blocking } | ConvertTo-Json -Depth 6
        exit 3
    }

    if ($wd -and $wd.ageSeconds -lt 10 -and $wd.state -eq 'safe-mode') {
        [ordered]@{ result = 'safe-mode'; waitedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); detail = 'The Editor is in Safe Mode (compile errors): no package code or Pipeline server runs. Fix the errors in the files.' } | ConvertTo-Json
        exit 5
    }

    $idle = $hb -and $hb.ageSeconds -lt 3 -and -not $hb.compiling -and -not $hb.updating -and -not $hb.quitting
    if ($idle) { $idleChecks++ } else { $idleChecks = 0 }
    if ($idleChecks -ge $SettleChecks) {
        [ordered]@{ result = 'idle'; waitedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); playMode = $hb.playMode; compileFailed = $hb.compileFailed; pid = $hb.pid } | ConvertTo-Json
        exit 0
    }
    Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000))
}

if (-not $sawHeartbeat) {
    [ordered]@{ result = 'no-heartbeat'; detail = "No $heartbeatFile. Is the Editor open with Unity CLI Extensions installed?" } | ConvertTo-Json
    exit 4
}
[ordered]@{ result = 'timed-out'; waitedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); heartbeat = $hb } | ConvertTo-Json -Depth 4
exit 1

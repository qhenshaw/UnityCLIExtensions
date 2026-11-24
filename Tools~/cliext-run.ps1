<#
.SYNOPSIS
Runs a `unity command` with a hard deadline, and when it runs out says what the Editor was doing.

.DESCRIPTION
A `unity command` that hangs usually waits on something in the Editor: a modal dialog, a long compile or import, or
a hung main thread. This runs the command, and if it hasn't finished by -Timeout seconds, stops waiting for it (the
Editor may still finish the work) and reports why from the files Unity CLI Extensions writes: the watchdog's report
(dialogs, a hung window) and the heartbeat (compiling, importing, Play mode, how long since the main loop ticked).

-Command is what follows `unity command --project-path <Project>`, as one string, quoted the way you would type it
in a terminal. (One string, because `powershell -File` would bind a `--timeout` inside it to this script's own
-Timeout.)

Exit codes: the command's own exit code when it finishes in time; 124 when the deadline passed (the JSON on stdout
says what the Editor was doing).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-run.ps1 -Project E:/Games/MyGame -Timeout 60 -Command 'scene open --path Assets/Main.unity'
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$Command,
    [double]$Timeout = 60
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path $Project).Path.TrimEnd('\')

$unity = (Get-Command unity -ErrorAction Stop).Source
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $unity
$info.Arguments = "command --project-path `"$projectPath`" $Command"
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.CreateNoWindow = $true

$process = [Diagnostics.Process]::Start($info)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()

if ($process.WaitForExit([int]($Timeout * 1000))) {
    $process.WaitForExit()
    [Console]::Out.Write($stdout.Result)
    [Console]::Error.Write($stderr.Result)
    exit $process.ExitCode
}

try { $process.Kill() } catch { }

function Read-Json([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try {
        $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        $json | Add-Member -NotePropertyName ageSeconds -NotePropertyValue ([math]::Round(([DateTime]::UtcNow - (Get-Item $path).LastWriteTimeUtc).TotalSeconds, 1)) -Force
        return $json
    } catch { return $null }
}

$state = Join-Path $projectPath 'Library\CLIExtensions'
$hb = Read-Json (Join-Path $state 'heartbeat.json')
$wd = Read-Json (Join-Path $state 'watchdog-status.json')
if ($wd -and $wd.ageSeconds -gt 15) { $wd = $null }   # a stale report says nothing about now

$blocking = @()
if ($wd) { $blocking = @($wd.dialogs | Where-Object { $_.action -ne 'wait' }) }

$reason = 'unknown'
$detail = 'Nothing in Library/CLIExtensions explains it. Is the watchdog running (unity command cliext.watchdog start)?'
if ($blocking.Count -gt 0) {
    $d = $blocking[0]
    $reason = 'blocked-by-dialog'
    $detail = "A dialog blocks the Editor: '$($d.title)' with buttons $(@($d.buttons) -join ', '). Press one with Tools~/Watchdog/cliext-watchdog.ps1 -Project <project> -Click <button>."
}
elseif ($wd -and $wd.state -eq 'safe-mode') { $reason = 'safe-mode'; $detail = 'The Editor is in Safe Mode because scripts have compile errors, so the Pipeline server is not running. Fix the errors in the files (Logs/Editor.log lists them).' }
elseif ($wd -and $wd.state -eq 'hung') { $reason = 'hung'; $detail = "The Editor's window has stopped responding: the main thread is stuck in a long operation or a loop." }
elseif ($hb -and $hb.compiling) { $reason = 'compiling'; $detail = 'The Editor is compiling scripts.' }
elseif ($hb -and $hb.updating) { $reason = 'importing'; $detail = 'The Editor is importing assets.' }
elseif ($wd -and $wd.state -eq 'busy') { $reason = 'busy'; $detail = 'A progress window is open (' + (@($wd.dialogs | ForEach-Object { $_.title }) -join ', ') + '): a long operation holds the main thread' + $(if ($hb) { ", which hasn't ticked for $($hb.ageSeconds) s." } else { '.' }) }
elseif ($hb -and $hb.ageSeconds -gt 5) { $reason = 'main-thread-busy'; $detail = "The Editor's main loop hasn't ticked for $($hb.ageSeconds) s: a long operation holds it, or a domain reload is in progress." }
elseif ($hb) { $reason = 'editor-idle'; $detail = 'The Editor is ticking and idle: the command itself is slow, or it waits on something of its own (a queued command ahead of it, a server-side wait).' }

[ordered]@{
    result = 'timed-out'; timeoutSeconds = $Timeout; command = $Command
    reason = $reason; detail = $detail
    dialogs = $blocking
    editor = $hb
    partialOutput = $stdout.Result
} | ConvertTo-Json -Depth 6
exit 124

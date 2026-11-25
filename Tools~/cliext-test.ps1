<#
.SYNOPSIS
Runs only the tests a change can affect, in one call.

.DESCRIPTION
Asks the Editor which test assemblies the change affects (`unity command cliext.test affected`), then runs each with
Pipeline's `test run`: Edit Mode assemblies synchronously, Play Mode ones asynchronously, polling `test status` (Play
Mode reloads the domain, which a synchronous request would not survive). Prints one JSON summary.

The change comes from -Files, or from git: uncommitted changes against -Since (default HEAD) plus untracked files.
-All runs every test assembly.

It refuses to start while an open scene has unsaved changes (the Test Runner would stop at a save prompt), and when
a run leaves the Editor in another scene (a run that timed out or was cancelled), it reopens the scenes that were open
before (`restoredScenes`).

Exit codes: 0 every run passed (or nothing was affected), 6 a test failed or a run errored, 1 the Editor couldn't be
asked or a scene has unsaved changes.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-test.ps1 -Project E:/Games/MyGame
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-test.ps1 -Project E:/Games/MyGame -Files Assets/Scripts/Player.cs
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Files,
    [string]$Since = 'HEAD',
    [switch]$All,
    [double]$PlayModeTimeout = 900
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path $Project).Path.TrimEnd('\')

function Invoke-Unity([string[]]$arguments) {
    # The CLI writes errors and logs to stderr; keep them as text instead of letting PowerShell turn them into errors.
    $ErrorActionPreference = 'Continue'
    $output = (& unity command --project-path $projectPath @arguments --format json 2>&1 | ForEach-Object { "$_" }) -join "`n"
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Get-Counts([string]$text) {
    $counts = [ordered]@{}
    foreach ($s in 'Passed', 'Failed', 'Skipped', 'Inconclusive') { $counts[$s.ToLower()] = ([regex]::Matches($text, '"status"\s*:\s*"' + $s + '"')).Count }
    $failures = @([regex]::Matches($text, '"(?:fullName|FullName|name)"\s*:\s*"([^"]+)"[^{}]*?"status"\s*:\s*"Failed"') | ForEach-Object { $_.Groups[1].Value })
    $counts['failures'] = $failures
    return $counts
}

$askArgs = @('cliext.test', 'affected')
if ($All) { $askArgs += @('--all', 'true') }
elseif ($Files) { $askArgs += @('--files', $Files) }
else { $askArgs += @('--since', $Since) }
$ask = Invoke-Unity $askArgs
if ($ask.ExitCode -ne 0) {
    [ordered]@{ result = 'error'; detail = 'cliext.test affected failed'; output = $ask.Output } | ConvertTo-Json -Depth 4
    exit 1
}
$jsonStart = $ask.Output.IndexOf('{')
$plan = $ask.Output.Substring($jsonStart) | ConvertFrom-Json
if ($plan.result) { $plan = $plan.result }   # tolerate an envelope

function Get-OpenScenes {
    $r = Invoke-Unity @('scene', 'list_open')
    if ($r.ExitCode -ne 0) { return $null }
    return @($r.Output -split "`n" | Where-Object { $_.TrimStart().StartsWith('{') } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } })
}

$scenesBefore = $null
if (@($plan.testAssemblies).Count -gt 0) {
    $scenesBefore = Get-OpenScenes
    # The Test Runner asks to save modified scenes in a modal dialog, which blocks the run until someone answers.
    $dirty = @($scenesBefore | Where-Object { $_.isDirty })
    if ($dirty.Count -gt 0) {
        [ordered]@{
            result = 'error'
            detail = "Open scene(s) have unsaved changes: $(@($dirty | ForEach-Object { if ($_.path) { $_.path } else { 'Untitled' } }) -join ', '). The Test Runner would stop at a save prompt. Save them (unity command cliext.scene save_open) or discard them, then run again."
            changedFiles = $plan.changedFiles
        } | ConvertTo-Json -Depth 4
        exit 1
    }
}

$runs = @()
$failed = $false
foreach ($t in @($plan.testAssemblies)) {
    $run = [ordered]@{ assembly = $t.name; mode = $t.mode; because = $t.because }
    if ($t.mode -eq 'EditMode') {
        $r = Invoke-Unity @('test', 'run', '--mode', 'editor', '--filter-type', 'Assembly', '--filter', $t.name)
        $run.exitCode = $r.ExitCode
        $counts = Get-Counts $r.Output
        foreach ($k in $counts.Keys) { $run[$k] = $counts[$k] }
        if ($r.ExitCode -ne 0 -and $run.passed -eq 0 -and $run.failed -eq 0) { $run.output = $r.Output.Trim() }
    }
    else {
        $start = Invoke-Unity @('test', 'run', '--mode', 'playmode', '--async-tests', 'true', '--filter-type', 'Assembly', '--filter', $t.name)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $status = $null
        while ($clock.Elapsed.TotalSeconds -lt $PlayModeTimeout) {
            Start-Sleep -Seconds 3
            $status = Invoke-Unity @('test', 'status', '--detail', 'failures')
            if ($status.Output -notmatch '"status"\s*:\s*"(running|in_progress|started|pending)"' -and $status.Output -notmatch 'closed the connection|No Pipeline instance|Bad Request') { break }
        }
        $run.exitCode = $status.ExitCode
        $run.status = $status.Output.Trim()
    }
    if ($run.exitCode -ne 0) { $failed = $true }
    $runs += $run
}

# A run that times out or is cancelled can leave the Editor in the Test Runner's Untitled scene. Put back the scenes
# that were open before.
$restoredScenes = $null
$saved = @($scenesBefore | Where-Object { $_.path })
if ($saved.Count -gt 0) {
    $now = Get-OpenScenes
    $nowPaths = @($now | Where-Object { $_.path } | ForEach-Object { $_.path })
    $missing = @($saved | Where-Object { $nowPaths -notcontains $_.path })
    if ($now -and $missing.Count -gt 0 -and -not @($now | Where-Object { $_.isDirty })) {
        $active = @($saved | Where-Object { $_.isActive } | Select-Object -First 1)
        $first = $(if ($active) { $active[0] } else { $saved[0] })
        $restoredScenes = @()
        if ((Invoke-Unity @('scene', 'open', '--path', $first.path)).ExitCode -eq 0) { $restoredScenes += $first.path }
        foreach ($s in $saved | Where-Object { $_.path -ne $first.path }) {
            if ((Invoke-Unity @('scene', 'open', '--path', $s.path, '--additive', 'true')).ExitCode -eq 0) { $restoredScenes += $s.path }
        }
    }
}

[ordered]@{
    result = $(if ($failed) { 'failed' } elseif ($runs.Count -eq 0) { 'nothing-affected' } else { 'passed' })
    changedFiles = $plan.changedFiles
    changedAssemblies = $plan.changedAssemblies
    otherChanges = $plan.otherChanges
    runs = $runs
    restoredScenes = $restoredScenes
} | ConvertTo-Json -Depth 6
if ($failed) { exit 6 }
exit 0

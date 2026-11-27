<#
.SYNOPSIS
Out-of-process tests for Unity CLI Extensions: drives a running Editor through `unity command` and checks what only
shows from outside it (exit codes, reply shapes, domain reloads, edit batches, the watchdog, shell quoting).

.DESCRIPTION
EditMode tests run inside the Editor and can't see the CLI's exit codes, a reply dropped by a domain reload, or how
Windows PowerShell passes arguments. This script runs each check against the Editor that has -Project open and prints
one line per check, then a summary. Scratch files go in Assets/_CliextTests/CliTest and are removed at the end.

Commands are started with System.Diagnostics.Process and an argument string this script quotes itself, so PowerShell
5.1's native argument passing (which drops the quotes inside JSON) does not affect them. The `env.*` checks use
PowerShell's own `&` on purpose, to show what an agent typing the same thing gets.

After the checks it prints T1 coverage: for each core-loop row, how many of its commands this run called, and which
it didn't. -T3Ready runs only the T3 environment check instead (setup applied, agent mode on, watchdog running, skills
current), for use right before agent scenario runs.

Exit codes: 0 when every check passed, 1 when one failed, 2 when no Editor answered.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-clitest.ps1 -Project E:/Games/MyGame
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-clitest.ps1 -Project E:/Games/MyGame -Filter '^(xcut|env)\.' -Out results.json
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Filter = '.',
    [string]$Out = '',
    [switch]$Keep,
    [switch]$T3Ready
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path $Project).Path.TrimEnd('\')
$unity = (Get-Command unity -ErrorAction Stop).Source
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Split-Path -Parent $tools
$scratchRel = 'Assets/_CliextTests/CliTest'
$scratch = Join-Path $projectPath ($scratchRel -replace '/', '\')
$work = Join-Path ([IO.Path]::GetTempPath()) ("cliext-clitest-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

# ---------------------------------------------------------------------------------------------------------------------
# Running commands

function Format-Arg([string]$a) {
    # Windows command-line quoting (CommandLineToArgvW rules).
    if ($a -ne '' -and $a -notmatch '[\s"]') { return $a }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $a.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') { [void]$sb.Append('\' * ($slashes * 2 + 1)); [void]$sb.Append('"'); $slashes = 0; continue }
        if ($slashes -gt 0) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
        [void]$sb.Append($ch)
    }
    if ($slashes -gt 0) { [void]$sb.Append('\' * ($slashes * 2)) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

$invoked = New-Object 'Collections.Generic.HashSet[string]'

function Invoke-Cli([string[]]$CliArgs, [double]$Timeout = 120) {
    if ($CliArgs.Count -gt 0 -and $CliArgs[0] -like 'cliext*') {
        if ($CliArgs.Count -gt 1 -and -not $CliArgs[1].StartsWith('-')) { [void]$invoked.Add("$($CliArgs[0]) $($CliArgs[1])") }
        else { [void]$invoked.Add($CliArgs[0]) }
    }
    $all = @('command', '--project-path', $projectPath) + $CliArgs
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $unity
    $info.Arguments = ($all | ForEach-Object { Format-Arg $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($info)
    $o = $p.StandardOutput.ReadToEndAsync()
    $e = $p.StandardError.ReadToEndAsync()
    $timedOut = -not $p.WaitForExit([int]($Timeout * 1000))
    if ($timedOut) { try { $p.Kill() } catch { } }
    $p.WaitForExit()
    $stdout = $o.Result
    $json = $null
    $t = $stdout.Trim()
    if ($t.StartsWith('{') -or $t.StartsWith('[')) {
        try { $json = $t | ConvertFrom-Json }
        catch {
            # Record commands print one JSON object per record, one per line.
            try { $json = @($t -split "`r?`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }) } catch { $json = $null }
        }
    }
    [pscustomobject]@{
        exit = $(if ($timedOut) { 124 } else { $p.ExitCode }); stdout = $stdout; stderr = $e.Result; json = $json
        ms = $sw.ElapsedMilliseconds; args = $info.Arguments
    }
}

function Wait-Editor([double]$Timeout = 300) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools 'cliext-wait.ps1') -Project $projectPath -Timeout $Timeout | Out-Null
    return $LASTEXITCODE
}

function Write-ScratchFile([string]$name, [string]$text) {
    New-Item -ItemType Directory -Force $scratch | Out-Null
    $p = Join-Path $scratch $name
    [IO.File]::WriteAllText($p, $text)
    return "$scratchRel/$name"
}

function Write-WorkFile([string]$name, [string]$text) {
    $p = Join-Path $work $name
    [IO.File]::WriteAllText($p, $text)
    return $p
}

function Compile-AndWait {
    $r = Invoke-Cli @('cliext.compile', 'run', '--async', 'true', '--format', 'json')
    [void](Wait-Editor 300)
    return $r
}

function Get-Diagnostics {
    $r = Invoke-Cli @('cliext.compile', 'diagnostics', '--warnings', 'false', '--format', 'json')
    $items = @()
    if ($r.json -ne $null) { $items = @($r.json) }
    return , $items
}

function Short([string]$s, [int]$n = 160) { $s = ($s -replace '\s+', ' ').Trim(); if ($s.Length -gt $n) { $s.Substring(0, $n) + '...' } else { $s } }

# ---------------------------------------------------------------------------------------------------------------------
# Checks: each returns a detail string, or throws (Fail) with the reason.

$checks = New-Object Collections.Generic.List[object]
function Add-Check([string]$Id, [string]$Row, [string]$What, [scriptblock]$Body) {
    $checks.Add([pscustomobject]@{ id = $Id; row = $Row; what = $What; body = $Body })
}
function Fail([string]$why) { throw [Exception]::new($why) }
function Expect([bool]$cond, [string]$why) { if (-not $cond) { Fail $why } }

# --- cliext, cliext.setup -------------------------------------------------------------------------------------------

Add-Check 'setup.status-shape' 'cliext, cliext.setup' 'cliext status names the version, the tools folder and the open setup checks' {
    $r = Invoke-Cli @('cliext', 'status', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    Expect ($r.json.version -match '^\d+\.\d+\.\d+') "no version in $(Short $r.stdout)"
    Expect (Test-Path $r.json.tools) "tools folder '$($r.json.tools)' doesn't exist"
    Expect ($r.json.PSObject.Properties.Name -contains 'setupToFix') 'no setupToFix'
    "version $($r.json.version), $(@($r.json.setupToFix).Count) setup check(s) open"
}

Add-Check 'setup.check-records' 'cliext, cliext.setup' 'cliext.setup check lists every check with an id and a status' {
    $r = Invoke-Cli @('cliext.setup', 'check', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    $recs = @($r.json)
    Expect ($recs.Count -gt 0) 'no records'
    Expect (@($recs | Where-Object { -not $_.id -or -not $_.status }).Count -eq 0) 'a record without id or status'
    "$($recs.Count) checks: " + (($recs | ForEach-Object { "$($_.id)=$($_.status)" }) -join ', ')
}

Add-Check 'setup.agent-idempotent' 'cliext, cliext.setup' 'a second cliext.setup agent run changes nothing' {
    [void](Invoke-Cli @('cliext.setup', 'agent', '--format', 'json'))
    $r = Invoke-Cli @('cliext.setup', 'agent', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    Expect (@($r.json.changes).Count -eq 0) "second run changed: $(@($r.json.changes) -join ', ')"
    'second run: no changes'
}

# --- cliext.wait, cliext.compile, cliext.edits ------------------------------------------------------------------------

Add-Check 'compile.wait-idle' 'cliext.compile, cliext.edits, cliext.wait' 'cliext.wait idle answers idle on an idle Editor' {
    $r = Invoke-Cli @('cliext.wait', 'idle', '--timeout', '60', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    "idle; $($r.ms) ms round trip"
}

Add-Check 'compile.error-diagnostics' 'cliext.compile, cliext.edits, cliext.wait' 'a compile error shows in diagnostics with file, line and code, and clears when fixed' {
    $rel = Write-ScratchFile 'CliTestBroken.cs' "public class CliTestBroken { int x = ; }`n"
    [void](Compile-AndWait)
    $d = Get-Diagnostics
    $mine = @($d | Where-Object { "$($_.file)" -match 'CliTestBroken' })
    Remove-Item (Join-Path $scratch 'CliTestBroken.cs') -Force
    [void](Compile-AndWait)
    Expect ($mine.Count -gt 0) "no diagnostic for $rel (got $($d.Count) others)"
    $first = $mine[0]
    Expect ($first.line -gt 0 -and "$($first.code)" -match '^CS\d+') "diagnostic without line or code: $(Short ($first | ConvertTo-Json -Compress))"
    $after = @((Get-Diagnostics) | Where-Object { "$($_.file)" -match 'CliTestBroken' })
    Expect ($after.Count -eq 0) 'the error is still listed after the file was removed'
    "$($first.code) at line $($first.line); cleared after removal"
}

Add-Check 'compile.edits-batch' 'cliext.compile, cliext.edits, cliext.wait' 'two files written inside an edit batch compile once, at edits end' {
    $r1 = Invoke-Cli @('cliext.edits', 'begin', '--format', 'json')
    Expect ($r1.exit -eq 0) "begin: exit $($r1.exit): $(Short $r1.stderr)"
    [void](Write-ScratchFile 'CliTestA.cs' "public static class CliTestA { public const int Value = 41; }`n")
    [void](Write-ScratchFile 'CliTestB.cs' "public static class CliTestB { public const int Value = CliTestA.Value + 1; }`n")
    $st = Invoke-Cli @('cliext.edits', 'status', '--format', 'json')
    $open = "$($st.stdout)" -match '"open"\s*:\s*true|"active"\s*:\s*true'
    $r2 = Invoke-Cli @('cliext.edits', 'end', '--async', 'true', '--format', 'json')
    [void](Wait-Editor 300)
    $d = @((Get-Diagnostics) | Where-Object { "$($_.file)" -match 'CliTest[AB]' })
    $snippet = Write-WorkFile 'b.cs' 'return System.Type.GetType("CliTestB, Assembly-CSharp") != null ? "compiled" : "missing";'
    $ev = Invoke-Cli @('script', 'eval', '--file', $snippet, '--format', 'json')
    Remove-Item (Join-Path $scratch 'CliTestA.cs'), (Join-Path $scratch 'CliTestB.cs') -Force
    [void](Compile-AndWait)
    Expect ($r2.exit -eq 0) "end: exit $($r2.exit): $(Short ($r2.stdout + $r2.stderr))"
    Expect ($d.Count -eq 0) "errors: $(Short ($d | ConvertTo-Json -Compress))"
    Expect ("$($ev.stdout)" -match 'compiled') "CliTestB isn't loaded after edits end: $(Short ($ev.stdout + $ev.stderr))"
    "batch open while writing: $open; compiled once at end"
}

# --- cliext.test ------------------------------------------------------------------------------------------------------

Add-Check 'test.empty-filter-fails' 'cliext.test' 'a filter that matches nothing fails and names the closest tests' {
    $r = Invoke-Cli @('cliext.test', 'run', '--filter', 'NoSuchTestAnywhere_xyz', '--format', 'json')
    Expect ($r.exit -ne 0) 'exit 0 for a filter that matches nothing'
    "exit $($r.exit): $(Short ($r.stderr + ' ' + $r.stdout) 140)"
}

Add-Check 'test.affected-semicolons' 'cliext.test' 'cliext.test affected splits --files on ;' {
    $r = Invoke-Cli @('cliext.test', 'affected', '--files', 'Assets/One.cs;Assets/Two.cs', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    $n = @($r.json.changedFiles).Count
    Expect ($n -eq 2) "changedFiles has $n entries: $(Short $r.stdout)"
    'two files read from one ;-separated value'
}

Add-Check 'test.run-own-result' 'cliext.test' 'an EditMode run returns its own counts' {
    $r = Invoke-Cli @('cliext.test', 'run', '--filter', 'UnityCLIExtensions.Tests.CommandSchemaTests', '--format', 'json') 300
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ($r.json.total -gt 0 -and $r.json.failed -eq 0) "counts: $(Short $r.stdout)"
    "$($r.json.passed)/$($r.json.total) passed in $([math]::Round($r.json.durationSeconds, 1))s"
}

# --- cliext.dialogs, cliext.watchdog ----------------------------------------------------------------------------------

Add-Check 'dialogs.log-and-scan' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'the dialog journal and the blocking-call scan answer' {
    $a = Invoke-Cli @('cliext.dialogs', 'log', '--tail', '5', '--format', 'json')
    $b = Invoke-Cli @('cliext.dialogs', 'scan', '--format', 'json')
    Expect ($a.exit -eq 0) "log: exit $($a.exit): $(Short $a.stderr)"
    Expect ($b.exit -eq 0) "scan: exit $($b.exit): $(Short $b.stderr)"
    "journal entries: $(@($a.json).Count); blocking calls found: $(@($b.json).Count)"
}

Add-Check 'dialogs.watchdog-roundtrip' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'the watchdog starts, reports the Editor ready, and stops (and is left as it was)' {
    $before = (Invoke-Cli @('cliext', 'status', '--format', 'json')).json.watchdog.running -eq $true
    $s = Invoke-Cli @('cliext.watchdog', 'start', '--format', 'json')
    Expect ($s.exit -eq 0) "start: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
    $state = ''
    for ($i = 0; $i -lt 10 -and $state -eq ''; $i++) {
        $rep = Invoke-Cli @('cliext.watchdog', 'report', '--format', 'json')
        if ("$($rep.stdout)" -match '"state"\s*:\s*"([^"]+)"') { $state = $Matches[1] } else { Start-Sleep -Milliseconds 500 }
    }
    $t = Invoke-Cli @('cliext.watchdog', 'stop', '--format', 'json')
    if ($before) { [void](Invoke-Cli @('cliext.watchdog', 'start', '--format', 'json')) }
    Expect ($state -eq 'ready') "report state '$state'"
    Expect ($t.exit -eq 0) "stop: exit $($t.exit): $(Short ($t.stdout + $t.stderr))"
    "state $state$(if ($before) { '; restarted, as it was running' })"
}

# --- cliext.eval, cliext.playmode -------------------------------------------------------------------------------------

Add-Check 'eval.placeholders' 'cliext.eval, cliext.playmode' 'cliext.eval fills {{placeholders}}, refuses an unfilled one, and returns the value' {
    $f = Write-WorkFile 'hello.cs' 'return "Hello {{name}} " + ({{a}} + {{b}});'
    $p = Invoke-Cli @('cliext.eval', 'run', '--file', $f, '--vars', 'name=World;a=2;b=3', '--print', 'true', '--format', 'json')
    Expect ($p.exit -eq 0 -and "$($p.stdout)" -match 'Hello World') "print: exit $($p.exit): $(Short ($p.stdout + $p.stderr))"
    $r = Invoke-Cli @('cliext.eval', 'run', '--file', $f, '--vars', 'name=World;a=2;b=3', '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'Hello World 5') "run: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $u = Invoke-Cli @('cliext.eval', 'run', '--file', $f, '--vars', 'name=World', '--format', 'json')
    Expect ($u.exit -ne 0) 'an unfilled placeholder ran'
    "filled and ran; unfilled placeholder: exit $($u.exit)"
}

Add-Check 'eval.time-scale' 'cliext.eval, cliext.playmode' 'cliext.playmode time_scale returns the previous value' {
    $a = Invoke-Cli @('cliext.playmode', 'time_scale', '--value', '0.5', '--format', 'json')
    $b = Invoke-Cli @('cliext.playmode', 'time_scale', '--value', '1', '--format', 'json')
    Expect ($a.exit -eq 0 -and $b.exit -eq 0) "exits $($a.exit), $($b.exit): $(Short ($a.stderr + $b.stderr))"
    Expect ("$($b.stdout)" -match '0\.5') "second call doesn't report 0.5 as previous: $(Short $b.stdout)"
    'set 0.5, then back to 1'
}

Add-Check 'eval.check-statics' 'cliext.eval, cliext.playmode' 'cliext.playmode check_statics answers' {
    $r = Invoke-Cli @('cliext.playmode', 'check_statics', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    "$(@($r.json).Count) static member(s) listed"
}

# --- cliext.scene -----------------------------------------------------------------------------------------------------

Add-Check 'scene.save-open' 'cliext.scene' 'cliext.scene save_open succeeds with nothing to save' {
    $r = Invoke-Cli @('cliext.scene', 'save_open', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    Short $r.stdout 100
}

Add-Check 'scene.save-open-dry' 'cliext.scene' 'save_open --dry-run lists, and an unknown --scene is NotFound' {
    $r = Invoke-Cli @('cliext.scene', 'save_open', '--dry-run', 'true', '--format', 'json')
    Expect ($r.exit -eq 0) "dry run: exit $($r.exit): $(Short $r.stderr)"
    Expect ($r.json.dryRun -eq $true -and $null -ne $r.json.wouldSave) "dry run reply has no wouldSave: $(Short $r.stdout)"
    $n = Invoke-Cli @('cliext.scene', 'save_open', '--scene', 'Assets/NoSuchScene.unity', '--format', 'json')
    Expect ($n.exit -eq 13) "unknown scene: exit $($n.exit), expected 13"
    "dry run lists $(@($r.json.wouldSave).Count); unknown scene exit 13"
}

Add-Check 'scene.test-run-names-save' 'cliext.scene' 'an unsaved scene stops cliext.test run with the exact save_open --scene command, and nothing else changes' {
    $f = Write-WorkFile 'dirty.cs' 'var go = new UnityEngine.GameObject("CliTestDirty"); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene); return go.scene.path;'
    $d = Invoke-Cli @('script', 'eval', '--file', $f, '--format', 'json')
    $scenePath = (("$($d.stdout)" -replace '"', '').Trim())
    $r = Invoke-Cli @('cliext.test', 'run', '--filter', 'UnityCLIExtensions.Tests.CommandSchemaTests', '--format', 'json')
    $dry = Invoke-Cli @('cliext.scene', 'save_open', '--dry-run', 'true', '--format', 'json')
    # Discard the probe: reopen the scene from disk.
    $re = Write-WorkFile 'reopen.cs' "UnityEditor.SceneManagement.EditorSceneManager.OpenScene(`"$scenePath`", UnityEditor.SceneManagement.OpenSceneMode.Single); return UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;"
    $back = Invoke-Cli @('script', 'eval', '--file', $re, '--format', 'json')
    Expect ($r.exit -eq 6) "test run: exit $($r.exit), expected 6"
    Expect ("$($r.stderr)" -match [regex]::Escape("cliext.scene save_open --scene $scenePath")) "refusal doesn't name the save command: $(Short $r.stderr)"
    Expect (@($dry.json.wouldSave) -contains $scenePath) "dry run doesn't list $scenePath`: $(Short $dry.stdout)"
    Expect ("$($back.stdout)" -match 'false') "the scene is still dirty after reopening: $(Short ($back.stdout + $back.stderr))"
    "refused (exit 6) naming save_open --scene $scenePath; dry run listed it; probe discarded"
}

# --- P1 commands the checks above don't call, and P1 failure modes (T2) -----------------------------------------------

Add-Check 'setup.agent-mode-toggle' 'cliext, cliext.setup' 'cliext set_agent_mode echoes its setting and is left on' {
    $off = Invoke-Cli @('cliext', 'set_agent_mode', '--enabled', 'false', '--format', 'json')
    $on = Invoke-Cli @('cliext', 'set_agent_mode', '--enabled', 'true', '--format', 'json')
    $again = Invoke-Cli @('cliext', 'set_agent_mode', '--enabled', 'true', '--format', 'json')
    Expect ($off.exit -eq 0 -and $on.exit -eq 0 -and $again.exit -eq 0) "exits $($off.exit), $($on.exit), $($again.exit): $(Short ($off.stderr + $on.stderr))"
    Expect ($off.json.setting -eq $false -and $on.json.setting -eq $true) "setting not echoed: $(Short ($off.stdout + ' ' + $on.stdout))"
    Expect ($on.json.agentMode -eq $true -and $again.json.agentMode -eq $true) "agent mode not on after --enabled true: $(Short $again.stdout)"
    "off: agentMode $($off.json.agentMode) ($($off.json.reason)); on: $($on.json.agentMode)"
}

Add-Check 'setup.apply-idempotent' 'cliext, cliext.setup' 'cliext.setup apply leaves nothing fixable, and a second apply changes nothing' {
    $a = Invoke-Cli @('cliext.setup', 'apply', '--format', 'json')
    $b = Invoke-Cli @('cliext.setup', 'apply', '--format', 'json')
    Expect ($a.exit -eq 0 -and $b.exit -eq 0) "exits $($a.exit), $($b.exit): $(Short ($a.stderr + $b.stderr))"
    $fixable = @(@($b.json) | Where-Object { $_.status -eq 'fix-available' -and -not $_.optional })
    Expect ($fixable.Count -eq 0) "still fixable after apply: $(($fixable | ForEach-Object { $_.id }) -join ', ')"
    $sig = { param($r) (@($r.json) | ForEach-Object { "$($_.id)=$($_.status)" }) -join ',' }
    Expect ((& $sig $a) -eq (& $sig $b)) "second apply changed statuses: $(& $sig $a) -> $(& $sig $b)"
    "$(@($b.json).Count) checks; nothing left to fix"
}

Add-Check 'setup.unknown-id' 'cliext, cliext.setup' 'an unknown setup check id is invalid usage, with the known ids listed' {
    $r = Invoke-Cli @('cliext.setup', 'check', '--ids', 'no-such-check', '--format', 'json')
    $a = Invoke-Cli @('cliext.setup', 'apply', '--ids', 'no-such-check', '--format', 'json')
    Expect ($r.exit -eq 2) "check: exit $($r.exit), expected 2: $(Short ($r.stdout + $r.stderr))"
    Expect ($a.exit -eq 2) "apply: exit $($a.exit), expected 2: $(Short ($a.stdout + $a.stderr))"
    Expect ("$($r.stderr)" -match 'Known:') "doesn't list the known ids: $(Short $r.stderr)"
    'exit 2 with the known ids'
}

Add-Check 'test.result-latest' 'cliext.test' 'cliext.test result returns the latest run, waits on a finished one at once, and an unknown run id is NotFound' {
    $run = Invoke-Cli @('cliext.test', 'run', '--filter', 'UnityCLIExtensions.Tests.CommandSchemaTests', '--format', 'json') 300
    Expect ($run.exit -eq 0) "run: exit $($run.exit): $(Short ($run.stdout + $run.stderr))"
    $r = Invoke-Cli @('cliext.test', 'result', '--format', 'json')
    $w = Invoke-Cli @('cliext.test', 'result', '--run-id', "$($run.json.runId)", '--wait', 'true', '--timeout', '30', '--format', 'json')
    $n = Invoke-Cli @('cliext.test', 'result', '--run-id', 'no-such-run', '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.json.runId)" -eq "$($run.json.runId)") "latest isn't the run just made: $(Short $r.stdout)"
    Expect ($w.exit -eq 0 -and $w.ms -lt 15000) "wait on a finished run: exit $($w.exit) after $($w.ms) ms"
    Expect ($n.exit -eq 13) "unknown run id: exit $($n.exit), expected 13"
    "latest = $($run.json.runId); wait answered in $($w.ms) ms; unknown id exit 13"
}

Add-Check 'dialogs.clear-log' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'cliext.dialogs clear_log empties the journal (restored afterwards)' {
    $st = (Invoke-Cli @('cliext', 'status', '--format', 'json')).json
    $journal = Join-Path $st.stateDirectory 'dialogs.jsonl'
    $backup = Join-Path $work 'dialogs.jsonl.bak'
    $had = Test-Path $journal
    if ($had) { Copy-Item $journal $backup -Force }
    try {
        $c = Invoke-Cli @('cliext.dialogs', 'clear_log', '--format', 'json')
        $l = Invoke-Cli @('cliext.dialogs', 'log', '--format', 'json')
        Expect ($c.exit -eq 0) "clear_log: exit $($c.exit): $(Short $c.stderr)"
        Expect ($l.exit -eq 0 -and "$($l.stdout)".Trim() -eq '') "journal not empty after clear_log: $(Short $l.stdout)"
    }
    finally { if ($had) { Copy-Item $backup $journal -Force } }
    "cleared; journal $(if ($had) { 'restored' } else { 'had no entries' })"
}

Add-Check 'dialogs.failure-log' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'a failed cliext command lands in the failure log with its command and parameter names (no values), and summary groups it' {
    $since = [DateTime]::UtcNow.AddSeconds(-1).ToString('o')
    $marker = 'Assets/NoSuchScene_' + [Guid]::NewGuid().ToString('N') + '.unity'
    [void](Invoke-Cli @('cliext.scene', 'save_open', '--scene', $marker, '--format', 'json'))
    $l = Invoke-Cli @('cliext.failures', 'log', '--since', $since, '--format', 'json')
    $s = Invoke-Cli @('cliext.failures', 'summary', '--since', $since, '--format', 'json')
    $b = Invoke-Cli @('cliext.failures', 'log', '--since', 'not-a-date', '--format', 'json')
    $entry = @($l.json) | Where-Object { "$($_.message)" -like "*$marker*" } | Select-Object -Last 1
    Expect ($l.exit -eq 0 -and $entry) "no entry for the failed save_open: $(Short $l.stdout)"
    Expect ($entry.command -eq 'cliext.scene save_open') "command '$($entry.command)'"
    Expect (@($entry.parameters) -contains 'scene' -and -not ("$($entry.parameters)" -match 'NoSuchScene')) "parameters $(@($entry.parameters) -join ',')"
    Expect ($s.exit -eq 0 -and (@($s.json) | Where-Object { $_.command -eq 'cliext.scene save_open' })) "summary has no save_open group: $(Short $s.stdout)"
    Expect ($b.exit -eq 2) "a bad --since: exit $($b.exit), expected 2"
    "logged as $($entry.command) [$(@($entry.parameters) -join ',')]; summary groups it; bad --since exit 2"
}

Add-Check 'dialogs.real-blocking-dialog' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'a real modal dialog: a main-thread command reports it, the watchdog names it and presses Cancel, and the Editor answers again' {
    $title = 'CliTest blocking dialog'
    $f = Write-WorkFile 'dialog.cs' "UnityEditor.EditorApplication.delayCall += () => UnityEditor.EditorUtility.DisplayDialog(`"$title`", `"Scheduled by cliext-clitest.ps1.`", `"OK`", `"Cancel`"); return `"scheduled`";"
    $s = Invoke-Cli @('script', 'eval', '--file', $f, '--format', 'json')
    Expect ($s.exit -eq 0) "scheduling: exit $($s.exit): $(Short $s.stderr)"
    Start-Sleep -Seconds 2
    $blocked = Invoke-Cli @('cliext.wait', 'idle', '--timeout', '10', '--format', 'json') 60
    $wd = Join-Path $tools 'Watchdog\cliext-watchdog.ps1'
    $status = & powershell -NoProfile -ExecutionPolicy Bypass -File $wd -Project $projectPath -Status 2>&1 | Out-String
    $click = & powershell -NoProfile -ExecutionPolicy Bypass -File $wd -Project $projectPath -Click 'Cancel' 2>&1 | Out-String
    [void](Wait-Editor 60)
    $after = Invoke-Cli @('cliext', 'status', '--format', 'json') 30
    Expect ($blocked.exit -ne 0) 'a main-thread command answered while the dialog was up'
    Expect ("$($blocked.stdout) $($blocked.stderr)" -match 'DIALOG|dialog') "the blocked command doesn't mention a dialog: $(Short ($blocked.stdout + $blocked.stderr))"
    Expect ($status -match [regex]::Escape($title)) "watchdog -Status doesn't name the dialog: $(Short $status)"
    Expect ($status -match 'Cancel') "watchdog -Status doesn't list its buttons: $(Short $status)"
    Expect ($after.exit -eq 0) "the Editor doesn't answer after Cancel: exit $($after.exit): $(Short ($after.stdout + $after.stderr))"
    "blocked command: exit $($blocked.exit); watchdog named it; Cancel pressed; Editor answers"
}

Add-Check 'eval.set-fast-roundtrip' 'cliext.eval, cliext.playmode' 'cliext.playmode set_fast turns fast Play mode on, then puts the project settings back' {
    $read = Write-WorkFile 'epmo.cs' 'return UnityEditor.EditorSettings.enterPlayModeOptionsEnabled + "/" + UnityEditor.EditorSettings.enterPlayModeOptions;'
    $before = (Invoke-Cli @('script', 'eval', '--file', $read, '--format', 'json')).stdout.Trim()
    $on = Invoke-Cli @('cliext.playmode', 'set_fast', '--enabled', 'true', '--format', 'json')
    $off = Invoke-Cli @('cliext.playmode', 'set_fast', '--enabled', 'false', '--format', 'json')
    $after = (Invoke-Cli @('script', 'eval', '--file', $read, '--format', 'json')).stdout.Trim()
    Expect ($on.exit -eq 0 -and $on.json.fastPlayMode -eq $true) "on: exit $($on.exit): $(Short ($on.stdout + $on.stderr))"
    Expect ($off.exit -eq 0 -and $off.json.fastPlayMode -eq $false) "off: exit $($off.exit): $(Short ($off.stdout + $off.stderr))"
    Expect ($before -eq $after) "settings not put back: $before -> $after"
    "on, then off; settings back to $before"
}

Add-Check 'eval.wait-until' 'cliext.eval, cliext.playmode' 'cliext.playmode wait_until stops on an exact ready, and its timeout says why a near-ready value did not count' {
    $ok = Write-WorkFile 'ready.cs' 'return "ready";'
    $near = Write-WorkFile 'near.cs' 'return "ready t=1";'
    $a = Invoke-Cli @('cliext.playmode', 'wait_until', '--file', $ok, '--timeout', '10', '--format', 'json') 40
    $b = Invoke-Cli @('cliext.playmode', 'wait_until', '--file', $near, '--timeout', '3', '--interval', '0.5', '--format', 'json') 40
    Expect ($a.exit -eq 0 -and $a.json.ready -eq $true) "exact ready: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
    Expect ($b.exit -eq 14) "near-ready: exit $($b.exit), expected 14 (timed out)"
    Expect ("$($b.stderr)" -match 'exact value') "the timeout doesn't explain: $(Short $b.stderr)"
    "ready after $($a.json.runs) run(s); near-ready timed out with the hint"
}

Add-Check 'eval.snippet-error' 'cliext.eval, cliext.playmode' 'a snippet that does not compile fails with its compiler error, not a hang or exit 0' {
    $bad = Write-WorkFile 'bad.cs' 'return UnityEngine.Object.FindObjectOfType<NoSuchTypeAnywhere>();'
    $r = Invoke-Cli @('cliext.eval', 'run', '--file', $bad, '--format', 'json') 60
    Expect ($r.exit -ne 0) 'exit 0 for a snippet that does not compile'
    Expect ("$($r.stdout) $($r.stderr)" -match 'CS\d{4}|NoSuchTypeAnywhere') "no compiler error in the reply: $(Short ($r.stdout + $r.stderr))"
    "exit $($r.exit): $(Short $r.stderr 100)"
}

Add-Check 'compile.edits-end-without-begin' 'cliext.compile, cliext.edits, cliext.wait' 'cliext.edits end without a batch warns and still compiles' {
    $r = Invoke-Cli @('cliext.edits', 'end', '--format', 'json') 300
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ("$($r.stderr) $($r.stdout)" -match 'No edit batch was open') "no warning about the missing batch: $(Short ($r.stdout + $r.stderr))"
    'warned, compiled'
}

# --- cliext.vfx -------------------------------------------------------------------------------------------------------

# One scratch graph built by vfx.edit-template; later checks edit it by the ids that edit returned.
$script:vfxPath = "$scratchRel/CliTestFx.vfx"
$script:vfxIds = @{}
$vfxTexture = 'Assets/ThirdParty/VFXTexturePack/Tx_Burst_directional_01.png'

function Get-VfxHash { (Get-FileHash (Join-Path $projectPath ($script:vfxPath -replace '/', '\'))).Hash }
function Get-VfxGraph { $r = Invoke-Cli @('cliext.vfx', 'inspect', '--path', $script:vfxPath, '--format', 'json'); Expect ($r.exit -eq 0) "inspect: exit $($r.exit): $(Short $r.stderr)"; $r.json }
function Get-VfxModel([string]$id) { $r = Invoke-Cli @('cliext.vfx', 'inspect', '--path', $script:vfxPath, '--id', $id, '--format', 'json'); if ($r.exit -eq 0) { $r.json } else { $null } }
function Get-VfxSlot($model, [string]$slot) { "$((@($model.inputs) | Where-Object { $_.name -eq $slot -or $_.name -eq "_$slot" } | Select-Object -First 1).value)" }
function Need-VfxIds { Expect ($script:vfxIds.Count -gt 0) 'no scratch graph: vfx.edit-template did not run or failed' }
function Expect-VfxFailure($r, [string]$hint, [string]$before, [string]$what) {
    Expect ($r.exit -ne 0) "${what}: exit 0"
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    if ($before) { Expect ((Get-VfxHash) -eq $before) "${what}: the .vfx on disk changed" }
}

Add-Check 'vfx.types' 'cliext.vfx' 'cliext.vfx types finds a block key by filter, and an unknown --kind is invalid usage' {
    $r = Invoke-Cli @('cliext.vfx', 'types', '--kind', 'block', '--filter', 'Single Burst', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    $keys = @(@($r.json) | ForEach-Object { $_.key })
    Expect ($keys -contains 'Spawn/Single Burst') "no Spawn/Single Burst in: $($keys -join ', ')"
    Expect (@(@($r.json) | Where-Object { $_.kind -ne 'block' }).Count -eq 0) 'records of another kind'
    $bad = Invoke-Cli @('cliext.vfx', 'types', '--kind', 'blocks', '--format', 'json')
    Expect ($bad.exit -eq 2) "unknown --kind: exit $($bad.exit), expected 2: $(Short ($bad.stdout + $bad.stderr))"
    Expect ("$($bad.stderr)" -match 'context, block, operator or parameter') "unknown --kind doesn't list the kinds: $(Short $bad.stderr)"
    "$($keys.Count) match(es); unknown --kind exit 2"
}

Add-Check 'vfx.create' 'cliext.vfx' 'cliext.vfx create makes an empty graph; an existing path and a path outside Assets fail and say what to pass' {
    $r = Invoke-Cli @('cliext.vfx', 'create', '--path', $script:vfxPath, '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.created -eq $true) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect (Test-Path (Join-Path $projectPath ($script:vfxPath -replace '/', '\'))) 'no .vfx on disk'
    $g = Get-VfxGraph
    Expect (@($g.contexts).Count -eq 0) "not empty: $(@($g.contexts).Count) context(s)"
    $before = Get-VfxHash
    $again = Invoke-Cli @('cliext.vfx', 'create', '--path', $script:vfxPath, '--format', 'json')
    Expect-VfxFailure $again 'already exists.*cliext\.vfx edit' $before 'existing path'
    Expect ($again.exit -eq 6) "existing path: exit $($again.exit), expected 6"
    $out = Invoke-Cli @('cliext.vfx', 'create', '--path', 'NotAssets/CliTest.vfx', '--format', 'json')
    Expect-VfxFailure $out "isn't under Assets/" '' 'path outside Assets'
    Expect ($out.exit -eq 2) "path outside Assets: exit $($out.exit), expected 2"
    Expect (-not (Test-Path (Join-Path $projectPath 'NotAssets'))) 'a NotAssets folder was made'
    "created; existing path exit 6, outside Assets exit 2"
}

Add-Check 'vfx.edit-template' 'cliext.vfx' 'cliext.vfx edit applies the skill burst template (with a pack texture) in one call, and inspect and check confirm it' {
    $template = Join-Path $packageRoot 'Skills~\unity-cliext-vfx\ops\burst.json'
    Expect (Test-Path $template) "no template at $template"
    Expect (Test-Path (Join-Path $projectPath ($vfxTexture -replace '/', '\'))) "no texture at $vfxTexture"
    $ops = (Get-Content $template -Raw).Replace('Assets/REPLACE/WithYourTexture.png', $vfxTexture)
    $parsed = $ops | ConvertFrom-Json # Windows PowerShell 5.1 emits the array as one object
    $total = $parsed.Count
    $f = Write-WorkFile 'vfx-burst.json' $ops
    $r = Invoke-Cli @('cliext.vfx', 'edit', '--path', $script:vfxPath, '--ops-file', $f, '--format', 'json') 300
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ($r.json.applied -eq $total -and $r.json.total -eq $total) "applied $($r.json.applied) of $($r.json.total), expected $total"
    Expect (@($r.json.warnings | Where-Object { $_.severity -ne 'info' }).Count -eq 0) "warnings: $(Short ($r.json.warnings | ConvertTo-Json -Compress))"
    foreach ($a in @($r.json.results | Where-Object { $_.alias })) { $script:vfxIds[$a.alias] = "$($a.id)" }
    foreach ($k in 'ev', 'burstSpawn', 'burstCount', 'burstInit', 'burstUpdate', 'burstOut', 'burstLife', 'burstFade') { Expect ($script:vfxIds.ContainsKey($k)) "no id for `$$k in the results" }
    $g = Get-VfxGraph
    Expect (@($g.contexts).Count -eq 5) "$(@($g.contexts).Count) contexts, expected 5"
    $out = @($g.contexts) | Where-Object { $_.id -eq $script:vfxIds.burstOut }
    Expect ((Get-VfxSlot $out 'mainTexture') -eq $vfxTexture) "mainTexture is '$(Get-VfxSlot $out 'mainTexture')'"
    Expect (@($g.eventAttributes | Where-Object { $_.event -eq 'OnPlay' }).Count -eq 1) "no OnPlay event attributes: $(Short ($g.eventAttributes | ConvertTo-Json -Compress))"
    $c = Invoke-Cli @('cliext.vfx', 'check', '--path', $script:vfxPath, '--strict', 'true', '--format', 'json')
    Expect ($c.exit -eq 0) "check --strict: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
    "$total ops applied in one call; 5 contexts; texture set; check --strict clean"
}

Add-Check 'vfx.add' 'cliext.vfx' 'cliext.vfx add puts a block in a context; an unknown block key or a missing --context fails, names the fix and saves nothing' {
    Need-VfxIds
    $r = Invoke-Cli @('cliext.vfx', 'add', '--path', $script:vfxPath, '--kind', 'block', '--type', 'Force/Linear Drag', '--context', $script:vfxIds.burstUpdate, '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $id = "$(@($r.json.results)[0].id)"
    Expect ($id -match '^\d+$') "no file id for the new block: $(Short $r.stdout)"
    $script:vfxIds['drag'] = $id
    $up = Get-VfxModel $script:vfxIds.burstUpdate
    Expect (@($up.blocks | ForEach-Object { "$($_.id)" }) -contains $id) "the Update context doesn't hold block $id"
    $before = Get-VfxHash
    $bad = Invoke-Cli @('cliext.vfx', 'add', '--path', $script:vfxPath, '--kind', 'block', '--type', 'Force/No Such Force', '--context', $script:vfxIds.burstUpdate, '--format', 'json')
    Expect-VfxFailure $bad 'cliext\.vfx types|Did you mean' $before 'unknown block key'
    $noCtx = Invoke-Cli @('cliext.vfx', 'add', '--path', $script:vfxPath, '--kind', 'block', '--type', 'Force/Linear Drag', '--format', 'json')
    Expect-VfxFailure $noCtx '--context' $before 'block without --context'
    $kind = Invoke-Cli @('cliext.vfx', 'add', '--path', $script:vfxPath, '--kind', 'blok', '--type', 'Force/Linear Drag', '--format', 'json')
    Expect-VfxFailure $kind 'context, block, operator or parameter' $before 'unknown --kind'
    "added block $id; unknown key exit $($bad.exit), no --context exit $($noCtx.exit), bad kind exit $($kind.exit); file unchanged"
}

Add-Check 'vfx.set' 'cliext.vfx' 'cliext.vfx set changes a slot and a setting; a bad value, an unknown slot or setting, or no value fails, names the fix and saves nothing' {
    Need-VfxIds
    $r = Invoke-Cli @('cliext.vfx', 'set', '--path', $script:vfxPath, '--id', $script:vfxIds.burstCount, '--slot', 'Count', '--value', '3', '--format', 'json')
    Expect ($r.exit -eq 0) "slot: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $s = Invoke-Cli @('cliext.vfx', 'set', '--path', $script:vfxPath, '--id', $script:vfxIds.burstOut, '--setting', 'blendMode', '--value', 'Alpha', '--format', 'json')
    Expect ($s.exit -eq 0) "setting: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
    Expect ((Get-VfxSlot (Get-VfxModel $script:vfxIds.burstCount) 'Count') -eq '3') "Count is '$(Get-VfxSlot (Get-VfxModel $script:vfxIds.burstCount) 'Count')'"
    $blend = "$((@((Get-VfxModel $script:vfxIds.burstOut).settings) | Where-Object { $_.name -eq 'blendMode' }).value)"
    Expect ($blend -eq 'Alpha') "blendMode is '$blend'"
    $before = Get-VfxHash
    $cases = @(
        @(@('--id', $script:vfxIds.burstCount, '--slot', 'Count', '--value', 'abc'), 'a number', 'non-numeric Count'),
        @(@('--id', $script:vfxIds.burstOut, '--setting', 'blendMode', '--value', 'Glowy'), 'Use one of:.*Additive', 'unknown enum name'),
        @(@('--id', $script:vfxIds.burstCount, '--slot', 'Countt', '--value', '1'), 'It has: Count', 'unknown slot'),
        @(@('--id', $script:vfxIds.burstOut, '--setting', 'blendMood', '--value', 'Alpha'), 'It has:.*blendMode', 'unknown setting'),
        @(@('--id', $script:vfxIds.burstCount, '--slot', 'Count'), 'needs a value', 'no --value'),
        @(@('--id', $script:vfxIds.burstOut, '--slot', 'mainTexture', '--value', 'Assets/NoSuchTexture.png'), 'No Texture2D at', 'missing texture'),
        @(@('--id', '999', '--slot', 'Count', '--value', '1'), 'cliext\.vfx inspect', 'unknown id')
    )
    $seen = @()
    foreach ($c in $cases) {
        $f = Invoke-Cli (@('cliext.vfx', 'set', '--path', $script:vfxPath) + $c[0] + @('--format', 'json'))
        Expect-VfxFailure $f $c[1] $before $c[2]
        Expect ("$($f.stderr)" -notmatch '   at ') "$($c[2]): a stack trace instead of a message: $(Short $f.stderr)"
        $seen += "$($c[2]) $($f.exit)"
    }
    Expect ((Get-VfxSlot (Get-VfxModel $script:vfxIds.burstCount) 'Count') -eq '3') 'Count changed after the failed sets'
    "Count 3, blendMode Alpha; failures: $($seen -join ', ')"
}

Add-Check 'vfx.link-unlink' 'cliext.vfx' 'cliext.vfx unlink and link a flow link; unlinking a link that is not there, or a link contexts refuse, fails and saves nothing' {
    Need-VfxIds
    $up = $script:vfxIds.burstUpdate; $out = $script:vfxIds.burstOut
    $u = Invoke-Cli @('cliext.vfx', 'unlink', '--path', $script:vfxPath, '--from', $up, '--to', $out, '--format', 'json')
    Expect ($u.exit -eq 0) "unlink: exit $($u.exit): $(Short ($u.stdout + $u.stderr))"
    Expect (@((Get-VfxModel $up).flowTo | ForEach-Object { "$($_.id)" }) -notcontains $out) 'Update still flows into the output after unlink'
    $before = Get-VfxHash
    $again = Invoke-Cli @('cliext.vfx', 'unlink', '--path', $script:vfxPath, '--from', $up, '--to', $out, '--format', 'json')
    Expect-VfxFailure $again 'no flow link.*inspect' $before 'unlink of a missing link'
    $l = Invoke-Cli @('cliext.vfx', 'link', '--path', $script:vfxPath, '--from', $up, '--to', $out, '--format', 'json')
    Expect ($l.exit -eq 0) "link: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
    Expect (@((Get-VfxModel $up).flowTo | ForEach-Object { "$($_.id)" }) -contains $out) 'Update does not flow into the output after link'
    $before = Get-VfxHash
    $bad = Invoke-Cli @('cliext.vfx', 'link', '--path', $script:vfxPath, '--from', $out, '--to', $script:vfxIds.ev, '--format', 'json')
    Expect-VfxFailure $bad "can't flow into" $before 'output into event'
    $slot = Invoke-Cli @('cliext.vfx', 'link', '--path', $script:vfxPath, '--from', "$($script:vfxIds.ev):nope", '--to', "$($script:vfxIds.burstLife):A", '--format', 'json')
    Expect-VfxFailure $slot 'has no (output|input) slot|has no slots' $before 'unknown data slot'
    "unlinked and relinked; missing link exit $($again.exit), refused link exit $($bad.exit), unknown slot exit $($slot.exit)"
}

Add-Check 'vfx.remove' 'cliext.vfx' 'cliext.vfx remove takes a block out; an unknown id fails, points at inspect and saves nothing' {
    Need-VfxIds
    Expect ($script:vfxIds.ContainsKey('drag')) 'no block from vfx.add to remove'
    $r = Invoke-Cli @('cliext.vfx', 'remove', '--path', $script:vfxPath, '--id', $script:vfxIds.drag, '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ($null -eq (Get-VfxModel $script:vfxIds.drag)) 'the block is still there'
    Expect (@((Get-VfxModel $script:vfxIds.burstUpdate).blocks).Count -eq 0) 'the Update context still has blocks'
    $before = Get-VfxHash
    $bad = Invoke-Cli @('cliext.vfx', 'remove', '--path', $script:vfxPath, '--id', $script:vfxIds.drag, '--format', 'json')
    Expect-VfxFailure $bad 'cliext\.vfx inspect' $before 'unknown id'
    "removed $($script:vfxIds.drag); again: exit $($bad.exit)"
}

Add-Check 'vfx.batch-rollback' 'cliext.vfx' 'an edit batch with one bad op saves nothing, on disk or in memory, and the next good edit saves only its own change' {
    Need-VfxIds
    $before = Get-VfxHash
    $g0 = Get-VfxGraph
    $ops = @(
        @{ op = 'add'; kind = 'context'; type = 'Spawn'; as = 'extra' },
        @{ op = 'set'; id = $script:vfxIds.burstCount; slot = 'Count'; value = '9' },
        @{ op = 'remove'; id = $script:vfxIds.burstFade },
        @{ op = 'set'; id = $script:vfxIds.burstInit; setting = 'capacity'; value = '7' },
        @{ op = 'add'; kind = 'block'; type = 'Spawn/No Such Block'; context = '$extra' }
    ) | ConvertTo-Json -Compress
    $f = Write-WorkFile 'vfx-bad-batch.json' $ops
    $r = Invoke-Cli @('cliext.vfx', 'edit', '--path', $script:vfxPath, '--ops-file', $f, '--format', 'json')
    Expect-VfxFailure $r 'Op 4 \(add\) failed.*Nothing was saved' $before 'bad batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 4 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
    $g1 = Get-VfxGraph
    Expect (@($g1.contexts).Count -eq @($g0.contexts).Count) "contexts in memory: $(@($g0.contexts).Count) -> $(@($g1.contexts).Count)"
    $good = Write-WorkFile 'vfx-good.json' ('[' + (@{ op = 'set'; id = $script:vfxIds.burstLife; slot = 'B'; value = '0.25' } | ConvertTo-Json -Compress) + ']')
    $ok = Invoke-Cli @('cliext.vfx', 'edit', '--path', $script:vfxPath, '--ops-file', $good, '--format', 'json')
    Expect ($ok.exit -eq 0) "good edit: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
    $g2 = Get-VfxGraph
    Expect (@($g2.contexts).Count -eq @($g0.contexts).Count) "the failed batch's context was saved: $(@($g2.contexts).Count) contexts"
    Expect ($null -ne (Get-VfxModel $script:vfxIds.burstFade)) "the failed batch's remove was saved: block $($script:vfxIds.burstFade) is gone"
    Expect ((Get-VfxSlot (Get-VfxModel $script:vfxIds.burstCount) 'Count') -eq '3') "the failed batch's Count was saved: $(Get-VfxSlot (Get-VfxModel $script:vfxIds.burstCount) 'Count')"
    $cap = "$((@((Get-VfxModel $script:vfxIds.burstInit).settings) | Where-Object { $_.name -eq 'capacity' }).value)"
    Expect ($cap -eq '64') "the failed batch's capacity was saved: $cap"
    Expect ((Get-VfxSlot (Get-VfxModel $script:vfxIds.burstLife) 'B') -eq '0.25') "the good edit's value isn't there"
    'failed at op 4, file unchanged; next edit saved only its own change'
}

Add-Check 'vfx.check' 'cliext.vfx' 'cliext.vfx check passes a good graph and fails (exit 6) on a burst that spawns nothing, naming it' {
    Need-VfxIds
    $ok = Invoke-Cli @('cliext.vfx', 'check', '--path', $script:vfxPath, '--format', 'json')
    Expect ($ok.exit -eq 0) "good graph: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
    $z = Invoke-Cli @('cliext.vfx', 'set', '--path', $script:vfxPath, '--id', $script:vfxIds.burstCount, '--slot', 'Count', '--value', '0', '--format', 'json')
    Expect ($z.exit -eq 0) "set Count 0: exit $($z.exit)"
    Expect (@($z.json.warnings).Count -gt 0) 'the edit result has no warning for Count 0'
    $warn = Invoke-Cli @('cliext.vfx', 'check', '--path', $script:vfxPath, '--format', 'json')
    $bad = Invoke-Cli @('cliext.vfx', 'check', '--path', $script:vfxPath, '--strict', 'true', '--format', 'json')
    [void](Invoke-Cli @('cliext.vfx', 'set', '--path', $script:vfxPath, '--id', $script:vfxIds.burstCount, '--slot', 'Count', '--value', '3', '--format', 'json'))
    # Count 0 is a warning: exit 0 by default, exit 6 with --strict (as the help says).
    Expect ($warn.exit -eq 0) "Count 0 without --strict: exit $($warn.exit), expected 0"
    $w = @($warn.json.warnings | Where-Object { $_.severity -eq 'warning' -and "$($_.id)" -eq $script:vfxIds.burstCount })
    Expect ($w.Count -eq 1 -and "$($w[0].problem)" -match 'Count') "no warning on the burst block naming Count: $(Short $warn.stdout)"
    Expect ($bad.exit -eq 6) "Count 0 with --strict: exit $($bad.exit), expected 6"
    Expect ("$($bad.stderr)" -match 'warning') "the --strict failure doesn't count the warnings: $(Short $bad.stderr)"
    "good graph exit 0; Count 0 warns (exit 0), --strict exit 6: $(Short $w[0].problem 80)"
}

Add-Check 'vfx.missing-asset' 'cliext.vfx' 'every cliext.vfx command given a .vfx that does not exist exits 13 at once, pointing at cliext.vfx create' {
    $none = 'Assets/_CliextTests/CliTest/NoSuch.vfx'
    $cmds = @(
        @('inspect'), @('check'), @('add', '--kind', 'context', '--type', 'Spawn'), @('remove', '--id', '1'),
        @('set', '--id', '1', '--slot', 'Count', '--value', '1'), @('link', '--from', '1', '--to', '2'), @('unlink', '--from', '1', '--to', '2')
    )
    $f = Write-WorkFile 'vfx-one.json' '[{"op":"add","kind":"context","type":"Spawn"}]'
    $bad = @()
    foreach ($c in $cmds + , @('edit', '--ops-file', $f)) {
        $r = Invoke-Cli (@('cliext.vfx', $c[0], '--path', $none) + @($c | Select-Object -Skip 1) + @('--format', 'json')) 60
        if ($r.exit -ne 13 -or "$($r.stderr)" -notmatch 'cliext\.vfx create' -or $r.ms -gt 30000) { $bad += "$($c[0]): exit $($r.exit) in $($r.ms) ms ($(Short $r.stderr 60))" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    Expect (-not (Test-Path (Join-Path $projectPath ($none -replace '/', '\')))) 'a .vfx was made at the missing path'
    "all $($cmds.Count + 1) exit 13 naming cliext.vfx create"
}

Add-Check 'vfx.play-mode' 'cliext.vfx' 'in Play mode an edit applies and stays after Play mode ends (graphs are assets), and a bad op still saves nothing' {
    Need-VfxIds
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $ok = Invoke-Cli @('cliext.vfx', 'set', '--path', $script:vfxPath, '--id', $script:vfxIds.burstLife, '--slot', 'A', '--value', '0.07', '--format', 'json')
        Expect ($ok.exit -eq 0) "set in Play mode: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
        $before = Get-VfxHash
        $bad = Invoke-Cli @('cliext.vfx', 'set', '--path', $script:vfxPath, '--id', $script:vfxIds.burstLife, '--slot', 'A', '--value', 'x', '--format', 'json')
        Expect-VfxFailure $bad 'a number' $before 'bad value in Play mode'
    }
    finally {
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
    }
    $mode = Write-WorkFile 'vfx-mode.cs' 'return UnityEditor.EditorApplication.isPlaying ? "playing" : "edit";'
    $m = Invoke-Cli @('script', 'eval', '--file', $mode, '--format', 'json')
    Expect ("$($m.stdout)" -match 'edit') "still in Play mode: $(Short $m.stdout)"
    Expect ((Get-VfxSlot (Get-VfxModel $script:vfxIds.burstLife) 'A') -eq '0.07') "the Play mode edit didn't stay: A is '$(Get-VfxSlot (Get-VfxModel $script:vfxIds.burstLife) 'A')'"
    'edit applied in Play mode and kept after it; bad value saved nothing; back in Edit mode'
}

# --- Cross-cutting ----------------------------------------------------------------------------------------------------

$batchEdits = @(
    @('cliext.vfx', 'edit', '--path', 'Assets/None.vfx'),
    @('cliext.shadergraph', 'edit', '--path', 'Assets/None.shadergraph'),
    @('cliext.animator', 'edit', '--path', 'Assets/None.controller'),
    @('cliext.anim', 'edit_clips', '--path', 'Assets/None.fbx'),
    @('cliext.anim', 'edit_clip', '--path', 'Assets/None.anim'),
    @('cliext.timeline', 'edit', '--path', 'Assets/None.playable'),
    @('cliext.input', 'edit', '--path', 'Assets/None.inputactions'),
    @('cliext.uitk', 'edit', '--path', 'Assets/None.uxml'),
    @('cliext.splines', 'edit', '--object', 'NoSuchObject'),
    @('cliext.addressables', 'edit')
)

Add-Check 'xcut.batch-no-ops' 'Cross-cutting: either/or parameters' 'every batch edit given neither --ops nor --ops-file exits 2 (invalid usage)' {
    $bad = @()
    foreach ($b in $batchEdits) {
        $r = Invoke-Cli ($b + @('--format', 'json'))
        if ($r.exit -ne 2) { $bad += "$($b[0]) $($b[1]): exit $($r.exit)" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    "all $($batchEdits.Count) exit 2"
}

Add-Check 'xcut.batch-both-ops' 'Cross-cutting: either/or parameters' 'every batch edit given both --ops and --ops-file exits 2 instead of picking one' {
    $f = Write-WorkFile 'ops.json' '[{"op":"none"}]'
    $bad = @()
    foreach ($b in $batchEdits) {
        $r = Invoke-Cli ($b + @('--ops', '[{"op":"none"}]', '--ops-file', $f, '--format', 'json'))
        if ($r.exit -ne 2) { $bad += "$($b[0]) $($b[1]): exit $($r.exit)" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    "all $($batchEdits.Count) exit 2"
}

Add-Check 'xcut.unknown-parameter' 'Cross-cutting: either/or parameters' 'a misspelled parameter is rejected, not ignored' {
    $r = Invoke-Cli @('cliext.wait', 'idle', '--timout', '5', '--format', 'json')
    Expect ($r.exit -ne 0) "exit 0: '--timout' was ignored ($(Short $r.stdout 80))"
    "exit $($r.exit): $(Short ($r.stderr + ' ' + $r.stdout) 120)"
}

Add-Check 'xcut.exit-codes-distinguish-reload' 'Cross-cutting: exit codes' 'a reply dropped by a domain reload is told apart from a failed command' {
    # A synchronous compile that succeeds reloads the domain, which usually drops the reply.
    $rel = Write-ScratchFile 'CliTestReload.cs' "public static class CliTestReload { public const int V = $(Get-Random); }`n"
    $ok = Invoke-Cli @('cliext.compile', 'run', '--format', 'json') 300
    [void](Wait-Editor 300)
    [IO.File]::WriteAllText((Join-Path $scratch 'CliTestReload.cs'), "public static class CliTestReload { int x = ; }`n")
    $bad = Invoke-Cli @('cliext.compile', 'run', '--format', 'json') 300
    Remove-Item (Join-Path $scratch 'CliTestReload.cs') -Force
    [void](Compile-AndWait)
    $okCode = ''; if ("$($ok.stdout)" -match '"code"\s*:\s*"([A-Z_]+)"') { $okCode = $Matches[1] }
    $badCode = ''; if ("$($bad.stdout)" -match '"code"\s*:\s*"([A-Z_]+)"') { $badCode = $Matches[1] }
    $detail = "success: exit $($ok.exit) $okCode; failed compile: exit $($bad.exit) $badCode"
    Expect ($bad.exit -ne 0) "a failed compile exited 0 ($detail)"
    Expect (($ok.exit -ne $bad.exit) -or ($okCode -ne $badCode)) "same exit and code for both ($detail)"
    $detail
}

Add-Check 'xcut.result-shape' 'Cross-cutting: result format' 'with --format json, success puts JSON on stdout, and every failure exits non-zero with its reason on stderr' {
    # The Pipeline CLI prints a success's result bare, a failure's reason on stderr (usually an `error:` line; stdout empty,
    # or the command's partial result), and a dropped reply as a {success:false, errors:[{code}]} envelope on stdout.
    # The skill documents those; this pins them so a change shows up.
    $s = Invoke-Cli @('cliext', 'status', '--format', 'json')
    $f = Invoke-Cli @('cliext.vfx', 'inspect', '--path', 'Assets/None.vfx', '--format', 'json')
    $c = Invoke-Cli @('cliext.nosuchgroup', 'nosuch', '--format', 'json')
    function Describe($r) {
        $out = if ($r.json -eq $null) { if ($r.stdout.Trim()) { 'text' } else { 'empty' } } elseif ($r.json.PSObject.Properties.Name -contains 'success') { 'envelope' } else { 'JSON' }
        "exit $($r.exit), stdout $out, stderr $(if ($r.stderr -match 'error:') { 'error line' } elseif ($r.stderr.Trim()) { 'text' } else { 'none' })"
    }
    $detail = "success: $(Describe $s); command failure: $(Describe $f); unknown command: $(Describe $c)"
    Expect ($s.exit -eq 0 -and $s.json -ne $null) $detail
    foreach ($r in @($f, $c)) {
        Expect ($r.exit -ne 0) $detail
        Expect ([bool]$r.stderr.Trim()) $detail
        Expect (-not $r.stdout.Trim() -or $r.json -ne $null) $detail
    }
    $detail
}

Add-Check 'xcut.skill-freshness' 'Cross-cutting: skill freshness' 'the installed skills match the package copies' {
    $src = Join-Path $packageRoot 'Skills~'
    $dst = Join-Path $projectPath '.claude\skills'
    $stale = @()
    foreach ($skill in Get-ChildItem $src -Directory) {
        $installed = Join-Path $dst $skill.Name
        if (-not (Test-Path $installed)) { continue } # optional skills are installed only when their package is
        foreach ($file in Get-ChildItem $skill.FullName -Recurse -File) {
            $rel = $file.FullName.Substring($skill.FullName.Length)
            $other = Join-Path $installed $rel
            $norm = { param($p) ((Get-Content $p -Raw) -replace "`r", '' -split "`n" | Where-Object { $_ -notmatch '^<!-- unity-cliext[\w-]* \d' }) -join "`n" }
            if (-not (Test-Path $other)) { $stale += "$($skill.Name)$rel missing"; continue }
            # The installer fills {{cliext-tools}} with this package's Tools~ path.
            $want = (& $norm $file.FullName).Replace('{{cliext-tools}}', ($tools -replace '\\', '/'))
            if ($want.Trim() -ne (& $norm $other).Trim()) { $stale += "$($skill.Name)$rel differs" }
        }
    }
    Expect ($stale.Count -eq 0) ($stale -join '; ')
    'installed skills match Skills~'
}

# --- Environment ------------------------------------------------------------------------------------------------------

Add-Check 'env.ps51-inline-json' 'Environment' 'the core skill warns about inline JSON under Windows PowerShell 5.1, where the quotes are lost' {
    # What an agent gets typing inline JSON in Windows PowerShell 5.1.
    $ErrorActionPreference = 'Continue'
    $out = & $unity command --project-path $projectPath cliext.vfx edit --path Assets/None.vfx --ops '[{"op":"add"}]' --format json 2>&1 | Out-String
    $lost = $out -match 'valid JSON|Unexpected|Invalid'
    $skill = Get-Content (Join-Path $packageRoot 'Skills~\unity-cliext\SKILL.md') -Raw
    $warned = $skill -match '(?s)PowerShell 5\.1.{0,60}(JSON|quotes)'
    Expect $warned 'the core skill does not warn about inline JSON in Windows PowerShell 5.1'
    "quotes lost in PS $($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor): $lost; skill warns: $warned"
}

Add-Check 'env.ps51-quoted-code' 'Environment' 'the core skill says to pass C# snippets and quoted text as files under Windows PowerShell 5.1' {
    $ErrorActionPreference = 'Continue'
    $out = & $unity command --project-path $projectPath script eval --code 'return "a b";' 2>&1 | Out-String
    $broken = $out -notmatch '(^|\s)a b(\s|$)'
    $skill = Get-Content (Join-Path $packageRoot 'Skills~\unity-cliext\SKILL.md') -Raw
    $warned = $skill -match '(?s)script eval --code.{0,200}--file'
    Expect $warned "inline --code broken: $broken; the core skill does not say to use --file for snippets and quoted text"
    "inline --code broken: $broken; skill says to use files: $warned"
}

# --- T3 environment (run with -T3Ready, before agent scenario runs) -------------------------------------------------

Add-Check 't3.environment' 'T3 environment' 'the Editor is set up the way agents get it: setup applied, agent mode on, watchdog running, skills current' {
    $st = (Invoke-Cli @('cliext', 'status', '--format', 'json')).json
    $problems = @()
    $open = @($st.setupToFix | Where-Object { -not $_.optional -and $_.status -eq 'fix-available' } | ForEach-Object { $_.id })
    if ($open.Count) { $problems += "setup checks with a fix available: $($open -join ', ') (cliext.setup apply)" }
    $manual = @($st.setupToFix | Where-Object { -not $_.optional -and $_.status -eq 'manual' } | ForEach-Object { $_.id })
    if (-not $st.agentMode) { $problems += 'agent mode off (cliext set_agent_mode --enabled true)' }
    if (-not $st.watchdog.running) { $problems += 'watchdog not running (cliext.watchdog start)' }
    $agent = (Invoke-Cli @('cliext.setup', 'agent', '--format', 'json')).json
    if (-not $agent.current) { $problems += "skills were not current for $($agent.version): $(@($agent.changes) -join ', ')" }
    Expect ($problems.Count -eq 0) ($problems -join '; ')
    "ready for T3 on $($st.version)$(if ($manual.Count) { "; manual checks left: $($manual -join ', ')" }); shell PowerShell $($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor)"
}

# ---------------------------------------------------------------------------------------------------------------------
# Run

$probe = Invoke-Cli @('cliext', 'status', '--format', 'json') 30
if ($probe.exit -ne 0) { Write-Output "No Editor answered for $projectPath (exit $($probe.exit)): $(Short ($probe.stdout + $probe.stderr))"; exit 2 }

$results = New-Object Collections.Generic.List[object]
$selected = $checks | Where-Object { $_.id -match $Filter -and (($_.id -like 't3.*') -eq [bool]$T3Ready) }
foreach ($c in $selected) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $detail = & $c.body; $ok = $true }
    catch { $detail = $_.Exception.Message; $ok = $false }
    $results.Add([pscustomobject]@{ id = $c.id; row = $c.row; what = $c.what; pass = $ok; detail = "$detail"; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) })
    Write-Output ("{0,-4} {1,-36} {2,6}s  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $c.id, [math]::Round($sw.Elapsed.TotalSeconds, 1), (Short "$detail" 150))
}

if (-not $Keep) {
    if (Test-Path $scratch) {
        $cleanup = Write-WorkFile 'cleanup.cs' "UnityEditor.AssetDatabase.DeleteAsset(`"$scratchRel`"); if (UnityEditor.AssetDatabase.IsValidFolder(`"Assets/_CliextTests`") && UnityEditor.AssetDatabase.FindAssets(`"`", new[] { `"Assets/_CliextTests`" }).Length == 0) UnityEditor.AssetDatabase.DeleteAsset(`"Assets/_CliextTests`"); return `"ok`";"
        [void](Invoke-Cli @('script', 'eval', '--file', $cleanup))
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { -not $_.pass }).Count
Write-Output ("{0} of {1} checks passed" -f ($results.Count - $failed), $results.Count)

# T1 coverage: which commands of each tracker row this run called at least once.
$coverage = @()
if (-not $T3Ready) {
    $rows = [ordered]@{
        'cliext, cliext.setup' = '^cliext( |$)|^cliext\.setup '; 'cliext.compile, cliext.edits, cliext.wait' = '^cliext\.(compile|edits|wait) '
        'cliext.test' = '^cliext\.test '; 'cliext.dialogs, cliext.watchdog, cliext.failures' = '^cliext\.(dialogs|watchdog|failures) '
        'cliext.eval, cliext.playmode' = '^cliext\.(eval|playmode) '; 'cliext.scene' = '^cliext\.scene '
        # P2 authoring rows
        'cliext.vfx' = '^cliext\.vfx '; 'cliext.shadergraph' = '^cliext\.shadergraph '
        'cliext.animator, cliext.anim' = '^cliext\.(animator|anim) '; 'cliext.timeline' = '^cliext\.timeline '
        'cliext.input' = '^cliext\.input '; 'cliext.uitk' = '^cliext\.uitk '
    }
    $q = Invoke-Cli @('--query', 'cliext', '--format', 'json') 60
    $all = @()
    if ($q.json -ne $null) { $all = @($q.json.data.commands | ForEach-Object { $_.command }) }
    Write-Output 'T1 coverage (P1 and P2 rows; commands called at least once):'
    foreach ($row in $rows.Keys) {
        $cmds = @($all | Where-Object { $_ -match $rows[$row] } | Sort-Object)
        $hit = @($cmds | Where-Object { $invoked.Contains($_) })
        $miss = @($cmds | Where-Object { -not $invoked.Contains($_) })
        $coverage += [pscustomobject]@{ row = $row; covered = $hit.Count; total = $cmds.Count; missing = $miss }
        Write-Output ("  {0,-44} {1,2} of {2,-2} {3}" -f $row, $hit.Count, $cmds.Count, $(if ($miss.Count) { 'missing: ' + ($miss -join ', ') } else { '' }))
    }
}
if ($Out) { [pscustomobject]@{ checks = $results; coverage = $coverage } | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding UTF8 }
if ($failed -gt 0) { exit 1 } else { exit 0 }

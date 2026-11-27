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
    # clear, with the log backed up and put back
    $log = Join-Path (Invoke-Cli @('cliext', 'status', '--format', 'json')).json.stateDirectory 'failures.jsonl'
    $bak = Join-Path $work 'failures.jsonl.bak'
    Copy-Item $log $bak -Force
    try {
        $c = Invoke-Cli @('cliext.failures', 'clear', '--format', 'json')
        $after = Invoke-Cli @('cliext.failures', 'log', '--format', 'json')
        Expect ($c.exit -eq 0 -and "$($after.stdout)".Trim() -eq '') "clear: exit $($c.exit); log after: $(Short $after.stdout)"
    }
    finally { Copy-Item $bak $log -Force }
    "logged as $($entry.command) [$(@($entry.parameters) -join ',')]; summary groups it; bad --since exit 2; clear empties it (restored)"
}

# Backs up field-notes.jsonl, runs $Body, and puts it back, so the note checks leave the project's notes as they were.
function Invoke-WithNotesRestored([scriptblock]$Body) {
    $notesFile = Join-Path (Invoke-Cli @('cliext', 'status', '--format', 'json')).json.stateDirectory 'field-notes.jsonl'
    $bak = Join-Path $work ('field-notes-' + [Guid]::NewGuid().ToString('N') + '.bak')
    $had = Test-Path $notesFile
    if ($had) { Copy-Item $notesFile $bak -Force }
    try { & $Body $notesFile }
    finally { if ($had) { Copy-Item $bak $notesFile -Force } elseif (Test-Path $notesFile) { Remove-Item $notesFile -Force } }
}

Add-Check 'failures.note' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'cliext.failures note records a note from flags and from --file (quotes and apostrophes intact); an unknown kind or no --what exits 2; log --notes, summary and clear --notes see it (notes restored)' {
    Invoke-WithNotesRestored {
        param($notesFile)
        $since = [DateTime]::UtcNow.AddSeconds(-1).ToString('o')
        $marker = 'clitest-note-' + [Guid]::NewGuid().ToString('N')
        $a = Invoke-Cli @('cliext.failures', 'note', '--kind', 'workaround', '--command', 'cliext.vfx edit', '--what', "$marker flags", '--workaround', 'edited the file', '--format', 'json')
        Expect ($a.exit -eq 0 -and $a.json.recorded -and $a.json.kind -eq 'workaround' -and $a.json.what -eq "$marker flags" -and $a.json.command -eq 'cliext.vfx edit') "flags: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        # The reason --file exists: PowerShell 5.1 strips the quotes inside a native command's arguments.
        $text = "It's the `"bounds`" op; $marker file"
        $file = Write-WorkFile 'note.json' (@{ kind = 'Missing'; command = 'cliext.vfx'; what = $text } | ConvertTo-Json -Compress)
        $b = Invoke-Cli @('cliext.failures', 'note', '--file', $file, '--format', 'json')
        Expect ($b.exit -eq 0 -and $b.json.kind -eq 'missing' -and $b.json.what -eq $text) "--file: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
        $c = Invoke-Cli @('cliext.failures', 'note', '--kind', 'annoyance', '--what', 'x', '--format', 'json')
        Expect ($c.exit -eq 2) "unknown kind: exit $($c.exit), expected 2"
        Expect ("$($c.stderr) $($c.stdout)" -match 'bug, workaround, missing, docs, slow') "the unknown kind's error doesn't list the kinds: $(Short ($c.stderr + $c.stdout))"
        $d = Invoke-Cli @('cliext.failures', 'note', '--kind', 'bug', '--format', 'json')
        Expect ($d.exit -eq 2) "no --what: exit $($d.exit), expected 2"
        $e = Invoke-Cli @('cliext.failures', 'note', '--file', (Join-Path $work 'no-such-note.json'), '--format', 'json')
        Expect ($e.exit -eq 13) "a missing --file: exit $($e.exit), expected 13"
        $l = Invoke-Cli @('cliext.failures', 'log', '--notes', 'true', '--since', $since, '--format', 'json')
        $mine = @(@($l.json) | Where-Object { "$($_.what)" -like "*$marker*" })
        Expect ($l.exit -eq 0 -and $mine.Count -eq 2) "log --notes shows $($mine.Count) of the 2 notes: $(Short $l.stdout)"
        Expect (@($mine | Where-Object { $_.unity -and $_.package -and $_.time }).Count -eq 2) "a note without its time or versions: $(Short ($mine | ConvertTo-Json -Compress))"
        $s = Invoke-Cli @('cliext.failures', 'summary', '--since', $since, '--format', 'json')
        Expect ($s.exit -eq 0 -and @(@($s.json) | Where-Object { $_.source -eq 'note' -and $_.kind -eq 'missing' -and $_.command -eq 'cliext.vfx' }).Count -gt 0) "summary has no note group: $(Short $s.stdout)"
        Expect (@(@($s.json) | Where-Object { $_.source -eq 'failure' -and $_.command -eq 'cliext.failures note' }).Count -gt 0) "summary has no failure group for the refused notes: $(Short $s.stdout)"
        $x = Invoke-Cli @('cliext.failures', 'clear', '--notes', 'true', '--format', 'json')
        $after = Invoke-Cli @('cliext.failures', 'log', '--notes', 'true', '--format', 'json')
        Expect ($x.exit -eq 0 -and "$($x.json.file)" -like '*field-notes.jsonl' -and "$($after.stdout)".Trim() -eq '') "clear --notes: exit $($x.exit), file '$($x.json.file)'; after: $(Short $after.stdout)"
        Expect (Test-Path (Join-Path (Split-Path $notesFile) 'failures.jsonl')) 'clear --notes also deleted failures.jsonl'
        "flags and --file recorded (quotes kept); unknown kind and no --what exit 2, missing file 13; log --notes, summary and clear --notes see them (restored)"
    }
}

Add-Check 'failures.field-report' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'cliext-field-report.ps1 writes the Markdown and JSON report, and says a note made outside any transcript is unmatched (notes restored)' {
    Invoke-WithNotesRestored {
        param($notesFile)
        $marker = 'clitest-report-note-' + [Guid]::NewGuid().ToString('N')
        $n = Invoke-Cli @('cliext.failures', 'note', '--kind', 'docs', '--command', 'cliext.failures note', '--what', $marker, '--format', 'json')
        Expect ($n.exit -eq 0) "note: exit $($n.exit): $(Short $n.stderr)"
        $out = Join-Path $work 'field-report'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        # Under -ErrorAction Stop, Windows PowerShell turns the script's stderr into errors; keep it as text.
        $ErrorActionPreference = 'Continue'
        $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools 'cliext-field-report.ps1') -Project $projectPath -Days 1 -Out $out 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        Expect ($exit -eq 0) "exit $exit`: $(Short $log)"
        $md = Join-Path $out 'field-report.md'; $js = Join-Path $out 'field-report.json'
        Expect ((Test-Path $md) -and (Test-Path $js)) "no report in $out"
        $r = [IO.File]::ReadAllText($js) | ConvertFrom-Json
        foreach ($k in 'failedByCause', 'failedByCommand', 'packageReads', 'opFileEdits', 'helpRepeats', 'evals', 'skillsBeforeFirstCall', 'notes', 'failures', 'sessions') {
            Expect ($r.PSObject.Properties.Name -contains $k) "field-report.json has no '$k'"
        }
        $item = @($r.notes.items) | Where-Object { $_.what -eq $marker } | Select-Object -First 1
        Expect ($item -and $item.unmatched -and -not $item.context) "the note is missing or claimed matched: $(Short ($item | ConvertTo-Json -Compress))"
        Expect ([IO.File]::ReadAllText($md) -match [regex]::Escape($marker)) 'the Markdown does not show the note'
        "$($r.transcripts) session and $($r.subagentTranscripts) subagent transcripts, $($r.toolCalls) calls, $($r.failedCalls) failed; the note reported unmatched; $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
    }
}

Add-Check 'failures.field-report-score' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'cliext-field-report.ps1 classifies as score.py does: a SKILL.md printed from the shell counts as loaded, and logged errors a console read prints are not failed calls' {
    # A made-up transcript in its own transcript root, so the check doesn't depend on this machine's sessions.
    $root = Join-Path $work 'fr-transcripts'
    $slug = $projectPath -replace '[^A-Za-z0-9]', '-'
    $dir = Join-Path $root "$slug\clitest-session\subagents"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $ts = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    function Use($id, $name, $inp) { @{ type = 'assistant'; timestamp = $ts; sessionId = 'clitest-session'; agentId = 'clitest'; message = @{ role = 'assistant'; content = @(@{ type = 'tool_use'; id = $id; name = $name; input = $inp }) } } | ConvertTo-Json -Depth 8 -Compress }
    function Res($id, $text) { @{ type = 'user'; timestamp = $ts; sessionId = 'clitest-session'; agentId = 'clitest'; message = @{ role = 'user'; content = @(@{ type = 'tool_result'; tool_use_id = $id; content = $text; is_error = $false }) } } | ConvertTo-Json -Depth 8 -Compress }
    $lines = @(
        (Use 't1' 'Bash' @{ command = 'cat /e/Game/.claude/skills/unity-cliext/SKILL.md' }), (Res 't1' '# Unity CLI Extensions'),
        (Use 't2' 'PowerShell' @{ command = 'Get-Content E:\Game\.claude\skills\unity-cliext-uitk\SKILL.md | Select-Object -First 40' }), (Res 't2' '# uitk'),
        (Use 't3' 'PowerShell' @{ command = 'unity command console get --tail 5 --format json' }), (Res 't3' "error: NullReferenceException in Player.Update`n{`"logType`":`"Error`",`"message`":`"x`"}"),
        (Use 't4' 'PowerShell' @{ command = 'unity command scene list --format json' }), (Res 't4' "error: No command named 'scene list'")
    )
    [IO.File]::WriteAllText((Join-Path $dir 'agent-clitest.jsonl'), ($lines -join "`n") + "`n")
    $out = Join-Path $work 'fr-score'
    $ErrorActionPreference = 'Continue'
    $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools 'cliext-field-report.ps1') -Project $projectPath -TranscriptRoot $root -Days 1 -Out $out -Quiet 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Expect ($exit -eq 0) "exit $exit`: $(Short $log)"
    $r = [IO.File]::ReadAllText((Join-Path $out 'field-report.json')) | ConvertFrom-Json
    $s = @($r.sessions) | Where-Object { $_.agentId -eq 'clitest' } | Select-Object -First 1
    Expect ($s -ne $null) "no session for the made-up transcript: $(Short ($r.sessions | ConvertTo-Json -Compress))"
    $skills = @($s.skillsLoaded) -join ', '
    Expect ($skills -eq 'unity-cliext (shell), unity-cliext-uitk (shell)') "skills loaded: '$skills', expected both (shell)"
    Expect ($s.toolCalls -eq 4 -and $s.failedCalls -eq 1 -and $s.embeddedFailures -eq 1) "calls $($s.toolCalls), failed $($s.failedCalls), embedded $($s.embeddedFailures); expected 4, 1 (the guessed scene list), 1"
    "skills: $skills; 4 calls, 1 failed (the console read's logged errors not counted)"
}

Add-Check 'failures.report-zip''cliext.dialogs, cliext.watchdog, cliext.failures' 'cliext-report.ps1 -NoEditor builds a zip with the field report, state files, versions, skills, the CLAUDE.md block and README.txt (zip and folder deleted after)' {
    $out = Join-Path $work 'report'
    $unz = Join-Path $work 'report-unzipped'
    try {
        # Under -ErrorAction Stop, Windows PowerShell turns the script's stderr into errors; keep it as text.
        $ErrorActionPreference = 'Continue'
        $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools 'cliext-report.ps1') -Project $projectPath -Days 1 -NoEditor -NoExplorer -Out $out 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        Expect ($exit -eq 0) "exit $exit`: $(Short $log)"
        $zip = Get-ChildItem $out -Filter 'cliext-report-*.zip' | Select-Object -First 1
        Expect ($zip -and $zip.Name -match '^cliext-report-\d{8}-\d{6}\.zip$') "no cliext-report-<time>.zip in $out"
        Expect ("$log" -match [regex]::Escape($zip.FullName)) "the script didn't print the zip's path: $(Short $log)"
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $unz)
        $want = @('README.txt', 'versions.json', 'editor\editor-queries.json', 'field-report\field-report.md', 'field-report\field-report.json',
                  'project\manifest.json', 'project\packages-lock.json', 'project\skills.json', 'project\claude-md-block.md')
        foreach ($f in @('failures.jsonl', 'dialogs.jsonl', 'watchdog-status.json')) { if (Test-Path (Join-Path $projectPath "Library\CLIExtensions\$f")) { $want += "state\$f" } }
        $missing = @($want | Where-Object { -not (Test-Path (Join-Path $unz $_)) })
        Expect ($missing.Count -eq 0) "missing from the zip: $($missing -join ', ')"
        $q = [IO.File]::ReadAllText((Join-Path $unz 'editor\editor-queries.json')) | ConvertFrom-Json
        Expect (-not $q.queried) 'editor-queries.json says the Editor was queried under -NoEditor'
        $readme = [IO.File]::ReadAllText((Join-Path $unz 'README.txt'))
        Expect ($readme -match 'github\.com/qhenshaw/UnityCLIExtensions/issues' -and $readme -match 'field-report\\field-report\.md') 'README.txt lacks the issues link or the file list'
        $skills = @([IO.File]::ReadAllText((Join-Path $unz 'project\skills.json')) | ConvertFrom-Json)
        Expect (@($skills | Where-Object { $_.name -eq 'unity-cliext' -and $_.stamp }).Count -gt 0) "skills.json has no stamped unity-cliext: $(Short ($skills | ConvertTo-Json -Compress))"
        "$([math]::Round($zip.Length / 1MB, 2)) MB, $(@(Get-ChildItem $unz -Recurse -File).Count) files, all expected ones there; skills: " + (($skills | ForEach-Object { "$($_.name)=$($_.status)" }) -join ', ')
    }
    finally { Remove-Item $out, $unz -Recurse -Force -ErrorAction SilentlyContinue }
}

Add-Check 'failures.report-command' 'cliext.dialogs, cliext.watchdog, cliext.failures' 'cliext.failures report starts the report script out of process and the zip appears at the path it returns (deleted after)' {
    $r = Invoke-Cli @('cliext.failures', 'report', '--days', '1', '--open-folder', 'false', '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.started -and "$($r.json.zip)" -like '*CLIExtensionsReports*cliext-report-*.zip') "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $bad = Invoke-Cli @('cliext.failures', 'report', '--days', '0', '--format', 'json')
    Expect ($bad.exit -eq 2) "--days 0: exit $($bad.exit), expected 2"
    $zip = "$($r.json.zip)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        # The script runs on its own; wait for its zip (renamed into place only when complete).
        while (-not (Test-Path -LiteralPath $zip) -and $sw.Elapsed.TotalSeconds -lt 180) { Start-Sleep -Milliseconds 500 }
        Expect (Test-Path -LiteralPath $zip) "no zip at $zip after 180 s"
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($zip)
        try { $names = @($z.Entries | ForEach-Object { $_.FullName }) } finally { $z.Dispose() }
        $q = $names -contains 'editor/editor-queries.json' -or $names -contains 'editor\editor-queries.json'
        Expect ($q -and ($names -match 'README\.txt').Count -gt 0) "zip entries: $($names -join ', ')"
        "zip after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s with $($names.Count) files; --days 0 exit 2"
    }
    finally {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        $dir = Split-Path -Parent $zip
        if ((Test-Path $dir) -and -not (Get-ChildItem $dir -Force)) { Remove-Item $dir -Force }
    }
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

# --- cliext.shadergraph -----------------------------------------------------------------------------------------------

# One scratch graph made by sg.create and built by sg.edit-batch; later checks edit it by the ids those returned.
$script:sgPath = "$scratchRel/CliTestSg.shadergraph"
$script:sgIds = @{}
$sgTexture = 'Assets/ThirdParty/VFXTexturePack/TX_Noise_01.png'

function Get-SgHash { (Get-FileHash (Join-Path $projectPath ($script:sgPath -replace '/', '\'))).Hash }
function Get-SgGraph { $r = Invoke-Cli @('cliext.shadergraph', 'inspect', '--path', $script:sgPath, '--format', 'json'); Expect ($r.exit -eq 0) "inspect: exit $($r.exit): $(Short $r.stderr)"; $r.json }
function Get-SgNode([string]$id) { $r = Invoke-Cli @('cliext.shadergraph', 'inspect', '--path', $script:sgPath, '--id', $id, '--format', 'json'); if ($r.exit -eq 0) { $r.json } else { $null } }
function Get-SgSlot($node, [string]$slot) { @($node.inputs) | Where-Object { $_.name -eq $slot } | Select-Object -First 1 }
function Get-SgSetting($node, [string]$name) { "$((@($node.settings) | Where-Object { $_.name -eq $name } | Select-Object -First 1).value)" }
function Need-SgIds { Expect ($script:sgIds.ContainsKey('mul')) 'no scratch graph: sg.create or sg.edit-batch did not run or failed' }
function Get-SgShader {
    # The compiled shader's property names, and a new material's defaults: what a material using the graph gets.
    $code = @"
var s = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>("$($script:sgPath)");
if (s == null) return "no shader";
var names = new System.Collections.Generic.List<string>();
for (int i = 0; i < s.GetPropertyCount(); i++) names.Add(s.GetPropertyName(i));
var m = new UnityEngine.Material(s);
var tex = m.HasTexture("_Noise") ? m.GetTexture("_Noise") : null;
string r = string.Join(",", names) + "|" + (tex != null ? UnityEditor.AssetDatabase.GetAssetPath(tex) : "") + "|"
    + (m.HasColor("_Tint") ? UnityEngine.ColorUtility.ToHtmlStringRGB(m.GetColor("_Tint")) : "") + "|"
    + (m.HasFloat("_Speed") ? m.GetFloat("_Speed").ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
UnityEngine.Object.DestroyImmediate(m);
return r;
"@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'sg-shader.cs' $code), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $parts = $r.stdout.Trim().Trim('"') -split '\|'
    Expect ($parts.Count -eq 4) "script eval: $(Short $r.stdout)"
    [pscustomobject]@{ properties = @($parts[0] -split ','); noise = $parts[1]; tint = $parts[2]; speed = $parts[3] }
}
function Expect-SgFailure($r, [string]$hint, [string]$before, [string]$what) {
    Expect ($r.exit -ne 0) "${what}: exit 0"
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    Expect ("$($r.stderr)" -notmatch '   at ') "${what}: a stack trace instead of a message: $(Short $r.stderr)"
    if ($before) { Expect ((Get-SgHash) -eq $before) "${what}: the .shadergraph on disk changed" }
}

Add-Check 'sg.types' 'cliext.shadergraph' 'cliext.shadergraph types finds a node key by filter and lists the targets with their materials' {
    $r = Invoke-Cli @('cliext.shadergraph', 'types', '--filter', 'Multiply', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short $r.stderr)"
    $keys = @(@($r.json) | ForEach-Object { $_.key })
    Expect ($keys -contains 'Math/Basic/Multiply') "no Math/Basic/Multiply in: $($keys -join ', ')"
    Expect (@($keys | Where-Object { $_ -notmatch 'Multiply' }).Count -eq 0) "keys that don't match the filter: $($keys -join ', ')"
    $t = Invoke-Cli @('cliext.shadergraph', 'types', '--targets', 'true', '--format', 'json')
    Expect ($t.exit -eq 0) "--targets: exit $($t.exit): $(Short $t.stderr)"
    $urp = @($t.json) | Where-Object { $_.target -eq 'Universal' }
    Expect ($null -ne $urp -and @($urp.materials) -contains 'Unlit' -and @($urp.materials) -contains 'Lit') "no Universal with Lit and Unlit: $(Short $t.stdout)"
    "$($keys.Count) match(es); $(@($t.json).Count) targets, Universal has $(@($urp.materials).Count) materials"
}

Add-Check 'sg.create' 'cliext.shadergraph' 'cliext.shadergraph create makes a URP Unlit graph and copies one (--from); an existing path, a path outside Assets, an unknown target or material, or a missing --from fails with the fix' {
    $r = Invoke-Cli @('cliext.shadergraph', 'create', '--path', $script:sgPath, '--target', 'Universal', '--material', 'Unlit', '--format', 'json') 180
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ($r.json.compile.hasErrors -eq $false) "the new graph has compile errors: $(Short ($r.json.compile | ConvertTo-Json -Compress))"
    $g = Get-SgGraph
    Expect (@($g.targets).Count -eq 1 -and $g.targets[0].target -eq 'Universal' -and $g.targets[0].material -eq 'Unlit') "targets: $(Short ($g.targets | ConvertTo-Json -Compress))"
    $base = @($g.fragment) | Where-Object { $_.name -eq 'Base Color' } | Select-Object -First 1
    Expect ($null -ne $base) "no Base Color block: $(@($g.fragment | ForEach-Object { $_.name }) -join ', ')"
    $script:sgIds['base'] = "$($base.id)"
    $copy = "$scratchRel/CliTestSgCopy.shadergraph"
    $c = Invoke-Cli @('cliext.shadergraph', 'create', '--path', $copy, '--from', $script:sgPath, '--format', 'json') 180
    Expect ($c.exit -eq 0) "--from: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
    $ci = Invoke-Cli @('cliext.shadergraph', 'inspect', '--path', $copy, '--details', 'false', '--format', 'json')
    Expect ($ci.exit -eq 0 -and $ci.json.targets[0].material -eq 'Unlit') "the copy: exit $($ci.exit): $(Short ($ci.stdout + $ci.stderr))"

    $before = Get-SgHash
    $again = Invoke-Cli @('cliext.shadergraph', 'create', '--path', $script:sgPath, '--target', 'Universal', '--format', 'json')
    Expect-SgFailure $again 'already exists.*cliext\.shadergraph edit' $before 'existing path'
    Expect ($again.exit -eq 6) "existing path: exit $($again.exit), expected 6"
    $cases = @(
        @(@('--path', 'NotAssets/CliTest.shadergraph', '--target', 'Universal'), 2, "isn't under Assets/", 'path outside Assets'),
        @(@('--path', 'CliTest.shadergraph', '--target', 'Universal'), 2, "isn't under Assets/", 'bare file name'),
        @(@('--path', "$scratchRel/BadTarget.shadergraph", '--target', 'Glowy'), 2, 'Targets:.*Universal', 'unknown target'),
        @(@('--path', "$scratchRel/BadMaterial.shadergraph", '--target', 'Universal', '--material', 'Glowy'), 2, 'It has:.*Unlit', 'unknown material'),
        @(@('--path', "$scratchRel/BadFrom.shadergraph", '--from', "$scratchRel/NoSuch.shadergraph"), 13, 'No Shader Graph to copy', 'missing --from')
    )
    $seen = @()
    foreach ($k in $cases) {
        $f = Invoke-Cli (@('cliext.shadergraph', 'create') + $k[0] + @('--format', 'json'))
        Expect-SgFailure $f $k[2] '' $k[3]
        Expect ($f.exit -eq $k[1]) "$($k[3]): exit $($f.exit), expected $($k[1])"
        $seen += "$($k[3]) $($f.exit)"
    }
    foreach ($left in 'NotAssets', 'CliTest.shadergraph', "$($scratchRel -replace '/', '\')\BadTarget.shadergraph", "$($scratchRel -replace '/', '\')\BadMaterial.shadergraph", "$($scratchRel -replace '/', '\')\BadFrom.shadergraph") {
        Expect (-not (Test-Path (Join-Path $projectPath $left))) "a failed create left $left"
    }
    "Universal/Unlit with Base Color; copy made; existing exit 6; $($seen -join ', ')"
}

Add-Check 'sg.edit-batch' 'cliext.shadergraph' 'cliext.shadergraph edit adds two properties (a pack texture), two nodes and four links in one call; the compiled shader and a material using it show them' {
    Expect ($script:sgIds.ContainsKey('base')) 'no scratch graph: sg.create did not run or failed'
    Expect (Test-Path (Join-Path $projectPath ($sgTexture -replace '/', '\'))) "no texture at $sgTexture"
    $ops = @(
        @{ op = 'add_property'; type = 'color'; name = 'Tint'; reference = '_Tint'; value = '#FF8000'; as = 'tint' },
        @{ op = 'add_property'; type = 'texture2d'; name = 'Noise'; reference = '_Noise'; value = $sgTexture; as = 'noise' },
        @{ op = 'add_node'; type = 'Input/Texture/Sample Texture 2D'; as = 'tex' },
        @{ op = 'add_node'; type = 'Math/Basic/Multiply'; as = 'mul' },
        @{ op = 'link'; from = '$noise'; to = '$tex:Texture' },
        @{ op = 'link'; from = '$tex:RGBA'; to = '$mul:A' },
        @{ op = 'link'; from = '$tint'; to = '$mul:B' },
        @{ op = 'link'; from = '$mul:Out'; to = "$($script:sgIds.base):Base Color" }
    )
    $f = Write-WorkFile 'sg-batch.json' ($ops | ConvertTo-Json -Compress)
    $r = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', $f, '--format', 'json') 180
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ($r.json.applied -eq 8 -and $r.json.total -eq 8) "applied $($r.json.applied) of $($r.json.total)"
    Expect ($r.json.compile.hasErrors -eq $false) "compile errors: $(Short ($r.json.compile | ConvertTo-Json -Compress))"
    foreach ($a in @($r.json.results | Where-Object { $_.alias })) { $script:sgIds[$a.alias] = "$($a.id)" }
    foreach ($k in 'tint', 'noise', 'tex', 'mul') { Expect ($script:sgIds.ContainsKey($k)) "no id for `$$k in the results" }
    $base = Get-SgSlot (Get-SgNode $script:sgIds.base) 'Base Color'
    Expect (@($base.links) -contains "$($script:sgIds.mul):2") "Base Color isn't linked from Multiply: $(Short ($base | ConvertTo-Json -Compress))"
    $s = Get-SgShader
    Expect ($s.properties -contains '_Tint' -and $s.properties -contains '_Noise') "shader properties: $($s.properties -join ', ')"
    Expect ($s.noise -eq $sgTexture) "a material's _Noise is '$($s.noise)', expected $sgTexture"
    Expect ($s.tint -eq 'FF8000') "a material's _Tint is '$($s.tint)'"
    "8 ops in one call; shader has _Tint and _Noise; a material gets the noise texture and FF8000"
}

Add-Check 'sg.add' 'cliext.shadergraph' 'cliext.shadergraph add puts a node at a position; an unknown node key fails, suggests a match or the types command, and saves nothing' {
    Need-SgIds
    $r = Invoke-Cli @('cliext.shadergraph', 'add', '--path', $script:sgPath, '--type', 'Math/Basic/Add', '--x', '-700', '--y', '320', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $id = "$(@($r.json.results)[0].id)"
    Expect ($id -match '^[0-9a-f]{32}$') "no id for the new node: $(Short $r.stdout)"
    $script:sgIds['add'] = $id
    $n = Get-SgNode $id
    Expect ($null -ne $n -and $n.type -eq 'AddNode') "inspect --id: $(Short ($n | ConvertTo-Json -Compress))"
    Expect ($n.position[0] -eq -700 -and $n.position[1] -eq 320) "position: $($n.position -join ',')"
    $before = Get-SgHash
    $none = Invoke-Cli @('cliext.shadergraph', 'add', '--path', $script:sgPath, '--type', 'Zzqx', '--format', 'json')
    Expect-SgFailure $none 'cliext\.shadergraph types' $before 'unknown key, no close match'
    $close = Invoke-Cli @('cliext.shadergraph', 'add', '--path', $script:sgPath, '--type', 'Math/Basic/Mult', '--format', 'json')
    Expect-SgFailure $close 'Did you mean: Math/Basic/Multiply' $before 'unknown key with a close match'
    $x = Invoke-Cli @('cliext.shadergraph', 'add', '--path', $script:sgPath, '--type', 'Multiply', '--x', 'abc', '--format', 'json')
    Expect-SgFailure $x 'abc' $before 'non-numeric --x'
    Expect ($x.exit -eq 2) "non-numeric --x: exit $($x.exit), expected 2"
    "added $id at -700,320; no match exit $($none.exit), close match exit $($close.exit), bad --x exit 2"
}

Add-Check 'sg.add_property' 'cliext.shadergraph' 'cliext.shadergraph add_property adds an exposed float with a default a material reads; an unknown type is invalid usage and a bad default is one line' {
    Need-SgIds
    $r = Invoke-Cli @('cliext.shadergraph', 'add_property', '--path', $script:sgPath, '--type', 'float', '--name', 'Speed', '--reference', '_Speed', '--value', '2.5', '--format', 'json')
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $script:sgIds['speed'] = "$(@($r.json.results)[0].id)"
    $p = @((Get-SgGraph).properties) | Where-Object { $_.reference -eq '_Speed' }
    Expect ($null -ne $p -and $p.exposed -eq $true -and $p.value -eq '2.5') "inspect: $(Short ($p | ConvertTo-Json -Compress))"
    $s = Get-SgShader
    Expect ($s.properties -contains '_Speed' -and $s.speed -eq '2.5') "shader: _Speed default '$($s.speed)' in $($s.properties -join ', ')"
    $before = Get-SgHash
    $t = Invoke-Cli @('cliext.shadergraph', 'add_property', '--path', $script:sgPath, '--type', 'colour', '--format', 'json')
    Expect-SgFailure $t 'Unknown property type.*color, texture2d' $before 'unknown --type'
    Expect ($t.exit -eq 2) "unknown --type: exit $($t.exit), expected 2"
    $v = Invoke-Cli @('cliext.shadergraph', 'add_property', '--path', $script:sgPath, '--type', 'float', '--name', 'Bad', '--value', 'abc', '--format', 'json')
    Expect-SgFailure $v 'a number' $before 'non-numeric --value'
    $b = Invoke-Cli @('cliext.shadergraph', 'add_property', '--path', $script:sgPath, '--type', 'boolean', '--name', 'Bad', '--value', 'maybe', '--format', 'json')
    Expect-SgFailure $b 'true or false' $before 'non-boolean --value'
    "Speed 2.5 exposed; unknown type exit 2, bad float exit $($v.exit), bad bool exit $($b.exit)"
}

Add-Check 'sg.set' 'cliext.shadergraph' 'cliext.shadergraph set changes a slot, a property default and a node setting; bad values, unknown slots, settings, properties or ids fail, name the fix and save nothing' {
    Need-SgIds
    $r = Invoke-Cli @('cliext.shadergraph', 'set', '--path', $script:sgPath, '--id', $script:sgIds.add, '--slot', 'B', '--value', '0.5', '--format', 'json')
    Expect ($r.exit -eq 0) "slot: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $p = Invoke-Cli @('cliext.shadergraph', 'set', '--path', $script:sgPath, '--id', 'Tint', '--value', '#00FF00', '--format', 'json')
    Expect ($p.exit -eq 0) "property: exit $($p.exit): $(Short ($p.stdout + $p.stderr))"
    $st = Invoke-Cli @('cliext.shadergraph', 'set', '--path', $script:sgPath, '--id', $script:sgIds.tex, '--setting', 'mipSamplingMode', '--value', 'LOD', '--format', 'json')
    Expect ($st.exit -eq 0) "setting: exit $($st.exit): $(Short ($st.stdout + $st.stderr))"
    Expect ("$((Get-SgSlot (Get-SgNode $script:sgIds.add) 'B').value)" -match '^\[?0\.5') "Add B is '$((Get-SgSlot (Get-SgNode $script:sgIds.add) 'B').value)'"
    Expect ((Get-SgShader).tint -eq '00FF00') 'a material does not get the new Tint'
    Expect ((Get-SgSetting (Get-SgNode $script:sgIds.tex) 'mipSamplingMode') -eq 'LOD') "mipSamplingMode is '$(Get-SgSetting (Get-SgNode $script:sgIds.tex) 'mipSamplingMode')'"
    $before = Get-SgHash
    $cases = @(
        @(@('--id', $script:sgIds.add, '--slot', 'B', '--value', 'abc'), 'a ', 'non-numeric slot value'),
        @(@('--id', 'Tint', '--value', 'notacolor'), 'colour', 'bad colour'),
        @(@('--id', $script:sgIds.tex, '--setting', 'textureType', '--value', 'Glowy'), 'Use one of:.*Normal', 'unknown enum name'),
        @(@('--id', $script:sgIds.add, '--slot', 'Bee', '--value', '1'), 'It has: 0 A, 1 B', 'unknown slot'),
        @(@('--id', $script:sgIds.mul, '--slot', 'B', '--value', '1'), 'unlink it', 'linked slot'),
        @(@('--id', $script:sgIds.tex, '--setting', 'nope', '--value', '1'), 'It has:.*textureType', 'unknown setting'),
        @(@('--id', 'NoSuchProperty', '--value', '1'), 'Properties:.*Tint', 'unknown property'),
        @(@('--id', 'Noise', '--value', 'Assets/NoSuchTexture.png'), 'No Texture at', 'missing texture'),
        @(@('--id', 'ffffffffffffffffffffffffffffffff', '--slot', 'B', '--value', '1'), 'cliext\.shadergraph inspect', 'unknown node id')
    )
    $seen = @()
    foreach ($c in $cases) {
        $f = Invoke-Cli (@('cliext.shadergraph', 'set', '--path', $script:sgPath) + $c[0] + @('--format', 'json'))
        Expect-SgFailure $f $c[1] $before $c[2]
        $seen += "$($c[2]) $($f.exit)"
    }
    $nv = Invoke-Cli @('cliext.shadergraph', 'set', '--path', $script:sgPath, '--id', $script:sgIds.add, '--slot', 'B', '--format', 'json')
    Expect-SgFailure $nv 'value' $before 'no --value'
    Expect ($nv.exit -eq 2) "no --value: exit $($nv.exit), expected 2"
    "B 0.5, Tint 00FF00, mipSamplingMode LOD; failures: $($seen -join ', '), no value 2"
}

Add-Check 'sg.link-unlink' 'cliext.shadergraph' 'cliext.shadergraph link (a property and a slot) and unlink; unlinking what is not linked, a type mismatch, or an unknown slot or property fails and saves nothing' {
    Need-SgIds
    $l = Invoke-Cli @('cliext.shadergraph', 'link', '--path', $script:sgPath, '--from', 'Speed', '--to', "$($script:sgIds.add):A", '--format', 'json')
    Expect ($l.exit -eq 0) "link a property: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
    $pn = "$(@($l.json.results)[0].fromNode)"
    Expect (@((Get-SgSlot (Get-SgNode $script:sgIds.add) 'A').links) -contains "${pn}:0") "Add A isn't linked from the Speed node $pn"
    Expect ((Get-SgNode $pn).property -eq 'Speed') "node $pn isn't a Speed property node"
    $u = Invoke-Cli @('cliext.shadergraph', 'unlink', '--path', $script:sgPath, '--to', "$($script:sgIds.add):A", '--format', 'json')
    Expect ($u.exit -eq 0 -and @($u.json.results)[0].removed -eq 1) "unlink: exit $($u.exit): $(Short ($u.stdout + $u.stderr))"
    Expect ($null -eq (Get-SgSlot (Get-SgNode $script:sgIds.add) 'A').links) 'Add A is still linked after unlink'
    $before = Get-SgHash
    $again = Invoke-Cli @('cliext.shadergraph', 'unlink', '--path', $script:sgPath, '--to', "$($script:sgIds.add):A", '--format', 'json')
    Expect-SgFailure $again 'No link into.*inspect' $before 'unlink of a slot with no link'
    $from = Invoke-Cli @('cliext.shadergraph', 'unlink', '--path', $script:sgPath, '--to', "$($script:sgIds.mul):A", '--from', "$($script:sgIds.add):Out", '--format', 'json')
    Expect-SgFailure $from 'No link into.*from' $before 'unlink --from a node that is not linked there'
    $type = Invoke-Cli @('cliext.shadergraph', 'link', '--path', $script:sgPath, '--from', "$($script:sgIds.add):Out", '--to', "$($script:sgIds.tex):Texture", '--format', 'json')
    Expect-SgFailure $type "Can't link|refused" $before 'a vector into a texture slot'
    $slot = Invoke-Cli @('cliext.shadergraph', 'link', '--path', $script:sgPath, '--from', "$($script:sgIds.add):Nope", '--to', "$($script:sgIds.mul):A", '--format', 'json')
    Expect-SgFailure $slot 'has no output slot.*It has: 2 Out' $before 'unknown output slot'
    $prop = Invoke-Cli @('cliext.shadergraph', 'link', '--path', $script:sgPath, '--from', 'NoSuchProperty', '--to', "$($script:sgIds.add):A", '--format', 'json')
    Expect-SgFailure $prop 'No property.*Properties:' $before 'unknown property'
    $form = Invoke-Cli @('cliext.shadergraph', 'link', '--path', $script:sgPath, '--from', "$($script:sgIds.add):Out", '--to', $script:sgIds.mul, '--format', 'json')
    Expect-SgFailure $form '<node id>:<slot>' $before 'a --to without a slot'
    "linked Speed into Add A and unlinked; failures: $($again.exit), $($from.exit), $($type.exit), $($slot.exit), $($prop.exit), $($form.exit)"
}

Add-Check 'sg.hlsl' 'cliext.shadergraph' 'a Custom Function with bad HLSL saves but exits 6 with the compile error in the reply; fixing the body compiles clean' {
    Need-SgIds
    $ops = @(
        @{ op = 'add_node'; type = 'Utility/Custom Function'; as = 'cf' },
        @{ op = 'add_port'; id = '$cf'; name = 'In'; type = 'vector3' },
        @{ op = 'add_port'; id = '$cf'; name = 'Out'; type = 'vector3'; value = 'out' },
        @{ op = 'set'; id = '$cf'; setting = 'sourceType'; value = 'String' },
        @{ op = 'set'; id = '$cf'; setting = 'functionName'; value = 'CliTestGlow' },
        @{ op = 'set'; id = '$cf'; setting = 'functionBody'; value = 'Out = In * 2 +;' },
        @{ op = 'link'; from = "$($script:sgIds.mul):Out"; to = '$cf:In' },
        @{ op = 'link'; from = '$cf:Out'; to = "$($script:sgIds.base):Base Color" }
    )
    $r = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-cf.json' ($ops | ConvertTo-Json -Compress)), '--format', 'json') 180
    Expect ($r.exit -eq 6) "bad HLSL: exit $($r.exit), expected 6: $(Short ($r.stdout + $r.stderr))"
    Expect ($r.json.applied -eq 8 -and @($r.json.compile.shaderErrors).Count -gt 0) "no shader error in the reply: $(Short ($r.json.compile | ConvertTo-Json -Compress))"
    Expect ("$($r.stderr)" -match 'compile error') "stderr doesn't say why it exited 6: $(Short $r.stderr)"
    $cf = "$((@($r.json.results) | Where-Object { $_.alias -eq 'cf' }).id)"
    $script:sgIds['cf'] = $cf
    $err = Short "$(@($r.json.compile.shaderErrors)[0])" 80
    $fix = Invoke-Cli @('cliext.shadergraph', 'set', '--path', $script:sgPath, '--id', $cf, '--setting', 'functionBody', '--value', 'Out = In * 2;', '--format', 'json') 180
    Expect ($fix.exit -eq 0) "good HLSL: exit $($fix.exit): $(Short ($fix.stdout + $fix.stderr))"
    Expect (@($fix.json.compile.shaderErrors).Count -eq 0) "still errors: $(Short ($fix.json.compile | ConvertTo-Json -Compress))"
    $i = Get-SgGraph
    Expect ($i.compile.hasErrors -eq $false) "inspect still reports errors: $(Short ($i.compile | ConvertTo-Json -Compress))"
    Expect ((Get-SgSetting (Get-SgNode $cf) 'functionBody') -eq 'Out = In * 2;') 'inspect shows another body'
    "bad body exit 6 ($err); fixed body exit 0, no errors"
}

Add-Check 'sg.remove' 'cliext.shadergraph' 'cliext.shadergraph remove takes out a node and a property (by name); an unknown id fails, points at inspect and saves nothing' {
    Need-SgIds
    $r = Invoke-Cli @('cliext.shadergraph', 'remove', '--path', $script:sgPath, '--id', $script:sgIds.add, '--format', 'json')
    Expect ($r.exit -eq 0) "node: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect ($null -eq (Get-SgNode $script:sgIds.add)) 'the Add node is still there'
    $p = Invoke-Cli @('cliext.shadergraph', 'remove', '--path', $script:sgPath, '--id', 'Speed', '--format', 'json')
    Expect ($p.exit -eq 0) "property: exit $($p.exit): $(Short ($p.stdout + $p.stderr))"
    $g = Get-SgGraph
    Expect (@($g.properties | Where-Object { $_.name -eq 'Speed' }).Count -eq 0) 'the Speed property is still there'
    Expect (@($g.nodes | Where-Object { $_.property -eq 'Speed' }).Count -eq 0) 'a Speed property node is left over'
    Expect ((Get-SgShader).properties -notcontains '_Speed') 'the compiled shader still has _Speed'
    $before = Get-SgHash
    $bad = Invoke-Cli @('cliext.shadergraph', 'remove', '--path', $script:sgPath, '--id', $script:sgIds.add, '--format', 'json')
    Expect-SgFailure $bad 'No node or property.*cliext\.shadergraph inspect' $before 'unknown id'
    "removed Add and Speed; again: exit $($bad.exit)"
}

Add-Check 'sg.target-mode' 'cliext.shadergraph' 'cliext.shadergraph edit set_target turns alpha clipping on (adding Alpha Clip Threshold, which add_block refused before) and set_mode makes a float a 0-1 slider and a colour HDR; inspect shows both; an unknown mode or setting fails with the valid values and saves nothing' {
    Need-SgIds
    $before = Get-SgHash
    $refused = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-clip-early.json' '[{"op":"add_block","block":"Alpha Clip Threshold"}]'), '--format', 'json')
    Expect-SgFailure $refused 'set_target.*alphaClip' $before 'add_block before alpha clipping'
    $badMode = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-bad-mode.json' '[{"op":"set_mode","id":"Tint","mode":"slider"}]'), '--format', 'json')
    Expect-SgFailure $badMode 'Use one of: default, hdr' $before 'unknown colour mode'
    $badSetting = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-bad-setting.json' '[{"op":"set_target","setting":"alphaClipping","value":"true"}]'), '--format', 'json')
    Expect-SgFailure $badSetting 'Did you mean alphaClip' $before 'unknown target setting'
    $ops = '[{"op":"set_target","setting":"alphaClip","value":"true"},' +
           '{"op":"add_property","type":"float","name":"Cutoff","reference":"_Cutoff","value":"0.3","as":"cut"},' +
           '{"op":"set_mode","id":"$cut","mode":"slider","min":0,"max":1},' +
           '{"op":"set_mode","id":"Tint","mode":"hdr"}]'
    $r = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-target-mode.json' $ops), '--format', 'json') 180
    Expect ($r.exit -eq 0) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    Expect (@($r.json.results[0].blocksAdded) -contains 'Alpha Clip Threshold') "set_target didn't add Alpha Clip Threshold: $(Short ($r.json.results[0] | ConvertTo-Json -Compress))"
    $g = Get-SgGraph
    Expect ("$($g.targets[0].settings.alphaClip)" -match '^(?i)true$') "inspect targets settings alphaClip: $(Short ($g.targets[0].settings | ConvertTo-Json -Compress))"
    Expect (@($g.fragment | Where-Object { $_.name -eq 'Alpha Clip Threshold' }).Count -eq 1) "no Alpha Clip Threshold block: $(@($g.fragment | ForEach-Object { $_.name }) -join ', ')"
    $cut = @($g.properties | Where-Object { $_.name -eq 'Cutoff' })[0]
    $tint = @($g.properties | Where-Object { $_.name -eq 'Tint' })[0]
    Expect ($cut.mode -eq 'Slider(0, 1)') "Cutoff mode '$($cut.mode)'"
    Expect ($tint.mode -eq 'HDR') "Tint mode '$($tint.mode)'"
    $range = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'sg-range.cs' "var s = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(`"$($script:sgPath)`"); int i = s.FindPropertyIndex(`"_Cutoff`"); return s.GetPropertyType(i) + `" `" + s.GetPropertyRangeLimits(i).y;"), '--format', 'json')
    Expect ("$($range.stdout)" -match 'Range 1') "the compiled shader's _Cutoff: $(Short $range.stdout)"
    'add_block refused until set_target alphaClip; Alpha Clip Threshold added; Cutoff Slider(0, 1) compiles to Range; Tint HDR; bad mode and setting save nothing'
}

Add-Check 'sg.batch-rollback' 'cliext.shadergraph' 'an edit batch with one bad op saves nothing, on disk or in memory, and the next good edit saves only its own change' {
    Need-SgIds
    $before = Get-SgHash
    $g0 = Get-SgGraph
    $ops = @(
        @{ op = 'add_node'; type = 'Math/Basic/Subtract'; as = 'extra' },
        @{ op = 'add_property'; type = 'float'; name = 'Extra' },
        @{ op = 'set'; id = 'Tint'; value = '#0000FF' },
        @{ op = 'remove'; id = $script:sgIds.tex },
        @{ op = 'unlink'; to = "$($script:sgIds.base):Base Color" },
        @{ op = 'add_node'; type = 'Math/Basic/NoSuchNode' }
    )
    $r = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-bad-batch.json' ($ops | ConvertTo-Json -Compress)), '--format', 'json')
    Expect-SgFailure $r 'Op 5 \(add_node\) failed.*Nothing was saved' $before 'bad batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 5 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
    $g1 = Get-SgGraph
    Expect (@($g1.nodes).Count -eq @($g0.nodes).Count -and @($g1.properties).Count -eq @($g0.properties).Count) "in memory: $(@($g0.nodes).Count) nodes, $(@($g0.properties).Count) properties -> $(@($g1.nodes).Count), $(@($g1.properties).Count)"
    $good = Write-WorkFile 'sg-good.json' ('[' + (@{ op = 'move'; id = $script:sgIds.mul; x = -333; y = 44 } | ConvertTo-Json -Compress) + ']')
    $ok = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', $good, '--format', 'json')
    Expect ($ok.exit -eq 0) "good edit: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
    $g2 = Get-SgGraph
    Expect (@($g2.nodes).Count -eq @($g0.nodes).Count) "the failed batch's nodes were saved: $(@($g0.nodes).Count) -> $(@($g2.nodes).Count) nodes"
    Expect (@($g2.properties | Where-Object { $_.name -eq 'Extra' }).Count -eq 0) "the failed batch's property was saved"
    Expect ($null -ne (Get-SgNode $script:sgIds.tex)) "the failed batch's remove was saved: node $($script:sgIds.tex) is gone"
    Expect (@((Get-SgSlot (@($g2.fragment) | Where-Object { $_.id -eq $script:sgIds.base }) 'Base Color').links).Count -eq 1) "the failed batch's unlink was saved"
    Expect ((Get-SgShader).tint -eq '00FF00') "the failed batch's Tint was saved: $((Get-SgShader).tint)"
    $mul = Get-SgNode $script:sgIds.mul
    Expect ($mul.position[0] -eq -333 -and $mul.position[1] -eq 44) "the good edit's move isn't there: $($mul.position -join ',')"
    'failed at op 5, file unchanged; next edit saved only its own change'
}

Add-Check 'sg.window-open' 'cliext.shadergraph' 'with the graph open in a Shader Graph window, add and edit refuse (exit 6, naming --force) and save nothing' {
    Need-SgIds
    $open = Write-WorkFile 'sg-open.cs' "UnityEditor.AssetDatabase.OpenAsset(UnityEditor.AssetDatabase.LoadMainAssetAtPath(`"$($script:sgPath)`")); return `"opened`";"
    $close = Write-WorkFile 'sg-close.cs' 'int n = 0; foreach (var w in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEditor.EditorWindow>()) if (w.GetType().Name == "MaterialGraphEditWindow") { w.Close(); n++; } return n;'
    $before = Get-SgHash
    $o = Invoke-Cli @('script', 'eval', '--file', $open, '--format', 'json')
    try {
        Expect ($o.exit -eq 0) "couldn't open the window: $(Short ($o.stdout + $o.stderr))"
        $a = Invoke-Cli @('cliext.shadergraph', 'add', '--path', $script:sgPath, '--type', 'Math/Basic/Add', '--format', 'json')
        Expect-SgFailure $a 'open in a Shader Graph window.*cliext\.shadergraph edit --force true' $before 'add with the window open'
        Expect ($a.exit -eq 6) "add: exit $($a.exit), expected 6"
        $e = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-win.json' '[{"op":"add_node","type":"Math/Basic/Add"}]'), '--format', 'json')
        Expect-SgFailure $e 'open in a Shader Graph window.*pass --force true' $before 'edit with the window open'
        $i = Invoke-Cli @('cliext.shadergraph', 'inspect', '--path', $script:sgPath, '--details', 'false', '--format', 'json')
        Expect ($i.exit -eq 0) "inspect (read-only) with the window open: exit $($i.exit)"
    }
    finally { $c = Invoke-Cli @('script', 'eval', '--file', $close, '--format', 'json') }
    Expect ("$($c.stdout)" -match '1') "closed $(Short $c.stdout) window(s), expected 1"
    'add and edit refused while open; inspect works; window closed'
}

Add-Check 'sg.missing-asset' 'cliext.shadergraph' 'every cliext.shadergraph command given a graph that does not exist exits 13 at once, pointing at cliext.shadergraph create; a path that is not a graph is invalid usage' {
    $none = "$scratchRel/NoSuch.shadergraph"
    $cmds = @(
        @('inspect'), @('add', '--type', 'Math/Basic/Add'), @('add_property', '--type', 'float'), @('remove', '--id', 'x'),
        @('set', '--id', 'x', '--value', '1'), @('link', '--from', 'a:Out', '--to', 'b:A'), @('unlink', '--to', 'b:A')
    )
    $f = Write-WorkFile 'sg-one.json' '[{"op":"add_node","type":"Math/Basic/Add"}]'
    $bad = @()
    foreach ($c in $cmds + , @('edit', '--ops-file', $f)) {
        $r = Invoke-Cli (@('cliext.shadergraph', $c[0], '--path', $none) + @($c | Select-Object -Skip 1) + @('--format', 'json')) 60
        if ($r.exit -ne 13 -or "$($r.stderr)" -notmatch 'cliext\.shadergraph create' -or $r.ms -gt 30000) { $bad += "$($c[0]): exit $($r.exit) in $($r.ms) ms ($(Short $r.stderr 60))" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    Expect (-not (Test-Path (Join-Path $projectPath ($none -replace '/', '\')))) 'a graph was made at the missing path'
    $png = Invoke-Cli @('cliext.shadergraph', 'inspect', '--path', $sgTexture, '--format', 'json')
    Expect-SgFailure $png "isn't a Shader Graph" '' 'a .png as --path'
    Expect ($png.exit -eq 2) "a .png as --path: exit $($png.exit), expected 2"
    "all $($cmds.Count + 1) exit 13 naming cliext.shadergraph create; a .png exit 2"
}

Add-Check 'sg.edit-usage' 'cliext.shadergraph' 'cliext.shadergraph edit with no ops, bad JSON or an unknown op fails with the fix and saves nothing' {
    Need-SgIds
    $before = Get-SgHash
    $no = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--format', 'json')
    Expect-SgFailure $no '--ops-file' $before 'no ops'
    Expect ($no.exit -eq 2) "no ops: exit $($no.exit), expected 2"
    $json = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-badjson.json' '[{"op":'), '--format', 'json')
    Expect-SgFailure $json "aren't valid JSON" $before 'bad JSON'
    Expect ($json.exit -eq 2) "bad JSON: exit $($json.exit), expected 2"
    $op = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-badop.json' '[{"op":"frob"}]'), '--format', 'json')
    Expect-SgFailure $op 'Unknown op.*add_port, remove_port' $before 'unknown op'
    $mv = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-noop.json' "[{`"op`":`"move`",`"id`":`"$($script:sgIds.mul)`"}]"), '--format', 'json')
    Expect-SgFailure $mv 'move needs x' $before 'a move with no x or y'
    $rn = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-norename.json' '[{"op":"rename","id":"Tint"}]'), '--format', 'json')
    Expect-SgFailure $rn 'rename needs a name' $before 'a rename with no name'
    $sv = Invoke-Cli @('cliext.shadergraph', 'edit', '--path', $script:sgPath, '--ops-file', (Write-WorkFile 'sg-noval.json' '[{"op":"set","id":"Tint"}]'), '--format', 'json')
    Expect-SgFailure $sv 'set needs a value' $before 'a set with no value'
    "no ops 2, bad JSON 2, unknown op $($op.exit), empty move/rename/set $($mv.exit)/$($rn.exit)/$($sv.exit)"
}

# --- cliext.animator, cliext.anim -------------------------------------------------------------------------------------

# Scratch copies of the TestLevel models (new GUIDs, via AssetDatabase.CopyAsset), a generic .anim and a two-state
# controller, made once by Need-AnimAssets; the committed assets are only read.
$script:animCtl = "$scratchRel/CliTestAnim.controller"
$script:animClip = "$scratchRel/CliTestBob.anim"
$script:animIdle = "$scratchRel/CliTestIdle.fbx"
$script:animChar = "$scratchRel/CliTestCharacter.fbx"
$script:animMask = "$scratchRel/CliTestUpper.mask"
$script:animReady = $false
$animRow = 'cliext.animator, cliext.anim'

function Need-AnimAssets {
    if ($script:animReady) { return }
    $code = @'
string dir = "{{dir}}";
if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests")) UnityEditor.AssetDatabase.CreateFolder("Assets", "_CliextTests");
if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests", "CliTest");
foreach (var p in new[] { "{{ctl}}", "{{clip}}", "{{idle}}", "{{char}}", "{{mask}}" }) UnityEditor.AssetDatabase.DeleteAsset(p);
if (!UnityEditor.AssetDatabase.CopyAsset("Assets/TestLevel/Animations/standing idle.fbx", "{{idle}}")) return "copy idle failed";
if (!UnityEditor.AssetDatabase.CopyAsset("Assets/TestLevel/Characters/characterMedium.fbx", "{{char}}")) return "copy character failed";
var clip = new UnityEngine.AnimationClip();
clip.SetCurve("", typeof(UnityEngine.Transform), "m_LocalPosition.y", UnityEngine.AnimationCurve.Linear(0, 0, 1, 1));
UnityEditor.AssetDatabase.CreateAsset(clip, "{{clip}}");
var c = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath("{{ctl}}");
c.AddParameter("Speed", UnityEngine.AnimatorControllerParameterType.Float);
c.AddParameter("Jump", UnityEngine.AnimatorControllerParameterType.Trigger);
c.AddParameter("Grounded", UnityEngine.AnimatorControllerParameterType.Bool);
var sm = c.layers[0].stateMachine;
var idle = sm.AddState("Idle");
idle.motion = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("{{idle}}").OfType<UnityEngine.AnimationClip>().First(x => !x.name.StartsWith("__preview__"));
var run = sm.AddState("Run");
var t = idle.AddTransition(run); t.AddCondition(UnityEditor.Animations.AnimatorConditionMode.Greater, 0.1f, "Speed"); t.hasExitTime = false;
var back = run.AddTransition(idle); back.AddCondition(UnityEditor.Animations.AnimatorConditionMode.Less, 0.1f, "Speed"); back.hasExitTime = false;
sm.defaultState = idle;
UnityEditor.EditorUtility.SetDirty(c);
UnityEditor.AssetDatabase.SaveAssets();
return "ok";
'@
    $code = $code.Replace('{{dir}}', $scratchRel).Replace('{{ctl}}', $script:animCtl).Replace('{{clip}}', $script:animClip).Replace('{{idle}}', $script:animIdle).Replace('{{char}}', $script:animChar).Replace('{{mask}}', $script:animMask)
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'anim-setup.cs' $code), '--timeout', '120', '--format', 'json') 180
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok') "scratch animation assets: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $script:animReady = $true
}
function Get-AnimHash([string]$rel) { (Get-FileHash (Join-Path $projectPath ($rel -replace '/', '\'))).Hash }
function Write-AnimOps([string]$name, [string]$json) { Write-WorkFile $name $json }
function Get-AnimController { $r = Invoke-Cli @('cliext.animator', 'inspect', '--path', $script:animCtl, '--format', 'json'); Expect ($r.exit -eq 0) "inspect: exit $($r.exit): $(Short $r.stderr)"; $r.json }
function Get-AnimClip([string]$path) { $r = Invoke-Cli @('cliext.anim', 'clip', '--path', $path, '--keys', 'true', '--format', 'json'); Expect ($r.exit -eq 0) "clip: exit $($r.exit): $(Short $r.stderr)"; $r.json }
function Get-AnimTakes { $r = Invoke-Cli @('cliext.anim', 'takes', '--path', $script:animIdle, '--format', 'json'); Expect ($r.exit -eq 0) "takes: exit $($r.exit): $(Short $r.stderr)"; $r.json }
function Get-AnimKeys($clip, [string]$property) { @((@($clip.curves) | Where-Object { $_.property -eq $property } | Select-Object -First 1).values) -join ' ' }
function Get-AnimMemory {
    # The controller as the Editor holds it in memory (not reloaded from disk): states with their transition counts,
    # sub-state machines, parameters with defaults, and the first layer's default state.
    $code = @'
var c = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("{{ctl}}");
var sm = c.layers[0].stateMachine;
return string.Join(",", sm.states.Select(s => s.state.name + "[" + s.state.transitions.Length + "]" + (s.state.motion == null ? "" : "=" + s.state.motion.name)))
    + "|" + string.Join(",", sm.stateMachines.Select(m => m.stateMachine.name))
    + "|" + string.Join(",", c.parameters.Select(p => p.name + "=" + p.defaultFloat.ToString(System.Globalization.CultureInfo.InvariantCulture)))
    + "|" + c.layers.Length + "|" + (sm.defaultState != null ? sm.defaultState.name : "");
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'anim-mem.cs' $code.Replace('{{ctl}}', $script:animCtl)), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}
function Expect-AnimFailure($r, [string]$hint, [string]$file, [string]$before, [string]$what, [int]$exit = 6) {
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ($r.exit -eq $exit) "${what}: exit $($r.exit), expected ${exit}: $(Short ($r.stdout + $r.stderr))"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    Expect ("$($r.stderr)" -notmatch '   at |Exception:') "${what}: a stack trace instead of a message: $(Short $r.stderr)"
    if ($before) { Expect ((Get-AnimHash $file) -eq $before) "${what}: $file changed on disk" }
}
function Test-AnimFailures([string[]]$command, [string]$file, [object[]]$cases) {
    # Each case: ops JSON, the hint the error must hold, a label. Every one exits 6 and leaves the file as it was.
    $before = Get-AnimHash $file
    $seen = @()
    $i = 0
    foreach ($c in $cases) {
        $i++
        $r = Invoke-Cli ($command + @('--ops-file', (Write-AnimOps "anim-fail-$i.json" $c[0]), '--format', 'json'))
        Expect-AnimFailure $r $c[1] $file $before $c[2]
        Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 0) "$($c[2]): reply $(Short $r.stdout)"
        $seen += $c[2]
    }
    "$($seen.Count) failures exit 6 with the fix, file unchanged"
}

Add-Check 'anim.takes' $animRow 'cliext.anim takes lists a copied model''s rig, take and clip; a missing model exits 13 saying how to get one, a .anim exits 2' {
    Need-AnimAssets
    $t = Get-AnimTakes
    Expect ($t.animationType -eq 'Human') "rig: $($t.animationType)"
    Expect (@($t.takes | ForEach-Object { $_.name }) -contains 'mixamo.com') "takes: $(Short ($t.takes | ConvertTo-Json -Compress))"
    Expect (@($t.clips | ForEach-Object { $_.name }) -contains 'standing idle') "clips: $(Short ($t.clips | ConvertTo-Json -Compress))"
    $none = Invoke-Cli @('cliext.anim', 'takes', '--path', "$scratchRel/NoSuch.fbx", '--format', 'json')
    Expect-AnimFailure $none 'No model at.*Import the model' '' '' 'missing model' 13
    $anim = Invoke-Cli @('cliext.anim', 'takes', '--path', $script:animClip, '--format', 'json')
    Expect-AnimFailure $anim 'is an AnimationClip, not a model' '' '' 'a .anim as the model' 2
    "Human rig, take mixamo.com, clip 'standing idle'; missing 13, .anim 2"
}

Add-Check 'anim.edit-clips' $animRow 'cliext.anim edit_clips adds a clip, sets loop and events, and takes shows them after the reimport; bad clip names, ranges, events, masks and no-op ops fail and leave the import settings as they were' {
    Need-AnimAssets
    $ops = '[{"op":"add_clip","name":"IdleShort","first":0,"last":20,"set":"loopTime=true"},{"op":"set_clip","name":"standing idle","set":"loopPose=true"},{"op":"set_events","name":"IdleShort","events":"0.5:Step;0.9:Step:Left"}]'
    $r = Invoke-Cli @('cliext.anim', 'edit_clips', '--path', $script:animIdle, '--ops-file', (Write-AnimOps 'anim-clips.json' $ops), '--format', 'json') 180
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 3) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $t = Get-AnimTakes
    $short = @($t.clips) | Where-Object { $_.name -eq 'IdleShort' }
    Expect ($null -ne $short -and $short.first -eq 0 -and $short.last -eq 20 -and $short.loopTime -eq $true) "IdleShort: $(Short ($short | ConvertTo-Json -Compress))"
    Expect ((@($short.events) -join ',') -eq '0.5:Step,0.9:Step:Left') "events: $(@($short.events) -join ',')"
    Expect ((@($t.clips) | Where-Object { $_.name -eq 'standing idle' }).loopPose -eq $true) 'standing idle loopPose not set'
    $sub = Invoke-Cli @('cliext.anim', 'clip', '--path', "$($script:animIdle)@IdleShort", '--format', 'json')
    Expect ($sub.exit -eq 0 -and [math]::Abs($sub.json.length - (20 / 30)) -lt 0.01) "the imported IdleShort clip: exit $($sub.exit): $(Short ($sub.stdout + $sub.stderr))"
    $meta = "$($script:animIdle).meta"
    $detail = Test-AnimFailures @('cliext.anim', 'edit_clips', '--path', $script:animIdle) $meta @(
        @('[{"op":"set_clip","name":"IdleShrt","set":"loopTime=false"}]', 'Did you mean: IdleShort', 'unknown clip, close match'),
        @('[{"op":"remove_clip","name":"Zzqx"}]', 'Clips:.*cliext\.anim takes', 'unknown clip, no match'),
        @('[{"op":"add_clip","first":0,"last":10}]', 'add_clip needs a name', 'add_clip without a name'),
        @('[{"op":"add_clip","name":"IdleShort"}]', 'already has a clip', 'duplicate clip name'),
        @('[{"op":"add_clip","name":"Back","first":40,"last":10}]', 'first must be 0 or more and last after it', 'last before first'),
        @('[{"op":"add_clip","name":"X","take":"Nope"}]', 'No take.*mixamo\.com', 'unknown take'),
        @('[{"op":"set_clip","name":"IdleShort"}]', 'set_clip needs first, last or set', 'set_clip that changes nothing'),
        @('[{"op":"set_clip","name":"IdleShort","set":"loopPose=maybe"}]', 'true or false', 'non-boolean setting'),
        @('[{"op":"set_events","name":"IdleShort","events":"x:Step"}]', "isn't a number", 'non-numeric event time'),
        @('[{"op":"set_events","name":"IdleShort","events":"1.5:Step"}]', 'normalized times from 0 to 1', 'event time past the end'),
        @('[{"op":"set_mask","name":"IdleShort","mask":"Assets/_CliextTests/CliTest/NoSuch.mask"}]', 'cliext\.anim mask --path', 'missing mask'),
        @('[{"op":"frob"}]', 'Unknown op.*add_clip, set_clip', 'unknown op')
    )
    $before = Get-AnimHash $meta
    $batch = '[{"op":"remove_clip","name":"standing idle"},{"op":"add_clip","name":"Extra"},{"op":"set_clip","name":"IdleShort","set":"loopTime=false"},{"op":"set_clip","name":"NoSuchClip","set":"loopTime=true"}]'
    $b = Invoke-Cli @('cliext.anim', 'edit_clips', '--path', $script:animIdle, '--ops-file', (Write-AnimOps 'anim-clips-batch.json' $batch), '--format', 'json')
    Expect-AnimFailure $b 'Op 3 \(set_clip\) failed.*Nothing was saved' $meta $before 'batch with one bad op'
    Expect ($b.json.failedAt -eq 3 -and $b.json.saved -eq $false) "batch reply: $(Short $b.stdout)"
    $t2 = Get-AnimTakes
    Expect ((@($t2.clips | ForEach-Object { $_.name }) -join ',') -eq 'standing idle,IdleShort') "clips after the failed batch: $(@($t2.clips | ForEach-Object { $_.name }) -join ',')"
    Expect ((@($t2.clips) | Where-Object { $_.name -eq 'IdleShort' }).loopTime -eq $true) "the failed batch's loopTime=false was kept"
    "IdleShort 0-20 looping with 2 events; $detail; bad batch saved nothing"
}

Add-Check 'anim.clip' $animRow 'cliext.anim clip describes an .anim and a model clip; a missing .anim exits 13 naming animation.clip create, a missing model clip lists the clips, a controller exits 2' {
    Need-AnimAssets
    $c = Get-AnimClip $script:animClip
    Expect ((Get-AnimKeys $c 'm_LocalPosition.y') -eq '0=0 1=1') "y keys: $(Get-AnimKeys $c 'm_LocalPosition.y')"
    Expect ($c.length -eq 1 -and $c.humanoid -eq $false) "length $($c.length), humanoid $($c.humanoid)"
    $m = Get-AnimClip "$($script:animIdle)@standing idle"
    Expect ($m.humanoid -eq $true -and $m.length -gt 1) "model clip: humanoid $($m.humanoid), length $($m.length)"
    $none = Invoke-Cli @('cliext.anim', 'clip', '--path', "$scratchRel/NoSuch.anim", '--format', 'json')
    Expect-AnimFailure $none 'animation\.clip create --path' '' '' 'missing .anim' 13
    $sub = Invoke-Cli @('cliext.anim', 'clip', '--path', "$($script:animIdle)@Standing Idel", '--format', 'json')
    Expect-AnimFailure $sub "No clip 'Standing Idel'.*standing idle" '' '' 'missing model clip' 13
    $ctl = Invoke-Cli @('cliext.anim', 'clip', '--path', $script:animCtl, '--format', 'json')
    Expect-AnimFailure $ctl 'is an AnimatorController, not an AnimationClip' '' '' 'a controller as the clip' 2
    "y keys 0=0 1=1; model clip humanoid $([math]::Round($m.length, 2))s; missing 13, missing model clip 13, controller 2"
}

Add-Check 'anim.edit-clip' $animRow 'cliext.anim edit_clip adds keys, an event and settings, and clip shows them; a missing time, value, function or curve, a bad tangent or type, and no-op ops fail with the fix and save nothing' {
    Need-AnimAssets
    $ops = '[{"op":"set_key","property":"m_LocalPosition.y","time":0.5,"value":2,"tangent":"linear"},{"op":"set_key","property":"m_LocalPosition.y","time":1,"value":3},{"op":"add_event","time":0.25,"function":"Step","parameter":"Left"},{"op":"set_settings","set":"loopTime=true","value":30}]'
    $r = Invoke-Cli @('cliext.anim', 'edit_clip', '--path', $script:animClip, '--ops-file', (Write-AnimOps 'anim-clip.json' $ops), '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 4) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $c = Get-AnimClip $script:animClip
    Expect ((Get-AnimKeys $c 'm_LocalPosition.y') -eq '0=0 0.5=2 1=3') "y keys: $(Get-AnimKeys $c 'm_LocalPosition.y')"
    Expect (@($c.events).Count -eq 1 -and $c.events[0].function -eq 'Step' -and $c.events[0].stringParameter -eq 'Left') "events: $(Short ($c.events | ConvertTo-Json -Compress))"
    Expect ($c.settings.loopTime -eq $true -and $c.frameRate -eq 30) "loopTime $($c.settings.loopTime), frameRate $($c.frameRate)"
    $detail = Test-AnimFailures @('cliext.anim', 'edit_clip', '--path', $script:animClip) $script:animClip @(
        @('[{"op":"set_key","property":"m_LocalPosition.y","value":1}]', 'set_key needs a time', 'set_key without a time'),
        @('[{"op":"set_key","property":"m_LocalPosition.y","time":0.2}]', 'set_key needs a value', 'set_key without a value'),
        @('[{"op":"set_key","property":"m_LocalPosition.y","time":0.2,"value":"abc"}]', "isn't a number", 'non-numeric value'),
        @('[{"op":"set_key","property":"m_LocalPosition.y","time":0.2,"value":1,"tangent":"smooth"}]', 'Use one of: auto, linear, constant, free', 'unknown tangent'),
        @('[{"op":"set_key","type":"Transfrm","property":"m_LocalPosition.y","time":0.2,"value":1}]', 'Did you mean: Transform', 'misspelled component type'),
        @('[{"op":"remove_key","property":"m_LocalPosition.y","time":0.7}]', 'No key at 0.7s.*0, 0.5, 1', 'remove_key where there is none'),
        @('[{"op":"remove_key","path":"Arm","property":"m_LocalPosition.y","time":0}]', 'no curve.*Curves:.*cliext\.anim clip', 'remove_key on a curve the clip lacks'),
        @('[{"op":"add_event","time":0.1}]', 'add_event needs a function', 'add_event without a function'),
        @('[{"op":"add_event","function":"Hit"}]', 'add_event needs a time', 'add_event without a time'),
        @('[{"op":"remove_event","function":"Nope"}]', 'No event calling Nope.*0.25:Step', 'remove_event that matches nothing'),
        @('[{"op":"remove_event"}]', 'needs a time or a function', 'remove_event with nothing to match'),
        @('[{"op":"set_settings"}]', 'set_settings needs', 'set_settings that changes nothing'),
        @('[{"op":"set_settings","set":"looptim=true"}]', 'no settable .looptim.*loopTime', 'unknown setting')
    )
    $imp = Invoke-Cli @('cliext.anim', 'edit_clip', '--path', "$($script:animIdle)@standing idle", '--ops-file', (Write-AnimOps 'anim-imported.json' '[{"op":"add_event","time":0.1,"function":"X"}]'), '--format', 'json')
    Expect-AnimFailure $imp 'cliext\.anim edit_clips on the model' "$($script:animIdle).meta" (Get-AnimHash "$($script:animIdle).meta") 'a model''s clip' 2
    "y 0=0 0.5=2 1=3, Step(Left) at 0.25, loop at 30 fps; $detail; a model's clip exit 2"
}

Add-Check 'anim.edit-clip-rollback' $animRow 'an edit_clip batch with one bad op saves nothing, on disk or in memory, and the next good edit saves only its own change' {
    Need-AnimAssets
    $before = Get-AnimHash $script:animClip
    $c0 = Get-AnimClip $script:animClip
    $ops = '[{"op":"set_key","property":"m_LocalPosition.x","time":0,"value":5},{"op":"remove_key","property":"m_LocalPosition.y","time":0},{"op":"add_event","time":0.3,"function":"Hit"},{"op":"remove_event","function":"Step"},{"op":"set_settings","set":"loopTime=false","value":24},{"op":"remove_key","property":"m_LocalPosition.y","time":9}]'
    $r = Invoke-Cli @('cliext.anim', 'edit_clip', '--path', $script:animClip, '--ops-file', (Write-AnimOps 'anim-clip-bad.json' $ops), '--format', 'json')
    Expect-AnimFailure $r 'Op 5 \(remove_key\) failed.*Nothing was saved' $script:animClip $before 'bad batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 5 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
    $c1 = Get-AnimClip $script:animClip
    Expect (($c1.curves | ConvertTo-Json -Compress) -eq ($c0.curves | ConvertTo-Json -Compress)) "curves in memory changed: $(Short ($c1.curves | ConvertTo-Json -Compress))"
    Expect ((@($c1.events | ForEach-Object { $_.function }) -join ',') -eq 'Step' -and $c1.settings.loopTime -eq $true -and $c1.frameRate -eq 30) "events/settings in memory: $(@($c1.events | ForEach-Object { $_.function }) -join ','), loopTime $($c1.settings.loopTime), $($c1.frameRate) fps"
    $ok = Invoke-Cli @('cliext.anim', 'edit_clip', '--path', $script:animClip, '--ops-file', (Write-AnimOps 'anim-clip-good.json' '[{"op":"add_event","time":0.75,"function":"Land"}]'), '--format', 'json')
    Expect ($ok.exit -eq 0) "good edit: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
    $text = Get-Content (Join-Path $projectPath ($script:animClip -replace '/', '\')) -Raw
    Expect ($text -match 'functionName: Land' -and $text -match 'functionName: Step' -and $text -notmatch 'functionName: Hit') 'the saved events are not Step and Land only'
    $c2 = Get-AnimClip $script:animClip
    Expect ((Get-AnimKeys $c2 'm_LocalPosition.y') -eq '0=0 0.5=2 1=3') "the failed batch's key removal was saved: $(Get-AnimKeys $c2 'm_LocalPosition.y')"
    Expect ((Get-AnimKeys $c2 'm_LocalPosition.x') -notmatch '=5') "the failed batch's x key was saved: $(Get-AnimKeys $c2 'm_LocalPosition.x')"
    Expect ($c2.settings.loopTime -eq $true -and $c2.frameRate -eq 30) "the failed batch's settings were saved: loopTime $($c2.settings.loopTime), $($c2.frameRate) fps"
    'failed at op 5, file and memory unchanged; next edit saved only its own event'
}

Add-Check 'anim.mask' $animRow 'cliext.anim mask creates a mask, lists a model''s transforms and turns one off; a path outside Assets, an unknown body part, a bad bool, a missing --from model or an unknown transform fail, create nothing and change nothing' {
    Need-AnimAssets
    $bare = $script:animMask -replace '\.mask$', ''
    $r = Invoke-Cli @('cliext.anim', 'mask', '--path', $bare, '--humanoid', 'Head=false;LeftArm=false', '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.created -eq $true -and $r.json.path -eq $script:animMask) "create: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $f = Invoke-Cli @('cliext.anim', 'mask', '--path', $script:animMask, '--from', $script:animChar, '--transforms', 'Root/HipsCtrl/Hips/Spine/*=false', '--format', 'json')
    Expect ($f.exit -eq 0 -and $f.json.created -eq $false -and $f.json.transforms -gt 10) "--from and --transforms: exit $($f.exit): $(Short ($f.stdout + $f.stderr))"
    $off = @($f.json.transformsOff)
    Expect ($off.Count -gt 1 -and @($off | Where-Object { $_ -ne 'Root/HipsCtrl/Hips/Spine' -and $_ -notlike 'Root/HipsCtrl/Hips/Spine/*' }).Count -eq 0) "transforms off: $(Short ($off -join ', '))"
    $read = Write-WorkFile 'anim-mask.cs' ("var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AvatarMask>(`"$($script:animMask)`"); return m.GetHumanoidBodyPartActive(UnityEngine.AvatarMaskBodyPart.Head) + `"/`" + m.GetHumanoidBodyPartActive(UnityEngine.AvatarMaskBodyPart.LeftArm) + `"/`" + m.GetHumanoidBodyPartActive(UnityEngine.AvatarMaskBodyPart.RightArm) + `"/`" + m.transformCount;")
    $s = Invoke-Cli @('script', 'eval', '--file', $read, '--format', 'json')
    Expect ("$($s.stdout)" -match "False/False/True/$($f.json.transforms)") "the mask asset: $(Short ($s.stdout + $s.stderr))"
    $before = Get-AnimHash $script:animMask
    $cases = @(
        @(@('--path', 'NotAssets/CliTest.mask', '--humanoid', 'Head=false'), "isn't under Assets/", 2, 'path outside Assets'),
        @(@('--path', "$scratchRel/CliTestNew.mask", '--humanoid', 'Hed=false'), 'Use one of: Root, Body, Head.*RightHandIK\.', 2, 'unknown body part'),
        @(@('--path', $script:animMask, '--humanoid', 'Head=maybe'), 'true or false', 2, 'non-boolean body part'),
        @(@('--path', "$scratchRel/CliTestNew.mask", '--from', "$scratchRel/NoSuch.fbx"), 'No model at.*Import the model', 13, 'missing --from model'),
        @(@('--path', $script:animMask, '--humanoid', 'RightArm=false', '--transforms', 'Hips=false'), 'Did you mean: Root/HipsCtrl', 6, 'unknown transform after a good body part'),
        @(@('--path', "$scratchRel/CliTestNew.mask", '--transforms', 'Hips=false'), 'pass --from a model', 6, 'transforms on a new mask with none')
    )
    $seen = @()
    foreach ($k in $cases) {
        $x = Invoke-Cli (@('cliext.anim', 'mask') + $k[0] + @('--format', 'json'))
        Expect-AnimFailure $x $k[1] $script:animMask $before $k[3] $k[2]
        $seen += "$($k[3]) $($x.exit)"
    }
    Expect (-not (Test-Path (Join-Path $scratch 'CliTestNew.mask'))) 'a failed call created CliTestNew.mask'
    Expect (-not (Test-Path (Join-Path $projectPath 'NotAssets'))) 'a NotAssets folder was made'
    $s2 = Invoke-Cli @('script', 'eval', '--file', $read, '--format', 'json')
    Expect ("$($s2.stdout)" -match "False/False/True/") "a failed call changed the mask in memory: $(Short $s2.stdout)"
    "created with Head and LeftArm off; $($f.json.transforms) transforms, $($off.Count) off; $($seen -join ', ')"
}

Add-Check 'anim.mask-folders' $animRow 'cliext.anim mask (and cliext.shadergraph create) into folders that do not exist make them; into a folder made on disk but not imported yet, they use it and make no "<Folder> 1" beside it' {
    $paths = "$scratchRel/MaskNew/Deep/Upper.mask", "$scratchRel/MaskDisk/Upper.mask"
    $diskDir = Join-Path $scratch 'MaskDisk'
    $sgDir = Join-Path $scratch 'SgDisk'
    New-Item -ItemType Directory -Force $diskDir | Out-Null
    New-Item -ItemType Directory -Force $sgDir | Out-Null
    $results = @()
    foreach ($p in $paths) {
        $r = Invoke-Cli @('cliext.anim', 'mask', '--path', $p, '--humanoid', 'Head=false', '--format', 'json')
        Expect ($r.exit -eq 0 -and $r.json.created -eq $true -and $r.json.path -eq $p) "mask at ${p}: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
        $results += $p
    }
    $sg = Invoke-Cli @('cliext.shadergraph', 'create', '--path', "$scratchRel/SgDisk/OnDisk.shadergraph", '--target', 'Universal', '--material', 'Unlit', '--format', 'json') 180
    Expect ($sg.exit -eq 0 -and $sg.json.path -eq "$scratchRel/SgDisk/OnDisk.shadergraph") "shadergraph create into an on-disk folder: exit $($sg.exit): $(Short ($sg.stdout + $sg.stderr))"
    foreach ($stray in 'MaskDisk 1', 'SgDisk 1', 'MaskNew 1') { Expect (-not (Test-Path (Join-Path $scratch $stray))) "a stray '$stray' folder was made" }
    Expect ((Test-Path "$diskDir.meta") -and (Test-Path "$sgDir.meta")) 'the on-disk folders were not imported (no .meta)'
    "masks at $($results -join ', '); shader graph in the on-disk folder; no '<Folder> 1'"
}

Add-Check 'anim.check' $animRow 'cliext.anim check passes humanoid clips on the humanoid model, fails (exit 6, saying why) on a generic clip, and a missing model or --clips entry exits 13' {
    Need-AnimAssets
    $ok = Invoke-Cli @('cliext.anim', 'check', '--model', $script:animChar, '--clips', $script:animCtl, '--format', 'json')
    Expect ($ok.exit -eq 0) "controller clips: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
    Expect (@(@($ok.json) | Where-Object { $_.clip -eq 'standing idle' -and $_.ok -eq $true }).Count -eq 1) "no ok record for standing idle: $(Short $ok.stdout)"
    $bad = Invoke-Cli @('cliext.anim', 'check', '--model', $script:animChar, '--clips', "$($script:animCtl);$($script:animClip)", '--format', 'json')
    Expect ($bad.exit -eq 6) "a generic clip: exit $($bad.exit), expected 6"
    Expect (@(@($bad.json) | Where-Object { $_.ok -eq $false -and "$($_.issues)" -match 'Generic clip on a humanoid model' }).Count -eq 1) "no record for the generic clip: $(Short $bad.stdout)"
    Expect ("$($bad.stderr)" -match 'problem') "stderr doesn't say why it exited 6: $(Short $bad.stderr)"
    $nm = Invoke-Cli @('cliext.anim', 'check', '--model', "$scratchRel/NoSuch.fbx", '--clips', $script:animCtl, '--format', 'json')
    Expect-AnimFailure $nm 'No model or prefab at' '' '' 'missing model' 13
    $nc = Invoke-Cli @('cliext.anim', 'check', '--model', $script:animChar, '--clips', "$scratchRel/NoSuch.controller", '--format', 'json')
    Expect-AnimFailure $nc "nothing at '.*NoSuch\.controller'" '' '' 'missing --clips entry' 13
    "humanoid clips ok; generic clip exit 6; missing model 13, missing --clips 13"
}

Add-Check 'anim.animator-inspect-validate' $animRow 'cliext.animator inspect describes the controller and validate flags a transition that can never fire (exit 6), which an edit fixes' {
    Need-AnimAssets
    $g = Get-AnimController
    Expect ((@($g.parameters | ForEach-Object { $_.name }) -join ',') -eq 'Speed,Jump,Grounded') "parameters: $(@($g.parameters | ForEach-Object { $_.name }) -join ',')"
    $idle = @($g.layers[0].states) | Where-Object { $_.path -eq 'Idle' }
    Expect ($g.layers[0].defaultState -eq 'Idle' -and "$($idle.motion)" -match 'CliTestIdle\.fbx@standing idle') "Idle: $(Short ($idle | ConvertTo-Json -Compress))"
    Expect ((@($idle.transitions)[0].conditions -join ',') -eq 'Speed Greater 0.1') "Idle -> Run: $(Short ($idle.transitions | ConvertTo-Json -Compress))"
    $v = Invoke-Cli @('cliext.animator', 'validate', '--path', $script:animCtl, '--format', 'json')
    Expect ($v.exit -eq 0) "validate: exit $($v.exit): $(Short ($v.stdout + $v.stderr))"
    Expect (@(@($v.json) | Where-Object { $_.where -eq 'Base Layer/Run' -and $_.problem -match 'No motion' }).Count -eq 1) "no warning for Run's missing motion: $(Short $v.stdout)"
    $e = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-never.json' '[{"op":"set_transition","from":"Run","to":"Idle","conditions":""}]'), '--format', 'json')
    Expect ($e.exit -eq 0 -and @($e.json.errors).Count -eq 1) "clearing Run -> Idle's conditions: exit $($e.exit), errors $(Short ($e.json.errors | ConvertTo-Json -Compress))"
    $bad = Invoke-Cli @('cliext.animator', 'validate', '--path', $script:animCtl, '--format', 'json')
    Expect ($bad.exit -eq 6 -and "$($bad.stderr)" -match 'error') "validate with a dead transition: exit $($bad.exit): $(Short $bad.stderr)"
    Expect (@(@($bad.json) | Where-Object { $_.severity -eq 'error' -and $_.problem -match 'never fire' }).Count -eq 1) "no error record: $(Short $bad.stdout)"
    $fix = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-fix.json' '[{"op":"set_transition","from":"Run","to":"Idle","conditions":"Speed Less 0.1"}]'), '--format', 'json')
    Expect ($fix.exit -eq 0 -and @($fix.json.errors).Count -eq 0) "restoring the condition: exit $($fix.exit): $(Short ($fix.stdout + $fix.stderr))"
    $ok = Invoke-Cli @('cliext.animator', 'validate', '--path', $script:animCtl, '--format', 'json')
    Expect ($ok.exit -eq 0) "validate after the fix: exit $($ok.exit)"
    'inspect shows Idle, its clip and transition; a dead transition makes validate exit 6; fixed, exit 0'
}

Add-Check 'anim.animator-edit' $animRow 'cliext.animator edit sets a motion, conditions, a 1D blend tree, a sub-state machine, a rename, a default and a layer in one call; inspect shows them all' {
    Need-AnimAssets
    $idleClip = "$($script:animIdle)@standing idle"
    $ops = @"
[{"op":"set_state","state":"Run","motion":"$idleClip","set":"speed=1.5;tag=Move"},
 {"op":"set_transition","from":"Idle","to":"Run","conditions":"Speed Greater 0.2;Grounded If","set":"duration=0.2"},
 {"op":"add_blendtree","state":"Move","parameter":"Speed","children":"$idleClip@0;$($script:animClip)@1"},
 {"op":"add_substatemachine","name":"Air"},
 {"op":"set_parameter","name":"Speed","value":"0.25"},
 {"op":"rename_state","state":"Move","name":"Locomotion"},
 {"op":"set_layer","set":"iKPass=true"}]
"@
    $r = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-edit.json' $ops), '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 7) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $g = Get-AnimController
    $l = $g.layers[0]
    $run = @($l.states) | Where-Object { $_.path -eq 'Run' }
    Expect ("$($run.motion)" -match 'standing idle' -and $run.speed -eq 1.5 -and $run.tag -eq 'Move') "Run: $(Short ($run | ConvertTo-Json -Compress))"
    $idle = @($l.states) | Where-Object { $_.path -eq 'Idle' }
    $t = @($idle.transitions)[0]
    Expect (($t.conditions -join ',') -eq 'Speed Greater 0.2,Grounded If' -and $t.duration -eq 0.2) "Idle -> Run: $(Short ($t | ConvertTo-Json -Compress))"
    $loco = @($l.states) | Where-Object { $_.path -eq 'Locomotion' }
    Expect ($null -ne $loco -and $loco.blendTree.type -eq 'Simple1D' -and $loco.blendTree.parameter -eq 'Speed' -and @($loco.blendTree.children).Count -eq 2) "Locomotion: $(Short ($loco | ConvertTo-Json -Compress -Depth 5))"
    Expect (@($l.subMachines) -contains 'Air' -and $l.ik -eq $true) "sub-machines $(@($l.subMachines) -join ','), ik $($l.ik)"
    Expect ((@($g.parameters) | Where-Object { $_.name -eq 'Speed' }).default -eq '0.25') 'Speed default is not 0.25'
    "7 ops in one call; Run has a motion, Idle -> Run 2 conditions, Locomotion a 1D tree of 2, Air, IK on"
}

Add-Check 'anim.animator-edit-errors' $animRow 'cliext.animator edit fails (exit 6) with the fix on unknown states, parameters, layers, modes, motions and blend tree parameters, bad numbers, and ops that would do nothing, saving nothing' {
    Need-AnimAssets
    Test-AnimFailures @('cliext.animator', 'edit', '--path', $script:animCtl) $script:animCtl @(
        @('[{"op":"remove_state","state":"Runn"}]', 'Did you mean: Run', 'unknown state, close match'),
        @('[{"op":"remove_state","state":"Zzqx"}]', 'States:.*cliext\.animator inspect', 'unknown state, no match'),
        @('[{"op":"set_transition","from":"Idle","to":"Run","conditions":"Sped Greater 0.1"}]', 'Did you mean: Speed', 'condition on a misspelled parameter'),
        @('[{"op":"set_transition","from":"Idle","to":"Run","conditions":"Speed Biggr 0.1"}]', 'Use one of: If, IfNot, Greater', 'unknown condition mode'),
        @('[{"op":"set_transition","from":"Idle","to":"Run","conditions":"Speed Greater abc"}]', "'abc' isn't a Single value", 'non-numeric threshold'),
        @('[{"op":"set_transition","from":"Idle","to":"Run","conditions":"Speed Greater"}]', 'needs a threshold', 'Greater without a threshold'),
        @('[{"op":"set_transition","from":"Run","to":"Locomotion"}]', 'set_transition needs conditions', 'set_transition that changes nothing'),
        @('[{"op":"remove_transition","from":"Run","to":"Run"}]', 'No transition from Run to Run.*inspect', 'missing transition'),
        @('[{"op":"set_state","state":"Run","motion":"Assets/_CliextTests/CliTest/NoSuch.anim"}]', 'animation\.clip create', 'missing motion'),
        @('[{"op":"set_state","state":"Run","set":"speed=fast"}]', "'fast' isn't a Single value", 'non-numeric speed'),
        @('[{"op":"set_state","state":"Run"}]', 'set_state needs a motion or set', 'set_state that changes nothing'),
        @('[{"op":"rename_state","state":"Run"}]', 'rename_state needs a name', 'rename without a name'),
        @('[{"op":"rename_state","state":"Run","name":"Idle"}]', 'already a state .Idle', 'rename onto another state'),
        @('[{"op":"set_default","state":"Idle"}]', 'already the default', 'set_default to the default'),
        @('[{"op":"add_substatemachine"}]', 'add_substatemachine needs a name', 'sub-state machine without a name'),
        @('[{"op":"remove_substatemachine","state":"Ground"}]', 'Sub-state machines: Air', 'unknown sub-state machine'),
        @('[{"op":"set_parameter","name":"Jump","value":"1"}]', 'Trigger: it has no default', 'default on a Trigger'),
        @('[{"op":"set_parameter","name":"Speed"}]', 'set_parameter needs a value', 'set_parameter without a value'),
        @('[{"op":"remove_parameter","name":"Sped"}]', 'Did you mean: Speed', 'remove a misspelled parameter'),
        @('[{"op":"set_layer","set":"blendingMode=Mix"}]', 'Use one of: Override, Additive', 'unknown blending mode'),
        @('[{"op":"set_layer"}]', 'set_layer needs set', 'set_layer that changes nothing'),
        @('[{"op":"set_layer","layer":"Upper","set":"defaultWeight=1"}]', "No layer 'Upper'.*Base Layer", 'unknown layer'),
        @('[{"op":"remove_layer"}]', 'remove_layer needs the layer', 'remove_layer without a layer'),
        @('[{"op":"add_blendtree","state":"Strafe","type":"Simple3D","parameter":"Speed"}]', 'Use one of: Simple1D, SimpleDirectional2D', 'unknown blend tree type'),
        @('[{"op":"add_blendtree","state":"Strafe","parameter":"Nope"}]', "No parameter 'Nope'", 'blend tree on a missing parameter'),
        @('[{"op":"add_blendtree","state":"Strafe","parameter":"Grounded"}]', 'needs a Float parameter', 'blend tree on a Bool'),
        @('[{"op":"add_blendtree","state":"Strafe","parameter":"Speed","children":"Assets/_CliextTests/CliTest/CliTestBob.anim@x"}]', "'x' isn't a Single value", 'non-numeric child threshold'),
        @('[{"op":"frob"}]', 'Unknown op.*remove_state, rename_state', 'unknown op')
    )
}

Add-Check 'anim.motion-refs' $animRow 'a bare model path with several clips, or a clip name two clips share, as a set_state motion, a blend tree child or an anim clip path exits 2 listing the clips with their lengths (and edit_clips for a shared name), saving nothing; a model with one clip may be given bare' {
    Need-AnimAssets
    $before = Get-AnimHash $script:animCtl
    $run = 'Assets/TestLevel/Animations/kenney run.fbx'
    $idle = 'Assets/TestLevel/Animations/kenney idle.fbx'
    $cases = @(
        @("[{`"op`":`"set_state`",`"state`":`"Run`",`"motion`":`"$run`"}]", "is a model with 2 clips.*$([regex]::Escape($run))@<clip>.*kenney run 1 \(", 'bare model with 2 clips'),
        @("[{`"op`":`"set_state`",`"state`":`"Run`",`"motion`":`"$idle@kenney idle`"}]", "2 clips named 'kenney idle'.*1 frame.*cliext\.anim edit_clips", 'a name two clips share'),
        @("[{`"op`":`"add_blendtree`",`"state`":`"Strafe`",`"parameter`":`"Speed`",`"children`":`"$run@0;$($script:animClip)@1`"}]", 'is a model with 2 clips', 'bare model as a blend tree child')
    )
    $seen = @()
    $i = 0
    foreach ($c in $cases) {
        $i++
        $r = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps "anim-ref-$i.json" $c[0]), '--format', 'json')
        Expect-AnimFailure $r $c[1] $script:animCtl $before $c[2] 2
        $seen += "$($c[2]) $($r.exit)"
    }
    $clip = Invoke-Cli @('cliext.anim', 'clip', '--path', "$idle@kenney idle", '--format', 'json')
    Expect-AnimFailure $clip "2 clips named 'kenney idle'" '' '' 'anim clip by a shared name' 2
    $seen += "anim clip $($clip.exit)"
    $speed0 = (@((Get-AnimController).layers[0].states) | Where-Object { $_.path -eq 'Run' }).speed
    $one = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-ref-one.json' "[{`"op`":`"set_state`",`"state`":`"Run`",`"motion`":`"Assets/TestLevel/Animations/standing idle.fbx`",`"set`":`"speed=1.25`"}]"), '--format', 'json')
    Expect ($one.exit -eq 0) "a bare model with one clip: exit $($one.exit): $(Short ($one.stdout + $one.stderr))"
    $runState = @((Get-AnimController).layers[0].states) | Where-Object { $_.path -eq 'Run' }
    Expect ("$($runState.motion)" -match 'standing idle') "Run's motion: $($runState.motion)"
    $back = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-ref-back.json' "[{`"op`":`"set_state`",`"state`":`"Run`",`"set`":`"speed=$([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $speed0))`"}]"), '--format', 'json')
    Expect ($back.exit -eq 0) "putting Run's speed back: exit $($back.exit): $(Short ($back.stdout + $back.stderr))"
    "$($seen -join ', '); the one-clip model given bare sets standing idle"
}

Add-Check 'anim.animator-rollback' $animRow 'an animator edit batch with one bad op saves nothing, on disk or in memory (removed states and transitions come back), and the next good edit saves only its own change' {
    Need-AnimAssets
    $before = Get-AnimHash $script:animCtl
    $m0 = Get-AnimMemory
    $g0 = Get-AnimController
    $ops = '[{"op":"add_substatemachine","name":"Ground"},{"op":"remove_state","state":"Run"},{"op":"remove_substatemachine","state":"Air"},{"op":"remove_parameter","name":"Grounded"},{"op":"rename_state","state":"Idle","name":"Rest"},{"op":"set_state","state":"Locomotion","set":"speed=3"},{"op":"add_blendtree","state":"Strafe","parameter":"Speed"},{"op":"set_parameter","name":"Speed","value":"9"},{"op":"remove_state","state":"NoSuchState"}]'
    $r = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-bad-batch.json' $ops), '--format', 'json')
    Expect-AnimFailure $r 'Op 8 \(remove_state\) failed.*Nothing was saved' $script:animCtl $before 'bad batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 8 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
    $m1 = Get-AnimMemory
    Expect ($m1 -eq $m0) "in memory: '$m0' -> '$m1'"
    $good = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-good.json' '[{"op":"set_state","state":"Idle","set":"tag=Rest"}]'), '--format', 'json')
    Expect ($good.exit -eq 0) "good edit: exit $($good.exit): $(Short ($good.stdout + $good.stderr))"
    $g2 = Get-AnimController
    Expect ((@($g2.layers[0].states) | Where-Object { $_.path -eq 'Idle' }).tag -eq 'Rest') "the good edit's tag isn't there"
    # Apart from Idle's tag, the controller is as it was before the failed batch.
    $strip = { param($g) foreach ($s in @($g.layers[0].states)) { if ($s.path -eq 'Idle') { $s.tag = '' } }; $g | ConvertTo-Json -Compress -Depth 8 }
    $j0 = & $strip $g0; $j2 = & $strip $g2
    Expect ($j2 -eq $j0) "the saved controller differs beyond the good edit: $(Short $j2 200)"
    $text = Get-Content (Join-Path $projectPath ($script:animCtl -replace '/', '\')) -Raw
    Expect ($text -match 'm_Name: Run' -and $text -match 'm_Name: Air' -and $text -notmatch 'm_Name: (Ground|Strafe|Rest)\r?\n') 'the file on disk has the failed batch''s states or lost Run/Air'
    "failed at op 8, file and memory unchanged ($m1); next edit saved only Idle's tag"
}

Add-Check 'anim.missing-asset' $animRow 'every cliext.animator and cliext.anim command given an asset that does not exist exits 13 at once, naming how to make or get one; an asset of another type exits 2' {
    Need-AnimAssets
    $f = Write-AnimOps 'anim-one.json' '[{"op":"set_default","state":"Idle"}]'
    $noCtl = "$scratchRel/NoSuch.controller"; $noAnim = "$scratchRel/NoSuch.anim"; $noFbx = "$scratchRel/NoSuch.fbx"
    $cases = @(
        @(@('cliext.animator', 'inspect', '--path', $noCtl), 'animation\.animator create_controller'),
        @(@('cliext.animator', 'validate', '--path', $noCtl), 'animation\.animator create_controller'),
        @(@('cliext.animator', 'edit', '--path', $noCtl, '--ops-file', $f), 'animation\.animator create_controller'),
        @(@('cliext.anim', 'clip', '--path', $noAnim), 'animation\.clip create'),
        @(@('cliext.anim', 'edit_clip', '--path', $noAnim, '--ops-file', $f), 'animation\.clip create'),
        @(@('cliext.anim', 'takes', '--path', $noFbx), 'Import the model'),
        @(@('cliext.anim', 'edit_clips', '--path', $noFbx, '--ops-file', $f), 'Import the model'),
        @(@('cliext.anim', 'check', '--model', $noFbx, '--clips', $script:animCtl), 'Import the model'),
        @(@('cliext.anim', 'mask', '--path', "$scratchRel/CliTestNew.mask", '--from', $noFbx), 'Import the model')
    )
    $bad = @()
    foreach ($c in $cases) {
        $r = Invoke-Cli ($c[0] + @('--format', 'json')) 60
        if ($r.exit -ne 13 -or "$($r.stderr)" -notmatch $c[1] -or $r.ms -gt 30000) { $bad += "$($c[0][1]) $($c[0][2]): exit $($r.exit) in $($r.ms) ms ($(Short $r.stderr 80))" }
    }
    $wrong = @(
        @(@('cliext.animator', 'inspect', '--path', $script:animClip), 'is an AnimationClip, not an AnimatorController'),
        @(@('cliext.animator', 'edit', '--path', $script:animIdle, '--ops-file', $f), 'not an AnimatorController'),
        @(@('cliext.anim', 'edit_clip', '--path', $script:animCtl, '--ops-file', $f), 'is an AnimatorController, not an AnimationClip'),
        @(@('cliext.anim', 'edit_clips', '--path', $script:animClip, '--ops-file', $f), 'not a model'),
        @(@('cliext.anim', 'check', '--model', $script:animClip, '--clips', $script:animCtl), 'not a model or prefab'),
        @(@('cliext.anim', 'mask', '--path', $script:animClip), 'not an AvatarMask')
    )
    foreach ($c in $wrong) {
        $r = Invoke-Cli ($c[0] + @('--format', 'json')) 60
        if ($r.exit -ne 2 -or "$($r.stderr)" -notmatch $c[1]) { $bad += "$($c[0][1]) $($c[0][2]) (wrong type): exit $($r.exit) ($(Short $r.stderr 80))" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    foreach ($left in 'NoSuch.controller', 'NoSuch.anim', 'NoSuch.fbx', 'CliTestNew.mask', 'CliTestBob.anim.mask') { Expect (-not (Test-Path (Join-Path $scratch $left))) "a failed call left $left" }
    "all $($cases.Count) missing exit 13 with the way to get one; $($wrong.Count) wrong types exit 2"
}

Add-Check 'anim.play-mode' $animRow 'cliext.animator live and drive refuse Edit mode with the fix; in Play mode they read and drive the Player''s Animator, reject bad parameters, values and states, and an edit still applies and stays' {
    Need-AnimAssets
    foreach ($cmd in @(@('live'), @('drive', '--parameter', 'Speed', '--value', '1'))) {
        $e = Invoke-Cli (@('cliext.animator') + $cmd + @('--object', 'Player', '--format', 'json'))
        Expect-AnimFailure $e 'enter Play mode first \(editor\.playmode start\)' '' '' "$($cmd[0]) in Edit mode" 6
    }
    $none = Invoke-Cli @('cliext.animator', 'drive', '--object', 'Player', '--format', 'json')
    Expect-AnimFailure $none 'Pass --parameter with --value.*or --state' '' '' 'drive with nothing to do' 2
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    $open = @($scenes.json) | Where-Object { $_.isActive } | Select-Object -First 1
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $level = 'Assets/Scenes/TestLevel.unity'
    $openCs = { param($p) Write-WorkFile 'anim-open.cs' "UnityEditor.SceneManagement.EditorSceneManager.OpenScene(`"$p`", UnityEditor.SceneManagement.OpenSceneMode.Single); return UnityEngine.SceneManagement.SceneManager.GetActiveScene().path + `"|`" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;" }
    if ($open.path -ne $level) { [void](Invoke-Cli @('script', 'eval', '--file', (& $openCs $level), '--format', 'json')) }
    $assign = Write-WorkFile 'anim-assign.cs' ("var a = UnityEngine.GameObject.Find(`"Player`").GetComponentInChildren<UnityEngine.Animator>(); a.runtimeAnimatorController = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(`"$($script:animCtl)`"); return a.name;")
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $nc = Invoke-Cli @('cliext.animator', 'live', '--object', 'Player', '--format', 'json')
        Expect-AnimFailure $nc 'has no controller' '' '' 'live with no controller' 6
        $as = Invoke-Cli @('script', 'eval', '--file', $assign, '--format', 'json')
        Expect ($as.exit -eq 0) "assigning the controller in Play mode: $(Short ($as.stdout + $as.stderr))"
        $d = Invoke-Cli @('cliext.animator', 'drive', '--object', 'Player', '--parameter', 'Speed', '--value', '0.8', '--format', 'json')
        Expect ($d.exit -eq 0) "drive Speed: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        $b = Invoke-Cli @('cliext.animator', 'drive', '--object', 'Player', '--parameter', 'Grounded', '--value', 'true', '--format', 'json')
        Expect ($b.exit -eq 0) "drive Grounded: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
        $l = Invoke-Cli @('cliext.animator', 'live', '--object', 'Player', '--format', 'json')
        Expect ($l.exit -eq 0 -and $l.json.controller -eq 'CliTestAnim' -and $l.json.playing -eq $true) "live: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
        $p = @{}; foreach ($x in @($l.json.parameters)) { $p[$x.name] = "$($x.value)" }
        Expect ($p['Speed'] -eq '0.8' -and $p['Grounded'] -eq 'true') "live parameters: $(Short ($l.json.parameters | ConvertTo-Json -Compress))"
        $st = Invoke-Cli @('cliext.animator', 'drive', '--object', 'Player', '--state', 'Base Layer.Locomotion', '--fade', '0', '--format', 'json')
        Expect ($st.exit -eq 0) "drive --state: exit $($st.exit): $(Short ($st.stdout + $st.stderr))"
        $cases = @(
            @(@('--parameter', 'Speed', '--value', 'fast'), "isn't a Single value", 2, 'non-numeric value'),
            @(@('--parameter', 'Grounded'), 'pass --value \(true or false\)', 2, 'Bool without a value'),
            @(@('--parameter', 'Sped', '--value', '1'), 'Did you mean: Speed', 13, 'misspelled parameter'),
            @(@('--state', 'Base Layer.Fly'), "No state 'Base Layer.Fly'.*Base Layer\.Idle", 13, 'unknown state'),
            @(@('--state', 'Idle', '--layer', '3'), 'No layer 3', 2, 'layer out of range'),
            @(@('--parameter', 'Speed', '--value', '1', '--state', 'Idle'), 'not both', 2, 'parameter and state')
        )
        $seen = @()
        foreach ($c in $cases) {
            $x = Invoke-Cli (@('cliext.animator', 'drive', '--object', 'Player') + $c[0] + @('--format', 'json'))
            Expect-AnimFailure $x $c[1] '' '' $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        $nobody = Invoke-Cli @('cliext.animator', 'live', '--object', 'NoSuchObject', '--format', 'json')
        Expect-AnimFailure $nobody "No GameObject 'NoSuchObject'.*scene get_hierarchy" '' '' 'unknown object' 13
        $later = Invoke-Cli @('cliext.animator', 'live', '--object', 'Player', '--format', 'json')
        $state = "$(@($later.json.layers)[0].state)"
        Expect ($state -eq 'Base Layer.Locomotion') "after drive --state the layer is in '$state'"
        $ed = Invoke-Cli @('cliext.animator', 'edit', '--path', $script:animCtl, '--ops-file', (Write-AnimOps 'anim-play-edit.json' '[{"op":"set_state","state":"Run","set":"tag=Played"}]'), '--format', 'json')
        Expect ($ed.exit -eq 0) "edit in Play mode: exit $($ed.exit): $(Short ($ed.stdout + $ed.stderr))"
    }
    finally {
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
        # Back to the scene that was open, from disk: nothing of the run is kept in it.
        $back = Invoke-Cli @('script', 'eval', '--file', (& $openCs $(if ($open.path) { $open.path } else { $level })), '--format', 'json')
    }
    Expect ("$($back.stdout)" -match '\|False') "the scene is dirty after the run: $(Short ($back.stdout + $back.stderr))"
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'anim-mode.cs' 'return UnityEditor.EditorApplication.isPlaying ? "playing" : "edit";'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit') "still in Play mode: $(Short $mode.stdout)"
    $run = @((Get-AnimController).layers[0].states) | Where-Object { $_.path -eq 'Run' }
    Expect ($run.tag -eq 'Played') "the Play mode edit didn't stay: Run's tag is '$($run.tag)'"
    "Edit mode refused (6); live read Speed 0.8 and Grounded; drive --state reached Locomotion; $($seen -join ', '); edit kept; scene clean"
}

# --- cliext.timeline --------------------------------------------------------------------------------------------------

# A scratch .playable (Anim with Idle and Run from the TestLevel models, Audio with bookClose, Group/Sub, Act) and a
# SignalAsset, made once by Need-TlAssets. The committed clips and sound are only referenced. Directors live in an
# untitled additive scene that Close-TlScene discards, or are made in Play mode, so no saved scene changes.
$script:tlPath = "$scratchRel/CliTestTimeline.playable"
$script:tlSignal = "$scratchRel/CliTestSignal.signal"
$script:tlReady = $false
$tlRow = 'cliext.timeline'

function Need-TlAssets {
    if ($script:tlReady) { return }
    $code = @'
string dir = "{{dir}}";
if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests")) UnityEditor.AssetDatabase.CreateFolder("Assets", "_CliextTests");
if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests", "CliTest");
UnityEditor.AssetDatabase.DeleteAsset("{{path}}"); UnityEditor.AssetDatabase.DeleteAsset("{{signal}}");
var t = UnityEngine.ScriptableObject.CreateInstance<UnityEngine.Timeline.TimelineAsset>();
UnityEditor.AssetDatabase.CreateAsset(t, "{{path}}");
UnityEditor.AssetDatabase.CreateAsset(UnityEngine.ScriptableObject.CreateInstance<UnityEngine.Timeline.SignalAsset>(), "{{signal}}");
System.Func<string, UnityEngine.AnimationClip> take = p => UnityEditor.AssetDatabase.LoadAllAssetsAtPath(p).OfType<UnityEngine.AnimationClip>().First(x => !x.name.StartsWith("__preview__"));
var anim = t.CreateTrack<UnityEngine.Timeline.AnimationTrack>(null, "Anim");
var idle = anim.CreateClip(take("Assets/TestLevel/Animations/kenney idle.fbx")); idle.start = 0; idle.duration = 1; idle.displayName = "Idle";
var run = anim.CreateClip(take("Assets/TestLevel/Animations/kenney run.fbx")); run.start = 1; run.duration = 1; run.displayName = "Run";
var audio = t.CreateTrack<UnityEngine.Timeline.AudioTrack>(null, "Audio");
var book = audio.CreateClip(UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AudioClip>("Assets/TestLevel/Audio/bookClose.ogg")); book.start = 0.5; book.displayName = "Book";
var grp = t.CreateTrack<UnityEngine.Timeline.GroupTrack>(null, "Group");
t.CreateTrack<UnityEngine.Timeline.AnimationTrack>(grp, "Sub");
var act = t.CreateTrack<UnityEngine.Timeline.ActivationTrack>(null, "Act");
var on = act.CreateDefaultClip(); on.start = 0; on.duration = 2;
UnityEditor.EditorUtility.SetDirty(t);
UnityEditor.AssetDatabase.SaveAssets();
return "ok";
'@
    $code = $code.Replace('{{dir}}', $scratchRel).Replace('{{path}}', $script:tlPath).Replace('{{signal}}', $script:tlSignal)
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-setup.cs' $code), '--timeout', '120', '--format', 'json') 180
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok') "scratch timeline: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $script:tlReady = $true
}
function Get-TlHash { (Get-FileHash (Join-Path $projectPath ($script:tlPath -replace '/', '\'))).Hash }
function Get-TlInspect([string[]]$extra = @('--path', $script:tlPath)) {
    $r = Invoke-Cli (@('cliext.timeline', 'inspect') + $extra + @('--format', 'json'))
    Expect ($r.exit -eq 0) "inspect: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.json
}
function Get-TlTrack($tl, [string]$path) { @($tl.tracks) | Where-Object { $_.path -eq $path } | Select-Object -First 1 }
function Get-TlMemory {
    # The timeline as the Editor holds it in memory (not reloaded from disk): tracks with mute/lock, clips with timing,
    # markers and child tracks, the timeline's own markers, and how many objects the .playable holds.
    $code = @'
var ci = System.Globalization.CultureInfo.InvariantCulture;
var t = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>("{{path}}");
System.Func<UnityEngine.Timeline.TrackAsset, string> d = null;
d = tr => tr.name + (tr.muted ? "!m" : "") + (tr.locked ? "!l" : "")
    + "[" + string.Join(",", tr.GetClips().Select(c => c.displayName + "@" + c.start.ToString(ci) + "+" + c.duration.ToString("0.####", ci) + (c.asset == null ? "!noasset" : ""))) + "]"
    + "{" + string.Join(",", tr.GetMarkers().Select(m => m.time.ToString(ci))) + "}"
    + "(" + string.Join(",", tr.GetChildTracks().Select(c => d(c))) + ")";
return string.Join(";", t.GetRootTracks().Where(r => r != t.markerTrack).Select(r => d(r)))
    + "|markers{" + (t.markerTrack == null ? "" : string.Join(",", t.markerTrack.GetMarkers().Select(m => m.time.ToString(ci)))) + "}"
    + "|objects " + UnityEditor.AssetDatabase.LoadAllAssetsAtPath("{{path}}").Length;
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-mem.cs' $code.Replace('{{path}}', $script:tlPath)), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}
function Expect-TlFailure($r, [string]$hint, [string]$before, [string]$what, [int]$exit = 6) {
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ($r.exit -eq $exit) "${what}: exit $($r.exit), expected ${exit}: $(Short ($r.stdout + $r.stderr))"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    Expect ("$($r.stderr)" -notmatch '   at |Exception:') "${what}: a stack trace instead of a message: $(Short $r.stderr)"
    if ($before) { Expect ((Get-TlHash) -eq $before) "${what}: the .playable changed on disk" }
}
function Open-TlScene {
    # An untitled additive scene with a director playing the scratch timeline and objects to bind; never saved.
    $code = @'
var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
System.Func<string, UnityEngine.GameObject> make = n => { var g = new UnityEngine.GameObject(n); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(g, scene); return g; };
var d = make("CliTestDirector").AddComponent<UnityEngine.Playables.PlayableDirector>();
d.playableAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>("{{path}}"); d.playOnAwake = false;
make("CliTestActor").AddComponent<UnityEngine.Animator>();
make("CliTestSpeaker").AddComponent<UnityEngine.AudioSource>();
var rig = new UnityEngine.GameObject("CliTestRig"); rig.transform.SetParent(make("CliTestPlain").transform); rig.AddComponent<UnityEngine.Animator>();
make("CliTestNoTimeline").AddComponent<UnityEngine.Playables.PlayableDirector>();
return "ok " + UnityEngine.SceneManagement.SceneManager.sceneCount;
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-scene.cs' $code.Replace('{{path}}', $script:tlPath)), '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok') "scratch scene: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
}
function Close-TlScene {
    # Discards every untitled scene (unsaved) and returns the active scene's path and dirty flag.
    $code = @'
for (int i = UnityEngine.SceneManagement.SceneManager.sceneCount - 1; i >= 0; i--)
{
    var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
    if (string.IsNullOrEmpty(s.path) && UnityEngine.SceneManagement.SceneManager.sceneCount > 1) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s, true);
}
var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
return a.path + "|" + a.isDirty + "|" + UnityEngine.SceneManagement.SceneManager.sceneCount;
'@
    (Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-close.cs' $code), '--format', 'json')).stdout
}
function Get-TlDirectorTime([string]$name) {
    $code = "var d = UnityEngine.Object.FindObjectsByType<UnityEngine.Playables.PlayableDirector>(UnityEngine.FindObjectsInactive.Include).First(x => x.name == `"$name`"); return d.time.ToString(System.Globalization.CultureInfo.InvariantCulture) + `"|`" + d.state;"
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-dtime.cs' $code), '--format', 'json')
    Expect ($r.exit -eq 0) "reading $name's time: $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}

Add-Check 'tl.inspect' $tlRow 'cliext.timeline inspect describes every track by path with its clips; a missing .playable exits 13 naming animation.timeline create, another asset type, a path outside Assets/ and no path exit 2' {
    Need-TlAssets
    $tl = Get-TlInspect
    Expect ((@($tl.tracks | ForEach-Object { $_.path }) -join ',') -eq 'Anim,Audio,Group,Group/Sub,Act') "tracks: $(@($tl.tracks | ForEach-Object { $_.path }) -join ',')"
    $anim = Get-TlTrack $tl 'Anim'
    Expect ($anim.type -eq 'AnimationTrack' -and (@($anim.clips | ForEach-Object { "$($_.name)@$([double]$_.start)" }) -join ',') -eq 'Idle@0,Run@1') "Anim: $(Short ($anim | ConvertTo-Json -Compress -Depth 4))"
    Expect ((Get-TlTrack $tl 'Audio').clips[0].name -eq 'Book' -and $tl.duration -gt 1.9) "Audio clip or duration: $(Short ($tl | ConvertTo-Json -Compress -Depth 4))"
    $cases = @(
        @(@('--path', "$scratchRel/NoSuch.playable"), 'No TimelineAsset at.*animation\.timeline create --path', 13, 'missing .playable'),
        @(@('--path', 'Assets/TestLevel/Audio/bookClose.ogg'), 'is an AudioClip, not a TimelineAsset', 2, 'an AudioClip as the timeline'),
        @(@('--path', 'NotAssets/CliTest.playable'), "isn't under Assets/ or Packages/", 2, 'path outside Assets'),
        @(@(), 'Pass --path.*or --director', 2, 'no path or director'),
        @(@('--director', 'NoSuchDirector'), "No PlayableDirector on 'NoSuchDirector'", 13, 'unknown director')
    )
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli (@('cliext.timeline', 'inspect') + $c[0] + @('--format', 'json'))
        Expect-TlFailure $x $c[1] '' $c[3] $c[2]
        $seen += "$($c[3]) $($x.exit)"
    }
    "5 tracks, Anim Idle@0 Run@1; $($seen -join ', ')"
}

Add-Check 'tl.edit' $tlRow 'cliext.timeline edit sets clip timing and names, moves a clip into a group''s track, adds an audio clip, renames and mutes tracks and adds markers in one call; inspect and the timeline in memory show them all' {
    Need-TlAssets
    $ops = @"
[{"op":"set_clip","track":"Anim","clip":"Idle","set":"duration=0.8;easeOut=0.2","name":"IdleIn"},
 {"op":"move_clip","track":"Anim","clip":"Run","to":"Group/Sub"},
 {"op":"add_clip","track":"Audio","clip":"Assets/TestLevel/Audio/bookClose.ogg","time":1.5,"name":"Book2"},
 {"op":"rename_track","track":"Act","name":"Active"},
 {"op":"mute_track","track":"Audio","value":"true"},
 {"op":"add_marker","time":0.25,"signal":"$($script:tlSignal)"},
 {"op":"add_marker","track":"Anim","time":0.5}]
"@
    $r = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile 'tl-edit.json' $ops), '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 7) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $tl = Get-TlInspect
    Expect ((@($tl.tracks | ForEach-Object { $_.path }) -join ',') -eq 'Anim,Audio,Group,Group/Sub,Active') "tracks: $(@($tl.tracks | ForEach-Object { $_.path }) -join ',')"
    $idle = @((Get-TlTrack $tl 'Anim').clips)
    Expect ($idle.Count -eq 1 -and $idle[0].name -eq 'IdleIn' -and $idle[0].duration -eq 0.8 -and $idle[0].easeOut -eq 0.2) "Anim clips: $(Short ($idle | ConvertTo-Json -Compress))"
    $sub = @((Get-TlTrack $tl 'Group/Sub').clips)
    Expect ($sub.Count -eq 1 -and $sub[0].name -eq 'Run' -and $sub[0].start -eq 1) "Group/Sub clips: $(Short ($sub | ConvertTo-Json -Compress))"
    $audio = Get-TlTrack $tl 'Audio'
    Expect ($audio.muted -eq $true -and (@($audio.clips | ForEach-Object { "$($_.name)@$([double]$_.start)" }) -join ',') -eq 'Book@0.5,Book2@1.5') "Audio: $(Short ($audio | ConvertTo-Json -Compress -Depth 4))"
    Expect (@($tl.markers).Count -eq 1 -and $tl.markers[0].time -eq 0.25 -and $tl.markers[0].signal -eq $script:tlSignal) "timeline markers: $(Short ($tl.markers | ConvertTo-Json -Compress))"
    Expect ((@((Get-TlTrack $tl 'Anim').markers | ForEach-Object { [double]$_.time }) -join ',') -eq '0.5') 'Anim has no marker at 0.5'
    $m = Get-TlMemory
    Expect ($m -match '^Anim\[IdleIn@0\+0\.8\]\{0\.5\}\(\);Audio!m\[Book@0\.5\+[\d.]+,Book2@1\.5\+[\d.]+\]\{\}\(\);Group\[\]\{\}\(Sub\[Run@1\+1\]\{\}\(\)\);Active\[Active@0\+2\]\{\}\(\)\|markers\{0\.25\}') "in memory: $m"
    Expect ($m -notmatch 'noasset') "a clip lost its asset: $m"
    "7 ops in one call; $m"
}

Add-Check 'tl.edit-errors' $tlRow 'cliext.timeline edit fails (exit 6) with the fix on unknown tracks, clips, ops and settings, bad numbers and bools, clips a track can''t hold, missing clip and signal assets, markers, and ops that would do nothing, saving nothing' {
    Need-TlAssets
    $before = Get-TlHash
    $cases = @(
        @('[{"op":"remove_track","track":"Anm"}]', 'Did you mean: Anim', 'unknown track, close match'),
        @('[{"op":"remove_track","track":"Zzqx"}]', 'Tracks: .*cliext\.timeline inspect', 'unknown track, no match'),
        @('[{"op":"remove_track"}]', 'Name a track', 'no track'),
        @('[{"op":"remove_clip","track":"Anim","clip":"IdleI"}]', 'Did you mean: IdleIn', 'unknown clip, close match'),
        @('[{"op":"remove_clip","track":"Anim","clip":"7"}]', 'No clip 7 on Anim.*Clips: IdleIn', 'clip index out of range'),
        @('[{"op":"set_clip","track":"Anim","clip":"IdleIn"}]', 'set_clip needs set.*change nothing', 'set_clip that changes nothing'),
        @('[{"op":"set_clip","track":"Anim","clip":"IdleIn","set":"duration=-1"}]', 'duration must be more than 0', 'negative duration'),
        @('[{"op":"set_clip","track":"Anim","clip":"IdleIn","set":"start=abc"}]', "'abc' isn't a Double value: give a number", 'non-numeric start'),
        @('[{"op":"set_clip","track":"Anim","clip":"IdleIn","set":"durtion=2"}]', "Unknown clip setting 'durtion'. Did you mean: duration", 'misspelled setting'),
        @('[{"op":"mute_track","track":"Audio","value":"true"}]', 'already muted', 'mute a muted track'),
        @('[{"op":"lock_track","track":"Anim","value":"maybe"}]', 'true or false', 'non-boolean value'),
        @('[{"op":"rename_track","track":"Anim"}]', 'rename_track needs a name', 'rename without a name'),
        @('[{"op":"move_clip","track":"Anim","clip":"IdleIn","to":"Audio"}]', "Audio \(AudioTrack\) can't hold IdleIn", 'move to a track of another kind'),
        @('[{"op":"move_clip","track":"Anim","clip":"IdleIn","to":"Anim"}]', 'already on Anim', 'move to the same track'),
        @('[{"op":"add_clip","track":"Group"}]', 'GroupTrack: it holds tracks, not clips', 'clip on a group'),
        @('[{"op":"add_clip","track":"Anim","clip":"Assets/_CliextTests/CliTest/NoSuch.anim"}]', 'nothing is there.*cliext\.anim takes', 'missing clip asset'),
        @('[{"op":"add_clip","track":"Audio","clip":"Assets/TestLevel/Animations/kenney idle.fbx"}]', 'with no AudioClip', 'a model on an audio track'),
        @('[{"op":"add_marker"}]', 'add_marker needs a time', 'marker without a time'),
        @('[{"op":"add_marker","time":1,"signal":"Assets/_CliextTests/CliTest/NoSuch.signal"}]', 'No SignalAsset at.*Create one', 'missing signal'),
        @('[{"op":"add_marker","time":0.25}]', 'already has a marker at 0.25s', 'second marker at the same time'),
        @('[{"op":"remove_marker","time":3}]', 'No marker at 3s.*at: 0\.25s', 'remove a marker that isn''t there'),
        @('[{"op":"add_clips","track":"Anim"}]', 'Did you mean: add_clip', 'misspelled op'),
        @('[{"op":"frob"}]', 'Unknown op .frob.*Ops: add_clip, set_clip', 'unknown op')
    )
    $seen = @()
    $i = 0
    foreach ($c in $cases) {
        $i++
        $r = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile "tl-fail-$i.json" $c[0]), '--format', 'json')
        Expect-TlFailure $r $c[1] $before $c[2]
        Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 0) "$($c[2]): reply $(Short $r.stdout)"
        $seen += $c[2]
    }
    $json = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile 'tl-badjson.json' '[{"op":'), '--format', 'json')
    Expect-TlFailure $json "aren't valid JSON" $before 'broken JSON' 2
    "$($seen.Count) failures exit 6 with the fix, broken JSON 2, file unchanged"
}

Add-Check 'tl.edit-rollback' $tlRow 'a timeline edit batch with removed tracks and clips, a new clip, a marker and a rename, then one bad op, saves nothing, on disk or in memory, and the next good edit saves only its own change' {
    Need-TlAssets
    $before = Get-TlHash
    $m0 = Get-TlMemory
    $ops = '[{"op":"remove_track","track":"Group"},{"op":"remove_clip","track":"Audio","clip":"Book"},{"op":"add_clip","track":"Anim","clip":"Assets/TestLevel/Animations/kenney run.fbx@kenney run 2","time":2,"name":"Extra"},{"op":"add_marker","time":1.25},{"op":"rename_track","track":"Anim","name":"Renamed"},{"op":"set_clip","track":"Active","clip":"0","set":"duration=5"},{"op":"remove_track","track":"NoSuchTrack"}]'
    $r = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile 'tl-bad-batch.json' $ops), '--format', 'json')
    Expect-TlFailure $r 'Op 6 \(remove_track\) failed.*Nothing was saved' $before 'bad batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 6 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
    $m1 = Get-TlMemory
    Expect ($m1 -eq $m0) "in memory: '$m0' -> '$m1'"
    $good = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile 'tl-good.json' '[{"op":"lock_track","track":"Anim","value":"true"}]'), '--format', 'json')
    Expect ($good.exit -eq 0) "good edit: exit $($good.exit): $(Short ($good.stdout + $good.stderr))"
    $m2 = Get-TlMemory
    $want = $m0 -replace '^Anim\[', 'Anim!l['
    Expect ($m2 -eq $want) "after the good edit: expected '$want', got '$m2'"
    $text = Get-Content (Join-Path $projectPath ($script:tlPath -replace '/', '\')) -Raw
    foreach ($n in 'Group', 'Sub', 'Anim') { Expect ($text -match "m_Name: $n\r?\n") "the file on disk lost the $n track" }
    Expect ($text -notmatch 'm_Name: Renamed|m_DisplayName: Extra') 'the file on disk has the failed batch''s rename or clip'
    $tl = Get-TlInspect
    Expect ((Get-TlTrack $tl 'Anim').locked -eq $true -and @($tl.markers).Count -eq 1 -and (Get-TlTrack $tl 'Active').clips[0].duration -eq 2) "inspect after the good edit: $(Short ($tl | ConvertTo-Json -Compress -Depth 4))"
    "failed at op 6, file and memory unchanged ($m1); next edit saved only Anim's lock"
}

Add-Check 'tl.missing-asset' $tlRow 'cliext.timeline edit given a .playable that does not exist exits 13 at once naming animation.timeline create; another asset type and a path outside Assets/ exit 2; nothing is created' {
    Need-TlAssets
    $f = Write-WorkFile 'tl-one.json' '[{"op":"mute_track","track":"Anim"}]'
    $cases = @(
        @("$scratchRel/NoSuch.playable", 'animation\.timeline create --path', 13),
        @($script:tlSignal, 'is a SignalAsset, not a TimelineAsset', 2),
        @('NotAssets/CliTest.playable', "isn't under Assets/ or Packages/", 2)
    )
    $bad = @()
    foreach ($c in $cases) {
        $r = Invoke-Cli @('cliext.timeline', 'edit', '--path', $c[0], '--ops-file', $f, '--format', 'json') 60
        if ($r.exit -ne $c[2] -or "$($r.stderr)" -notmatch $c[1] -or $r.ms -gt 30000) { $bad += "$($c[0]): exit $($r.exit) in $($r.ms) ms ($(Short $r.stderr 80))" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    Expect (-not (Test-Path (Join-Path $scratch 'NoSuch.playable')) -and -not (Test-Path (Join-Path $projectPath 'NotAssets'))) 'a failed call created a file'
    'missing 13 naming animation.timeline create; a SignalAsset 2; outside Assets 2'
}

Add-Check 'tl.bind-director' $tlRow 'in an unsaved scratch scene, cliext.timeline bind binds tracks to an Animator and an AudioSource and director scrubs; wrong-type targets, group tracks, unknown names, bad times and actions, and play in Edit mode fail with the fix and change nothing' {
    Need-TlAssets
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $before = Get-TlHash
    Open-TlScene
    try {
        $a = Invoke-Cli @('cliext.timeline', 'bind', '--director', 'CliTestDirector', '--track', 'Anim', '--object', 'CliTestActor', '--format', 'json')
        Expect ($a.exit -eq 0 -and $a.json.boundTo -eq 'CliTestActor (Animator)') "bind Anim: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        $s = Invoke-Cli @('cliext.timeline', 'bind', '--director', 'CliTestDirector', '--track', 'Audio', '--object', 'CliTestSpeaker', '--format', 'json')
        Expect ($s.exit -eq 0) "bind Audio: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $tl = Get-TlInspect @('--director', 'CliTestDirector')
        Expect ($tl.path -eq $script:tlPath -and (Get-TlTrack $tl 'Anim').boundTo -eq 'CliTestActor (Animator)' -and (Get-TlTrack $tl 'Audio').boundTo -eq 'CliTestSpeaker (AudioSource)') "inspect --director: $(Short ($tl.tracks | Select-Object path, boundTo | ConvertTo-Json -Compress))"
        $cases = @(
            @(@('bind', '--director', 'CliTestDirector', '--track', 'Anim', '--object', 'CliTestPlain'), "CliTestPlain has no Animator.*--object CliTestRig", 2, 'bind to an object without an Animator'),
            @(@('bind', '--director', 'CliTestDirector', '--track', 'Group', '--object', 'CliTestActor'), 'GroupTrack, which has no binding', 2, 'bind a group track'),
            @(@('bind', '--director', 'CliTestDirector', '--track', 'Anm', '--object', 'CliTestActor'), 'Did you mean: Anim', 13, 'misspelled track'),
            @(@('bind', '--director', 'CliTestDirector', '--track', 'Anim', '--object', 'NoSuchObject'), "No GameObject 'NoSuchObject'.*scene get_hierarchy", 13, 'unknown object'),
            @(@('bind', '--director', 'CliTestDirectr', '--track', 'Anim', '--object', 'CliTestActor'), 'Did you mean: CliTestDirector', 13, 'misspelled director'),
            @(@('bind', '--director', 'CliTestActor', '--track', 'Anim', '--object', 'CliTestActor'), "'CliTestActor' has no PlayableDirector", 13, 'an object without a director'),
            @(@('bind', '--director', 'CliTestNoTimeline', '--track', 'Anim', '--object', 'CliTestActor'), "doesn't play a timeline.*Playable Asset", 6, 'a director with no timeline'),
            @(@('director', '--director', 'CliTestDirector', '--action', 'play'), 'enter Play mode first \(editor\.playmode start\)', 6, 'play in Edit mode'),
            @(@('director', '--director', 'CliTestDirector', '--action', 'jump'), 'Use one of: play, pause, resume, stop', 2, 'unknown action'),
            @(@('director', '--director', 'CliTestDirector', '--time', '-5'), '--time must be 0 or more', 2, 'negative time'),
            @(@('director', '--director', 'CliTestDirector', '--time', '99'), 'past the end of CliTestTimeline', 2, 'time past the end'),
            @(@('director', '--director', 'NoSuchDirector'), "No PlayableDirector on 'NoSuchDirector'", 13, 'unknown director'),
            @(@('director', '--director', 'CliTestNoTimeline', '--time', '1'), 'no Playable Asset to scrub', 6, 'scrub a director with no timeline')
        )
        $seen = @()
        foreach ($c in $cases) {
            $x = Invoke-Cli (@('cliext.timeline') + $c[0] + @('--format', 'json'))
            Expect-TlFailure $x $c[1] $before $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        $tl2 = Get-TlInspect @('--director', 'CliTestDirector')
        Expect ((Get-TlTrack $tl2 'Anim').boundTo -eq 'CliTestActor (Animator)' -and -not (Get-TlTrack $tl2 'Group').boundTo) "a failed bind changed the bindings: $(Short ($tl2.tracks | Select-Object path, boundTo | ConvertTo-Json -Compress))"
        $d = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestDirector', '--time', '0.75', '--format', 'json')
        Expect ($d.exit -eq 0 -and $d.json.time -eq 0.75 -and $d.json.timeline -eq $script:tlPath) "scrub: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        $t = Get-TlDirectorTime 'CliTestDirector'
        Expect ($t -match '^0\.75\|') "the director's time after the scrub: $t"
        $read = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestDirector', '--format', 'json')
        Expect ($read.exit -eq 0 -and $read.json.time -eq 0.75) "read: exit $($read.exit): $(Short ($read.stdout + $read.stderr))"
        $none = Invoke-Cli @('cliext.timeline', 'bind', '--director', 'CliTestDirector', '--track', 'Audio', '--object', 'none', '--format', 'json')
        Expect ($none.exit -eq 0 -and -not (Get-TlTrack (Get-TlInspect @('--director', 'CliTestDirector')) 'Audio').boundTo) "bind none: exit $($none.exit): $(Short ($none.stdout + $none.stderr))"
        Expect ((Get-TlHash) -eq $before) 'binding or scrubbing changed the .playable'
    }
    finally { $closed = Close-TlScene }
    Expect ("$closed" -match '^"?Assets/Scenes/[^|]+\|False\|1') "after closing the scratch scene: $(Short $closed)"
    "Anim -> Animator, Audio -> AudioSource; scrubbed to 0.75; $($seen -join ', '); scratch scene discarded"
}

Add-Check 'tl.play-mode' $tlRow 'in Play mode, cliext.timeline director plays a director made there, its time advances, pause and stop work; leaving Play mode leaves the scene clean' {
    Need-TlAssets
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $make = Write-WorkFile 'tl-play-make.cs' ("var d = new UnityEngine.GameObject(`"CliTestPlayDirector`").AddComponent<UnityEngine.Playables.PlayableDirector>(); d.playOnAwake = false; d.extrapolationMode = UnityEngine.Playables.DirectorWrapMode.Hold; d.playableAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>(`"$($script:tlPath)`"); return `"ok`";")
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $mk = Invoke-Cli @('script', 'eval', '--file', $make, '--format', 'json')
        Expect ($mk.exit -eq 0 -and "$($mk.stdout)" -match 'ok') "making the director: $(Short ($mk.stdout + $mk.stderr))"
        $p = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestPlayDirector', '--action', 'play', '--format', 'json')
        Expect ($p.exit -eq 0 -and $p.json.state -eq 'Playing') "play: exit $($p.exit): $(Short ($p.stdout + $p.stderr))"
        $t = Get-TlDirectorTime 'CliTestPlayDirector'
        $later = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestPlayDirector', '--format', 'json')
        Expect ($later.exit -eq 0 -and $later.json.time -gt 0 -and $later.json.state -eq 'Playing') "the time didn't advance: $t, then $(Short $later.stdout)"
        $pa = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestPlayDirector', '--action', 'pause', '--time', '0.5', '--format', 'json')
        Expect ($pa.exit -eq 0 -and $pa.json.state -eq 'Paused' -and $pa.json.time -eq 0.5) "pause at 0.5: exit $($pa.exit): $(Short ($pa.stdout + $pa.stderr))"
        $st = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestPlayDirector', '--action', 'stop', '--format', 'json')
        Expect ($st.exit -eq 0 -and $st.json.time -eq 0) "stop: exit $($st.exit): $(Short ($st.stdout + $st.stderr))"
    }
    finally {
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
    }
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-mode.cs' 'var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return (UnityEditor.EditorApplication.isPlaying ? "playing" : "edit") + "|" + a.isDirty;'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit\|False') "after Play mode: $(Short $mode.stdout)"
    "played (time $($later.json.time)s), paused at 0.5, stopped; back in Edit mode, scene clean"
}

Add-Check 'tl.scrub-restore' $tlRow 'in a saved scratch scene with a humanoid rig, cliext.timeline director --time in Edit mode names the active clip and its local time, --read returns the scrubbed bone pose, and the rig and the scene are left as saved; --keep-pose true leaves the frame and marks the scene dirty' {
    Need-TlAssets
    $scenePath = "$scratchRel/CliTestScrub.unity"
    # A rig of the TestLevel character bound to Anim, in a scene saved to disk and reopened additively, so it starts clean.
    $setup = @'
var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
var model = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/TestLevel/Characters/characterMedium.fbx");
var rig = (UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(model, scene); rig.name = "CliTestScrubRig";
var dgo = new UnityEngine.GameObject("CliTestScrubDirector"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(dgo, scene);
var d = dgo.AddComponent<UnityEngine.Playables.PlayableDirector>(); d.playOnAwake = false;
var t = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>("{{path}}"); d.playableAsset = t;
d.SetGenericBinding(t.GetRootTracks().First(x => x.name == "Anim"), rig.GetComponent<UnityEngine.Animator>());
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "{{scene}}");
UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
var again = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("{{scene}}", UnityEditor.SceneManagement.OpenSceneMode.Additive);
return "ok|" + again.isDirty;
'@
    $poseCode = @'
var s = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("{{scene}}");
var arm = UnityEngine.GameObject.Find("CliTestScrubRig").GetComponentsInChildren<UnityEngine.Transform>().First(x => x.name == "LeftForeArm");
var r = arm.localEulerAngles;
return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}|{3}", r.x, r.y, r.z, s.isDirty);
'@
    $pose = Write-WorkFile 'tl-scrub-pose.cs' $poseCode.Replace('{{scene}}', $scenePath)
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-scrub-setup.cs' $setup.Replace('{{path}}', $script:tlPath).Replace('{{scene}}', $scenePath)), '--format', 'json')
    try {
        Expect ("$($r.stdout)" -match 'ok\|False') "scratch scene: $(Short ($r.stdout + $r.stderr))"
        $find = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-scrub-path.cs' 'var t = UnityEngine.GameObject.Find("CliTestScrubRig").GetComponentsInChildren<UnityEngine.Transform>().First(x => x.name == "LeftForeArm"); var p = t.name; for (var q = t.parent; q != null; q = q.parent) p = q.name + "/" + p; return p;'), '--format', 'json')
        $armPath = $find.stdout.Trim().Trim('"')
        $p0 = (Invoke-Cli @('script', 'eval', '--file', $pose, '--format', 'json')).stdout.Trim().Trim('"')
        $d = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestScrubDirector', '--time', '0.5', '--read', $armPath, '--format', 'json')
        Expect ($d.exit -eq 0) "scrub: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        $anim = @($d.json.tracks | Where-Object { $_.track -eq 'Anim' })[0]
        Expect ($anim.clips[0].clip -and [math]::Abs($anim.clips[0].localTime - 0.5) -lt 1e-3) "tracks: $(Short ($d.json.tracks | ConvertTo-Json -Compress -Depth 5))"
        Expect ("$($d.json.scene)" -match '^unchanged') "scene note: $($d.json.scene)"
        $read = $d.json.read[0].localRotation
        $readText = (@($read | ForEach-Object { [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0}', [double]$_) }) -join ',')
        $p1 = (Invoke-Cli @('script', 'eval', '--file', $pose, '--format', 'json')).stdout.Trim().Trim('"')
        Expect ($p1 -eq $p0) "the rig after the scrub: $p1, before: $p0"
        Expect ($p1 -match '\|False$') "the scene is dirty after the scrub: $p1"
        Expect (($p0 -split '\|')[0] -ne $readText) "--read gave the saved pose ($readText), not the scrubbed one"
        $k = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestScrubDirector', '--time', '0.5', '--keep-pose', 'true', '--format', 'json')
        Expect ($k.exit -eq 0 -and "$($k.json.scene)" -match '^kept') "keep-pose: exit $($k.exit): $(Short ($k.stdout + $k.stderr))"
        $p2 = (Invoke-Cli @('script', 'eval', '--file', $pose, '--format', 'json')).stdout.Trim().Trim('"')
        Expect ($p2 -match '\|True$' -and ($p2 -split '\|')[0] -ne ($p0 -split '\|')[0]) "after --keep-pose: $p2 (saved $p0)"
        $usage = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestScrubDirector', '--read', $armPath, '--format', 'json')
        Expect-TlFailure $usage '--read reports the pose at --time' '' '--read without --time' 2
        $missing = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestScrubDirector', '--time', '0.5', '--read', 'NoSuchBone', '--format', 'json')
        Expect-TlFailure $missing "No GameObject 'NoSuchBone'" '' 'unknown --read object' 13
        # The same bone as a rooted path and as a path relative to the bound rig reads the same pose; a wrong path names where the bone is.
        $relPath = $armPath.Substring($armPath.IndexOf('/') + 1)
        foreach ($form in @("/$armPath", $relPath, "/$relPath")) {
            $f = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestScrubDirector', '--time', '0.5', '--read', $form, '--format', 'json')
            Expect ($f.exit -eq 0) "--read $form`: exit $($f.exit): $(Short ($f.stdout + $f.stderr))"
            $ft = (@($f.json.read[0].localRotation | ForEach-Object { [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0}', [double]$_) }) -join ',')
            Expect ($ft -eq $readText) "--read $form gave $ft, the full path $readText"
        }
        $wrong = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestScrubDirector', '--time', '0.5', '--read', 'CliTestScrubRig/NoSuchModel/LeftForeArm', '--format', 'json')
        Expect-TlFailure $wrong "'LeftForeArm' is at (.*; )?$([regex]::Escape($armPath)) \(under CliTestScrubRig: $([regex]::Escape($relPath))\).*relative to a bound object" '' 'a wrong --read path' 13
    }
    finally {
        $close = "var s = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(`"$scenePath`"); if (s.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s, true); UnityEditor.AssetDatabase.DeleteAsset(`"$scenePath`"); return `"ok`";"
        [void](Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-scrub-close.cs' $close), '--format', 'json'))
    }
    "Anim plays $($anim.clips[0].clip) at local 0.5; read $readText while sampled; rig back at $p0; keep-pose left the frame and dirtied the scene"
}

Add-Check 'tl.pose-proof' $tlRow 'the animation skill''s pose proof: pose.cs mode=bones lists the rig''s bones, director --read of them saved to a file, and pose.cs mode=compare puts the active clip under 1 degree and the other clip well away; the scene stays as saved (no --keep-pose, nothing restored)' {
    Need-TlAssets
    $scenePath = "$scratchRel/CliTestPose.unity"
    $setup = @'
var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
var model = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/TestLevel/Characters/characterMedium.fbx");
var rig = (UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(model, scene); rig.name = "CliTestPoseRig";
var dgo = new UnityEngine.GameObject("CliTestPoseDirector"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(dgo, scene);
var d = dgo.AddComponent<UnityEngine.Playables.PlayableDirector>(); d.playOnAwake = false;
var t = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>("{{path}}"); d.playableAsset = t;
d.SetGenericBinding(t.GetRootTracks().First(x => x.name == "Anim"), rig.GetComponent<UnityEngine.Animator>());
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "{{scene}}");
return "ok|" + scene.isDirty;
'@
    $pose = Join-Path $packageRoot 'Skills~\unity-cliext-animation\ops\pose.cs'
    $scrub = Join-Path $work 'tl-pose-scrub.json'
    try {
        Expect ((Invoke-Eval 'tl-pose-setup.cs' $setup.Replace('{{path}}', $script:tlPath).Replace('{{scene}}', $scenePath)) -eq 'ok|False') 'scratch scene'
        $b = Invoke-Cli @('cliext.eval', 'run', '--file', $pose, '--vars', 'mode=bones;object=CliTestPoseRig;clip=-;time=0;read=-', '--format', 'json')
        $bones = $b.stdout.Trim().Trim([char]34)
        Expect ($b.exit -eq 0 -and $bones -match '^CliTestPoseRig/.+;CliTestPoseRig/') "mode=bones: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
        $d = Invoke-Cli @('cliext.timeline', 'director', '--director', 'CliTestPoseDirector', '--time', '1.5', '--read', $bones, '--format', 'json')
        Expect ($d.exit -eq 0 -and @($d.json.read).Count -eq @($bones -split ';').Count) "director --read: exit $($d.exit), $(@($d.json.read).Count) poses: $(Short $d.stderr)"
        [IO.File]::WriteAllText($scrub, $d.stdout)
        $run = @($d.json.tracks | Where-Object { $_.track -eq 'Anim' })[0].clips[0]
        function Compare-Pose([string]$model, $clip) {
            $vars = [string]::Format([Globalization.CultureInfo]::InvariantCulture, 'mode=compare;object=CliTestPoseRig;clip={0}@{1};time={2};read={3}', $model, $clip.animationClip, [double]$clip.localTime, $scrub)
            $c = Invoke-Cli @('cliext.eval', 'run', '--file', $pose, '--vars', $vars, '--format', 'json')
            Expect ($c.exit -eq 0 -and "$($c.stdout)" -match 'mean ([\d.]+) deg over (\d+) bones') "compare $($clip.clip): $(Short ($c.stdout + $c.stderr))"
            [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        }
        $own = Compare-Pose 'Assets/TestLevel/Animations/kenney run.fbx' $run
        $other = Compare-Pose 'Assets/TestLevel/Animations/standing idle.fbx' ([pscustomobject]@{ clip = 'standing idle'; animationClip = 'standing idle'; localTime = 0.5 })
        Expect ($own -lt 1 -and $other -gt $own + 2) "mean off the active clip $own, off the other $other"
        $dirty = Invoke-Eval 'tl-pose-dirty.cs' "return UnityEngine.SceneManagement.SceneManager.GetSceneByPath(`"$scenePath`").isDirty.ToString();"
        Expect ($dirty -eq 'False') 'the scene is dirty after the proof'
        "$(@($bones -split ';').Count) bones read at 1.5 s; mean $own deg off $($run.animationClip), $other off standing idle; scene clean"
    }
    finally {
        $close = "var s = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(`"$scenePath`"); if (s.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s, true); UnityEditor.AssetDatabase.DeleteAsset(`"$scenePath`"); return `"ok`";"
        [void](Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'tl-pose-close.cs' $close), '--format', 'json'))
    }
}

Add-Check 'tl.clip-refs' $tlRow'cliext.timeline edit add_clip given a take name two clips share, or a bare model with several clips, exits 2 listing them with their lengths and saves nothing' {
    Need-TlAssets
    $before = Get-TlHash
    $idle = 'Assets/TestLevel/Animations/kenney idle.fbx'
    $shared = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile 'tl-ref-shared.json' "[{`"op`":`"add_clip`",`"track`":`"Anim`",`"clip`":`"$idle@kenney idle`",`"time`":3}]"), '--format', 'json')
    Expect-TlFailure $shared "2 clips named 'kenney idle'.*1 frame.*cliext\.anim edit_clips" $before 'a take name two clips share' 2
    $bare = Invoke-Cli @('cliext.timeline', 'edit', '--path', $script:tlPath, '--ops-file', (Write-WorkFile 'tl-ref-bare.json' "[{`"op`":`"add_clip`",`"track`":`"Anim`",`"clip`":`"$idle`",`"time`":3}]"), '--format', 'json')
    Expect-TlFailure $bare 'is a model with 2 clips.*@<clip>' $before 'a bare model with 2 clips' 2
    "shared name $($shared.exit), bare model $($bare.exit); .playable unchanged"
}

# --- cliext.input -----------------------------------------------------------------------------------------------------

# Edit checks work on a scratch copy of the project's InputSystem_Actions.inputactions (AssetDatabase.CopyAsset, so it
# has its own GUID), made fresh by Reset-InAsset; the committed asset is only read, and its hash is checked each time.
# Play mode checks use the open scene unsaved, add virtual devices and hook the project-wide actions from a snippet
# (performed counts kept in SessionState, read by another snippet), and remove all of it before leaving Play mode.
$script:inPath = "$scratchRel/CliTestInput.inputactions"
$script:inSource = 'Assets/InputSystem_Actions.inputactions'
$script:inSourceHash = $null
$inRow = 'cliext.input'

function Reset-InAsset {
    $src = Join-Path $projectPath ($script:inSource -replace '/', '\')
    if (-not $script:inSourceHash) { $script:inSourceHash = (Get-FileHash $src).Hash }
    Expect ((Get-FileHash $src).Hash -eq $script:inSourceHash) "the committed $($script:inSource) changed"
    $code = @'
if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests")) UnityEditor.AssetDatabase.CreateFolder("Assets", "_CliextTests");
if (!UnityEditor.AssetDatabase.IsValidFolder("{{dir}}")) UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests", "CliTest");
UnityEditor.AssetDatabase.DeleteAsset("{{path}}");
return UnityEditor.AssetDatabase.CopyAsset("{{src}}", "{{path}}") ? "ok" : "copy failed";
'@
    $code = $code.Replace('{{dir}}', $scratchRel).Replace('{{path}}', $script:inPath).Replace('{{src}}', $script:inSource)
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-setup.cs' $code), '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok') "scratch .inputactions: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
}
function Get-InHash { (Get-FileHash (Join-Path $projectPath ($script:inPath -replace '/', '\'))).Hash }
function Get-InInspect([string]$path = $script:inPath) {
    $r = Invoke-Cli @('cliext.input', 'inspect', '--path', $path, '--format', 'json')
    Expect ($r.exit -eq 0) "inspect: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.json
}
function Get-InAction($j, [string]$map, [string]$action) { @(@($j.maps) | Where-Object { $_.name -eq $map } | ForEach-Object { $_.actions } | Where-Object { $_.name -eq $action }) | Select-Object -First 1 }
function Get-InSig($j) {
    # The same text Get-InMemory builds from the imported asset: map/action:type:interactions:binding paths, then schemes.
    $acts = foreach ($m in @($j.maps)) { foreach ($a in @($m.actions)) { "$($m.name)/$($a.name):$($a.type):$($a.interactions):" + (@($a.bindings | ForEach-Object { $_.path }) -join ',') } }
    (@($acts) -join ';') + '|' + (@($j.controlSchemes | ForEach-Object { $_.name }) -join ',')
}
function Get-InMemory {
    # The asset as the Editor holds it in memory (the imported InputActionAsset, not a fresh read of the file).
    $code = @'
var a = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>("{{path}}");
if (a == null) return "no asset";
return string.Join(";", a.actionMaps.SelectMany(m => m.actions.Select(x => m.name + "/" + x.name + ":" + x.type + ":" + x.interactions + ":" + string.Join(",", x.bindings.Select(b => b.path)))))
    + "|" + string.Join(",", a.controlSchemes.Select(s => s.name));
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-mem.cs' $code.Replace('{{path}}', $script:inPath)), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    ($r.stdout.Trim() | ConvertFrom-Json)
}
function Expect-InFailure($r, [string]$hint, [string]$before, [string]$what, [int]$exit = 6) {
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ($r.exit -eq $exit) "${what}: exit $($r.exit), expected ${exit}: $(Short ($r.stdout + $r.stderr))"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    Expect ("$($r.stderr)" -notmatch '   at |Exception:|\[0x') "${what}: a stack trace instead of a message: $(Short $r.stderr)"
    Expect (@("$($r.stderr)".Trim() -split "`r?`n").Count -eq 1) "${what}: the error isn't one line: $(Short $r.stderr)"
    if ($before) { Expect ((Get-InHash) -eq $before) "${what}: the .inputactions changed on disk" }
}

Add-Check 'input.inspect' $inRow 'cliext.input inspect lists maps, actions with bindings by index and control schemes, with no false invalid marks; a missing .inputactions exits 13 naming how to make one, another asset type and a path outside Assets/ exit 2' {
    Reset-InAsset
    $j = Get-InInspect
    Expect ((@($j.maps | ForEach-Object { $_.name }) -join ',') -eq 'Player,UI') "maps: $(@($j.maps | ForEach-Object { $_.name }) -join ',')"
    $jump = Get-InAction $j 'Player' 'Jump'
    Expect ($jump.type -eq 'Button' -and (@($jump.bindings | ForEach-Object { "$($_.index)=$($_.path)" }) -join ',') -eq '0=<Keyboard>/space,1=<Gamepad>/buttonSouth,2=<XRController>/secondaryButton') "Jump: $(Short ($jump | ConvertTo-Json -Compress -Depth 4))"
    Expect ((@($j.controlSchemes | ForEach-Object { $_.name }) -join ',') -eq 'Keyboard&Mouse,Gamepad,Touch,Joystick,XR') "schemes: $(@($j.controlSchemes | ForEach-Object { $_.name }) -join ',')"
    # */{Submit} and the other committed bindings are fine; only unknown layouts or controls are marked.
    $invalid = @($j.maps | ForEach-Object { $_.actions } | ForEach-Object { $_.bindings } | Where-Object { $_.valid -eq $false } | ForEach-Object { $_.path })
    Expect ($invalid.Count -eq 0) "bindings marked invalid in the committed asset: $($invalid -join ', ')"
    $cases = @(
        @("$scratchRel/NoSuch.inputactions", 'No \.inputactions asset at.*Create > Input Actions', 13, 'missing .inputactions'),
        @('Assets/Scenes/TestLevel.unity', 'is a SceneAsset, not an \.inputactions asset', 2, 'a scene as the asset'),
        @('ProjectSettings/ProjectVersion.txt', "isn't under Assets/ or Packages/", 2, 'path outside Assets')
    )
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli @('cliext.input', 'inspect', '--path', $c[0], '--format', 'json')
        Expect-InFailure $x $c[1] '' $c[3] $c[2]
        $seen += "$($c[3]) $($x.exit)"
    }
    "Player, UI; Jump 3 bindings; 5 schemes; none invalid; $($seen -join ', ')"
}

Add-Check 'input.edit' $inRow 'cliext.input edit runs all 13 op kinds in one call (actions, a composite, a new map, set_action, renames, bindings, schemes); inspect and the asset in memory show them all, set_action keeps the action''s id, an unknown control path saves with a Did-you-mean warning' {
    Reset-InAsset
    $j0 = Get-InInspect
    $jumpId = (Get-InAction $j0 'Player' 'Jump').id
    $order0 = @((@($j0.maps) | Where-Object { $_.name -eq 'Player' }).actions | ForEach-Object { $_.name }) -join ','
    $ops = @'
[{"op":"add_action","map":"Player","action":"Dash","type":"Button"},
 {"op":"add_binding","map":"Player","action":"Dash","path":"<Gamepad>/buttonEast","groups":"Gamepad"},
 {"op":"add_binding","map":"Player","action":"Dash","path":"<Gamepad>/buttonSouht"},
 {"op":"add_action","map":"Debug","action":"Pan","controlType":"Vector2"},
 {"op":"add_composite","map":"Debug","action":"Pan","composite":"2DVector","parts":"up=<Keyboard>/i;down=<Keyboard>/k;left=<Keyboard>/j;right=<Keyboard>/l"},
 {"op":"set_action","map":"Player","action":"Jump","interactions":"Hold"},
 {"op":"rename_action","map":"Player","action":"Crouch","name":"Duck"},
 {"op":"set_binding","map":"Player","action":"Sprint","index":0,"path":"<Keyboard>/rightShift"},
 {"op":"remove_binding","map":"Player","action":"Attack","index":0},
 {"op":"add_scheme","name":"CliPad","devices":"<Gamepad>;?<Keyboard>"},
 {"op":"rename_map","map":"Debug","name":"Dbg"},
 {"op":"add_map","map":"Empty"},
 {"op":"remove_map","map":"Empty"},
 {"op":"remove_scheme","name":"XR"},
 {"op":"remove_action","map":"UI","action":"MiddleClick"}]
'@
    $r = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile 'in-edit.json' $ops), '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 15) "exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    # Two warnings: the misspelled control, and buttonEast, which the committed asset binds to Player/Crouch.
    Expect (@($r.json.warnings).Count -eq 2 -and "$($r.json.warnings)" -match 'buttonSouht.*Did you mean: buttonSouth' -and "$($r.json.warnings)" -match 'buttonEast is also bound to Player/Crouch') "warnings: $(Short ($r.json.warnings -join ' | ') 300)"
    $j = Get-InInspect
    Expect ((@($j.maps | ForEach-Object { $_.name }) -join ',') -eq 'Player,UI,Dbg') "maps: $(@($j.maps | ForEach-Object { $_.name }) -join ',')"
    $jump = Get-InAction $j 'Player' 'Jump'
    Expect ($jump.id -eq $jumpId -and $jump.interactions -eq 'Hold' -and @($jump.bindings).Count -eq 3) "Jump after set_action: $(Short ($jump | ConvertTo-Json -Compress -Depth 4))"
    $order = @((@($j.maps) | Where-Object { $_.name -eq 'Player' }).actions | ForEach-Object { $_.name }) -join ','
    Expect ($order -eq (($order0 -replace 'Crouch', 'Duck') + ',Dash')) "Player's actions: $order"
    $dash = Get-InAction $j 'Player' 'Dash'
    Expect ($dash.type -eq 'Button' -and (@($dash.bindings | ForEach-Object { "$($_.path)$(if ($_.valid -eq $false) { '!' })" }) -join ',') -eq '<Gamepad>/buttonEast,<Gamepad>/buttonSouht!') "Dash: $(Short ($dash.bindings | ConvertTo-Json -Compress))"
    $pan = Get-InAction $j 'Dbg' 'Pan'
    Expect ($pan.controlType -eq 'Vector2' -and @($pan.bindings).Count -eq 5 -and $pan.bindings[0].composite -eq $true -and (@($pan.bindings | Select-Object -Skip 1 | ForEach-Object { "$($_.name)=$($_.path)" }) -join ',') -eq 'up=<Keyboard>/i,down=<Keyboard>/k,left=<Keyboard>/j,right=<Keyboard>/l') "Pan: $(Short ($pan.bindings | ConvertTo-Json -Compress))"
    Expect ((Get-InAction $j 'Player' 'Sprint').bindings[0].path -eq '<Keyboard>/rightShift') 'Sprint binding 0 was not set'
    Expect (@((Get-InAction $j 'Player' 'Attack').bindings).Count -eq 5 -and (Get-InAction $j 'Player' 'Attack').bindings[0].path -eq '<Mouse>/leftButton') 'Attack binding 0 was not removed'
    Expect (-not (Get-InAction $j 'UI' 'MiddleClick')) 'UI/MiddleClick is still there'
    $schemes = @($j.controlSchemes)
    $pad = $schemes | Where-Object { $_.name -eq 'CliPad' }
    Expect ($pad -and ($pad.devices -join ',') -eq '<Gamepad>,optional <Keyboard>' -and -not ($schemes | Where-Object { $_.name -eq 'XR' })) "schemes: $(Short ($schemes | ConvertTo-Json -Compress))"
    $mem = Get-InMemory
    Expect ($mem -eq (Get-InSig $j)) "in memory differs from the file: $(Short $mem 300)"
    "15 ops (13 kinds), two warnings (a typo, an overlap with Crouch); Jump kept its id; inspect and memory agree"
}

Add-Check 'input.edit-errors' $inRow 'cliext.input edit fails (exit 6, one line, with the fix) on unknown maps, actions, bindings, schemes, ops, action types, interactions, processors, composites and parts, missing fields and ops that would change nothing, saving nothing' {
    Reset-InAsset
    $before = Get-InHash
    $P = '"map":"Player"'
    $cases = @(
        @('[{"op":"remove_action","map":"Playr","action":"Jump"}]', 'Did you mean: Player', 'unknown map, close match'),
        @("[{`"op`":`"remove_action`",$P,`"action`":`"Jum`"}]", 'Did you mean: Jump', 'unknown action, close match'),
        @("[{`"op`":`"remove_action`",$P,`"action`":`"Zzqx`"}]", 'Actions: Move, .*cliext\.input inspect', 'unknown action, no match'),
        @("[{`"op`":`"remove_binding`",$P,`"action`":`"Jump`",`"index`":9}]", 'bindings 0-2, not 9.*cliext\.input inspect', 'binding index out of range'),
        @("[{`"op`":`"remove_binding`",$P,`"action`":`"Jump`",`"id`":`"nope`"}]", "No binding with id 'nope'.*cliext\.input inspect", 'unknown binding id'),
        @("[{`"op`":`"add_action`",$P,`"action`":`"Dash`",`"type`":`"Buton`"}]", 'Use one of: Value, Button, PassThrough', 'unknown action type'),
        @("[{`"op`":`"add_action`",$P,`"action`":`"Jump`"}]", "Action 'Jump' already exists in Player", 'action that exists'),
        @("[{`"op`":`"add_action`",$P}]", 'add_action needs an action', 'add_action without a name'),
        @('[{"op":"add_map"}]', 'add_map needs a map', 'add_map without a name'),
        @('[{"op":"add_map","map":"Player"}]', "Map 'Player' already exists", 'map that exists'),
        @("[{`"op`":`"rename_action`",$P,`"action`":`"Jump`"}]", 'rename_action needs name', 'rename without a name'),
        @("[{`"op`":`"rename_action`",$P,`"action`":`"Jump`",`"name`":`"Move`"}]", "Player already has an action 'Move'", 'rename onto another action'),
        @("[{`"op`":`"rename_action`",$P,`"action`":`"Jump`",`"name`":`"Jump`"}]", 'already has that name', 'rename to the same name'),
        @('[{"op":"rename_map","map":"UI","name":"UI"}]', 'already has that name', 'rename a map to its name'),
        @("[{`"op`":`"set_action`",$P,`"action`":`"Jump`"}]", 'set_action needs type, controlType, interactions or processors', 'set_action with nothing'),
        @("[{`"op`":`"set_action`",$P,`"action`":`"Jump`",`"type`":`"Button`"}]", 'would change nothing', 'set_action that changes nothing'),
        @("[{`"op`":`"set_action`",$P,`"action`":`"Jump`",`"interactions`":`"Hld`"}]", "No interaction 'Hld'. Did you mean: Hold", 'misspelled interaction'),
        @("[{`"op`":`"add_binding`",$P,`"action`":`"Jump`"}]", 'add_binding needs a path', 'binding without a path'),
        @("[{`"op`":`"add_binding`",$P,`"action`":`"Jump`",`"path`":`"<Keyboard>/j`",`"processors`":`"noSuchProcessor`"}]", "No processor 'noSuchProcessor'", 'unknown processor'),
        @("[{`"op`":`"set_binding`",$P,`"action`":`"Jump`",`"index`":0}]", 'set_binding needs path, groups', 'set_binding with nothing'),
        @("[{`"op`":`"set_binding`",$P,`"action`":`"Jump`",`"index`":0,`"path`":`"<Keyboard>/space`"}]", 'would change nothing', 'set_binding that changes nothing'),
        @("[{`"op`":`"add_composite`",$P,`"action`":`"Move`",`"composite`":`"Zzqx`",`"parts`":`"up=<Keyboard>/w`"}]", 'Composites: 1DAxis, 2DVector', 'unknown composite'),
        @("[{`"op`":`"add_composite`",$P,`"action`":`"Move`",`"composite`":`"2DVector`",`"parts`":`"upp=<Keyboard>/w`"}]", "2DVector has no part 'upp'. Did you mean: up", 'misspelled composite part'),
        @("[{`"op`":`"add_composite`",$P,`"action`":`"Move`",`"composite`":`"2DVector`"}]", 'add_composite needs parts', 'composite without parts'),
        @('[{"op":"add_scheme","name":"Gamepad"}]', "Control scheme 'Gamepad' already exists", 'scheme that exists'),
        @('[{"op":"remove_scheme","name":"Gamepd"}]', 'Did you mean: Gamepad', 'unknown scheme'),
        @("[{`"op`":`"remove_actions`",$P,`"action`":`"Jump`"}]", 'Did you mean: remove_action', 'misspelled op'),
        @('[{"op":"frob"}]', "Unknown op 'frob'.*Ops: add_map, remove_map", 'unknown op')
    )
    $seen = @()
    $i = 0
    foreach ($c in $cases) {
        $i++
        $r = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile "in-fail-$i.json" $c[0]), '--format', 'json')
        Expect-InFailure $r $c[1] $before $c[2]
        Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 0) "$($c[2]): reply $(Short $r.stdout)"
        $seen += $c[2]
    }
    $json = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile 'in-badjson.json' '[{"op":'), '--format', 'json')
    Expect-InFailure $json "aren't valid JSON" $before 'broken JSON' 2
    $none = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile 'in-noops.json' '[]'), '--format', 'json')
    Expect-InFailure $none 'No ops given' $before 'no ops' 2
    "$($seen.Count) failures exit 6 with the fix, broken JSON and no ops 2, file unchanged"
}

Add-Check 'input.edit-rollback' $inRow 'an .inputactions batch that removes an action, a binding and a scheme, adds a map, renames and retypes actions, then has one bad op, saves nothing, on disk or in memory, and the next good edit saves only its own change' {
    Reset-InAsset
    $before = Get-InHash
    $j0 = Get-InInspect
    $sig0 = Get-InSig $j0
    $m0 = Get-InMemory
    Expect ($m0 -eq $sig0) "before: memory and file differ: $(Short $m0)"
    $ops = '[{"op":"remove_action","map":"Player","action":"Look"},{"op":"remove_binding","map":"Player","action":"Jump","index":0},{"op":"add_map","map":"Extra"},{"op":"rename_action","map":"Player","action":"Sprint","name":"Run"},{"op":"set_action","map":"Player","action":"Attack","type":"Value"},{"op":"remove_scheme","name":"Touch"},{"op":"remove_action","map":"Player","action":"NoSuchAction"}]'
    $r = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile 'in-bad-batch.json' $ops), '--format', 'json')
    Expect-InFailure $r 'Op 6 \(remove_action\) failed.*Nothing was saved' $before 'bad batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 6 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
    $m1 = Get-InMemory
    Expect ($m1 -eq $m0) "in memory after the failed batch: '$(Short $m0 120)' -> '$(Short $m1 120)'"
    Expect ((Get-InSig (Get-InInspect)) -eq $sig0) 'inspect after the failed batch differs'
    $good = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile 'in-good.json' '[{"op":"remove_binding","map":"Player","action":"Interact","index":0}]'), '--format', 'json')
    Expect ($good.exit -eq 0 -and $good.json.applied -eq 1) "good edit: exit $($good.exit): $(Short ($good.stdout + $good.stderr))"
    $want = $sig0.Replace('Player/Interact:Button:Hold:<Keyboard>/e,<Gamepad>/buttonNorth', 'Player/Interact:Button:Hold:<Gamepad>/buttonNorth')
    Expect ($want -ne $sig0) "the expected change isn't in the signature: $(Short $sig0)"
    $disk = Get-InSig (Get-InInspect)
    Expect ($disk -eq $want) "the file after the good edit: $(Short $disk 300)"
    $m2 = Get-InMemory
    Expect ($m2 -eq $want) "in memory after the good edit: $(Short $m2 300)"
    "failed at op 6, file and memory unchanged; next edit removed only Interact's <Keyboard>/e"
}

Add-Check 'input.missing-asset' $inRow 'cliext.input edit given an .inputactions that does not exist exits 13 at once naming how to make one; another asset type and a path outside Assets/ exit 2; nothing is created' {
    $f = Write-WorkFile 'in-one.json' '[{"op":"add_map","map":"X"}]'
    $cases = @(
        @("$scratchRel/NoSuch.inputactions", 'Create > Input Actions', 13),
        @('Assets/Scenes/TestLevel.unity', 'is a SceneAsset, not an \.inputactions asset', 2),
        @('NotAssets/CliTest.inputactions', "isn't under Assets/ or Packages/", 2)
    )
    $bad = @()
    foreach ($c in $cases) {
        $r = Invoke-Cli @('cliext.input', 'edit', '--path', $c[0], '--ops-file', $f, '--format', 'json') 60
        if ($r.exit -ne $c[2] -or "$($r.stderr)" -notmatch $c[1] -or $r.ms -gt 30000) { $bad += "$($c[0]): exit $($r.exit) in $($r.ms) ms ($(Short $r.stderr 80))" }
    }
    Expect ($bad.Count -eq 0) ($bad -join '; ')
    Expect (-not (Test-Path (Join-Path $scratch 'NoSuch.inputactions')) -and -not (Test-Path (Join-Path $projectPath 'NotAssets'))) 'a failed call created a file'
    'missing 13 naming Create > Input Actions; a scene 2; outside Assets 2'
}

Add-Check 'input.edit-warnings' $inRow 'cliext.input edit warns (and still saves) when an added binding''s control is already bound to another action, naming it, and when a binding''s groups match no control scheme, suggesting the close one; a group the same batch adds a scheme for gives none' {
    Reset-InAsset
    $ops = '[{"op":"add_action","map":"Player","action":"Dash","type":"Button"},' +
           '{"op":"add_binding","map":"Player","action":"Dash","path":"<Keyboard>/leftShift","groups":"Keyboard"},' +
           '{"op":"add_binding","map":"Player","action":"Dash","path":"<Gamepad>/leftShoulder","groups":"Gamepad"},' +
           '{"op":"add_binding","map":"Player","action":"Dash","path":"<Keyboard>/f12","groups":"Arcade"},' +
           '{"op":"add_scheme","name":"Arcade","devices":"<Keyboard>"}]'
    $r = Invoke-Cli @('cliext.input', 'edit', '--path', $script:inPath, '--ops-file', (Write-WorkFile 'in-warn.json' $ops), '--format', 'json')
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 5) "edit: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $w = @($r.json.warnings)
    Expect (@($w | Where-Object { $_ -match '<Keyboard>/leftShift is also bound to Player/Sprint' }).Count -eq 1) "no overlap warning naming Player/Sprint: $(Short ($w -join ' | ') 300)"
    Expect (@($w | Where-Object { $_ -match "groups 'Keyboard'.*matches no control scheme.*Did you mean: Keyboard&Mouse" }).Count -eq 1) "no groups warning for Keyboard: $(Short ($w -join ' | ') 300)"
    Expect (@($w | Where-Object { $_ -match 'Arcade|leftShoulder|f12' }).Count -eq 0) "a warning for a scheme the batch adds or a free control: $(Short ($w -join ' | ') 300)"
    Expect ("$($r.stderr)" -match 'also bound to Player/Sprint') "the warning isn't on stderr: $(Short $r.stderr)"
    $dash = Get-InAction (Get-InInspect) 'Player' 'Dash'
    Expect (@($dash.bindings).Count -eq 3) "Dash wasn't saved with its bindings: $(Short ($dash | ConvertTo-Json -Compress -Depth 4))"
    "$($w.Count) warnings: overlap with Player/Sprint, groups Keyboard (did you mean Keyboard&Mouse); saved"
}

Add-Check 'input.edit-mode' $inRow 'in Edit mode, add_device, set, press, record_start and replay exit 6 naming editor.playmode start and add no device; devices and replay_status still answer' {
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-mode.cs' 'return UnityEditor.EditorApplication.isPlaying ? "playing" : "edit";'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit') "not in Edit mode: $(Short $mode.stdout)"
    $d0 = Invoke-Cli @('cliext.input', 'devices', '--format', 'json')
    Expect ($d0.exit -eq 0) "devices: exit $($d0.exit): $(Short ($d0.stdout + $d0.stderr))"
    $trace = Write-WorkFile 'in-edit.inputtrace' 'x'
    $cases = @(
        @(@('add_device', '--layout', 'Gamepad'), 'add_device'),
        @(@('set', '--control', 'buttonSouth', '--value', '1'), 'set'),
        @(@('press', '--control', '<Keyboard>/space'), 'press'),
        @(@('record_start'), 'record_start'),
        @(@('replay', '--file', $trace), 'replay')
    )
    foreach ($c in $cases) {
        $r = Invoke-Cli (@('cliext.input') + $c[0] + @('--format', 'json'))
        Expect-InFailure $r 'enter Play mode first \(editor\.playmode start\)' '' "$($c[1]) in Edit mode"
    }
    $d1 = Invoke-Cli @('cliext.input', 'devices', '--format', 'json')
    Expect ($d1.stdout -eq $d0.stdout -and -not (@($d1.json) | Where-Object { $_.virtual })) "the devices changed: $(Short $d1.stdout)"
    $st = Invoke-Cli @('cliext.input', 'replay_status', '--format', 'json')
    Expect ($st.exit -eq 0 -and $st.json.replaying -eq $false -and $st.json.recording -eq $false) "replay_status: exit $($st.exit): $(Short ($st.stdout + $st.stderr))"
    "5 commands exit 6 naming editor.playmode start; $(@($d1.json).Count) devices, none added"
}

Add-Check 'input.play-mode' $inRow 'in Play mode, virtual devices drive the project-wide actions: set moves Player/Move, press fires Player/Jump from a gamepad button and a full <Keyboard>/space path (seen by actions and by a performed hook), a recording saves those events and replays them; bad controls, values, devices and layouts fail with the fix; leaving Play mode leaves no device and a clean scene' {
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $hook = @'
var a = UnityEngine.InputSystem.InputSystem.actions;
if (a == null) return "no project-wide actions";
var jump = a.FindAction("Player/Jump"); var move = a.FindAction("Player/Move");
UnityEditor.SessionState.SetInt("cliext.clitest.jumps", 0); UnityEditor.SessionState.SetString("cliext.clitest.move", "");
System.Action<UnityEngine.InputSystem.InputAction.CallbackContext> j = ctx => UnityEditor.SessionState.SetInt("cliext.clitest.jumps", UnityEditor.SessionState.GetInt("cliext.clitest.jumps", 0) + 1);
System.Action<UnityEngine.InputSystem.InputAction.CallbackContext> m = ctx => UnityEditor.SessionState.SetString("cliext.clitest.move", ctx.ReadValue<UnityEngine.Vector2>().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + ctx.control.device.name);
jump.performed += j; move.performed += m;
System.AppDomain.CurrentDomain.SetData("cliext.clitest.hooks", new object[] { j, m });
return "ok";
'@
    $unhook = @'
var a = UnityEngine.InputSystem.InputSystem.actions;
var h = System.AppDomain.CurrentDomain.GetData("cliext.clitest.hooks") as object[];
if (a != null && h != null)
{
    a.FindAction("Player/Jump").performed -= (System.Action<UnityEngine.InputSystem.InputAction.CallbackContext>)h[0];
    a.FindAction("Player/Move").performed -= (System.Action<UnityEngine.InputSystem.InputAction.CallbackContext>)h[1];
}
System.AppDomain.CurrentDomain.SetData("cliext.clitest.hooks", null);
UnityEditor.SessionState.EraseInt("cliext.clitest.jumps"); UnityEditor.SessionState.EraseString("cliext.clitest.move");
return "ok";
'@
    $readHook = Write-WorkFile 'in-read-hook.cs' 'return UnityEditor.SessionState.GetInt("cliext.clitest.jumps", -1) + "|" + UnityEditor.SessionState.GetString("cliext.clitest.move", "");'
    function Read-InHook { $x = Invoke-Cli @('script', 'eval', '--file', $readHook, '--format', 'json'); Expect ($x.exit -eq 0) "reading the hook: $(Short ($x.stdout + $x.stderr))"; ($x.stdout.Trim() | ConvertFrom-Json) }
    function Get-InActionRow([string]$name) { $x = Invoke-Cli @('cliext.input', 'actions', '--filter', $name, '--format', 'json'); Expect ($x.exit -eq 0) "actions: exit $($x.exit)"; @($x.json) | Where-Object { $_.action -eq $name } | Select-Object -First 1 }
    $tracePath = Join-Path $work 'in-run.inputtrace'
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $h = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-hook.cs' $hook), '--format', 'json')
        Expect ($h.exit -eq 0 -and "$($h.stdout)" -match 'ok') "hooking the actions: $(Short ($h.stdout + $h.stderr))"
        $pad = Invoke-Cli @('cliext.input', 'add_device', '--layout', 'Gamepad', '--format', 'json')
        Expect ($pad.exit -eq 0 -and $pad.json.name -eq 'CLI Gamepad') "add_device Gamepad: exit $($pad.exit): $(Short ($pad.stdout + $pad.stderr))"
        $devs = Invoke-Cli @('cliext.input', 'devices', '--format', 'json')
        Expect ([bool](@($devs.json) | Where-Object { $_.id -eq $pad.json.id -and $_.virtual })) "devices doesn't list the virtual gamepad: $(Short $devs.stdout)"
        $rec = Invoke-Cli @('cliext.input', 'record_start', '--format', 'json')
        Expect ($rec.exit -eq 0 -and $rec.json.recording -eq $true) "record_start: exit $($rec.exit): $(Short ($rec.stdout + $rec.stderr))"

        $s = Invoke-Cli @('cliext.input', 'set', '--control', 'leftStick', '--value', '[0,1]', '--format', 'json')
        Expect ($s.exit -eq 0 -and $s.json.control -eq '/CLI Gamepad/leftStick') "set leftStick: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $mv = Get-InActionRow 'Player/Move'
        Expect ($mv.control -eq '/CLI Gamepad/leftStick' -and $mv.lastValue -eq '[0,1]') "actions Player/Move: $(Short ($mv | ConvertTo-Json -Compress))"
        $hk = Read-InHook
        Expect ($hk -match '\|\(0, 1\) CLI Gamepad$') "the Move hook saw: $hk"
        [void](Invoke-Cli @('cliext.input', 'set', '--control', 'leftStick', '--value', '[0,0]', '--format', 'json'))

        $p = Invoke-Cli @('cliext.input', 'press', '--control', 'buttonSouth', '--hold', '0.1', '--format', 'json')
        Expect ($p.exit -eq 0 -and $p.json.control -eq '/CLI Gamepad/buttonSouth') "press buttonSouth: exit $($p.exit): $(Short ($p.stdout + $p.stderr))"
        $jumps1 = [int](("$(Read-InHook)" -split '\|')[0])
        Expect ($jumps1 -ge 1) "Player/Jump wasn't performed by buttonSouth (hook count $jumps1)"
        $kb = Invoke-Cli @('cliext.input', 'add_device', '--layout', 'Keyboard', '--format', 'json')
        Expect ($kb.exit -eq 0) "add_device Keyboard: exit $($kb.exit): $(Short ($kb.stdout + $kb.stderr))"
        $k = Invoke-Cli @('cliext.input', 'press', '--control', '<Keyboard>/space', '--format', 'json')
        Expect ($k.exit -eq 0 -and $k.json.device -eq 'CLI Keyboard') "press <Keyboard>/space: exit $($k.exit): $(Short ($k.stdout + $k.stderr))"
        $jumps2 = [int](("$(Read-InHook)" -split '\|')[0])
        Expect ($jumps2 -gt $jumps1) "Player/Jump wasn't performed by <Keyboard>/space (hook count $jumps1 -> $jumps2)"
        $jr = Get-InActionRow 'Player/Jump'
        Expect ($jr.lastChange -match 'Performed|Canceled' -and $jr.secondsAgo -lt 10) "actions Player/Jump: $(Short ($jr | ConvertTo-Json -Compress))"

        $notTrace = Write-WorkFile 'in-not-a-trace.inputtrace' 'not a trace'
        $cases = @(
            @(@('set', '--control', 'nosuch', '--value', '1', '--device', 'CLI Gamepad'), "CLI Gamepad has no control 'nosuch'.*cliext\.input devices", 13, 'unknown control'),
            @(@('set', '--control', 'buttonSouht', '--value', '1', '--device', 'CLI Gamepad'), 'Did you mean: buttonSouth', 13, 'misspelled control'),
            @(@('set', '--control', '<Gamepad>/buttonSouht', '--value', '1'), 'Did you mean: buttonSouth', 13, 'misspelled full path'),
            @(@('set', '--control', '<Joystick>/trigger', '--value', '1'), 'no Joystick device.*add_device --layout Joystick', 13, 'a path to a device that is not there'),
            @(@('set', '--control', 'buttonSouth', '--value', 'abc', '--device', 'CLI Gamepad'), "'abc' isn't a Single value: give a number", 2, 'non-numeric value'),
            @(@('set', '--control', 'leftStick', '--value', '1,2,3', '--device', 'CLI Gamepad'), 'Expected 2 numbers', 2, 'three numbers for a stick'),
            @(@('set', '--control', 'buttonSouth', '--value', '1', '--device', 'NoSuchPad'), "No device 'NoSuchPad'.*cliext\.input devices", 13, 'unknown device'),
            @(@('press', '--control', 'leftStick', '--device', 'CLI Gamepad'), 'not a button or key: use cliext\.input set', 2, 'press a stick'),
            @(@('press', '--control', 'buttonSouth', '--hold', '0'), '--hold must be more than 0', 2, 'press for no time'),
            @(@('add_device', '--layout', 'Gamepd'), "No device layout 'Gamepd'. Did you mean: Gamepad", 2, 'misspelled layout'),
            @(@('add_device', '--layout', 'Button'), "'Button' is a control layout, not a device layout", 2, 'a control layout'),
            @(@('record_start'), 'Already recording.*record_stop', 6, 'second record_start'),
            @(@('remove_device', '--device', 'CLI Gamepd'), 'Did you mean: CLI Gamepad', 13, 'misspelled device to remove'),
            @(@('replay', '--file', (Join-Path $work 'none.inputtrace')), 'No trace at.*record_start', 13, 'missing trace'),
            @(@('replay', '--file', $notTrace), "isn't an input trace", 2, 'a file that is not a trace')
        )
        $seen = @()
        foreach ($c in $cases) {
            $x = Invoke-Cli (@('cliext.input') + $c[0] + @('--format', 'json'))
            Expect-InFailure $x $c[1] '' $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        $after = Invoke-Cli @('cliext.input', 'devices', '--format', 'json')
        Expect (@($after.json | Where-Object { $_.virtual }).Count -eq 2) "a failed call changed the virtual devices: $(Short $after.stdout)"

        $stop = Invoke-Cli @('cliext.input', 'record_stop', '--out', $tracePath, '--format', 'json')
        Expect ($stop.exit -eq 0 -and $stop.json.events -gt 0 -and (Test-Path $tracePath)) "record_stop: exit $($stop.exit): $(Short ($stop.stdout + $stop.stderr))"
        $readTrace = "var t = UnityEngine.InputSystem.LowLevel.InputEventTrace.LoadFrom(@`"$tracePath`"); var r = t.eventCount + `"|`" + string.Join(`",`", t.deviceInfos.Select(d => d.layout)); t.Dispose(); return r;"
        $tr = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-trace.cs' $readTrace), '--format', 'json')
        Expect ($tr.exit -eq 0 -and "$($tr.stdout)" -match "^`"$($stop.json.events)\|.*Gamepad") "the saved trace: $(Short ($tr.stdout + $tr.stderr))"
        $again = Invoke-Cli @('cliext.input', 'record_stop', '--out', $tracePath, '--format', 'json')
        Expect-InFailure $again 'Not recording: start with cliext\.input record_start' '' 'record_stop when not recording'
        [void](Invoke-Cli @('cliext.input', 'record_start', '--device', 'CLI Gamepad', '--format', 'json'))
        $empty = Invoke-Cli @('cliext.input', 'record_stop', '--out', (Join-Path $work 'in-empty.inputtrace'), '--format', 'json')
        Expect-InFailure $empty 'caught no input events, so nothing was saved' '' 'a recording with no events'
        Expect (-not (Test-Path (Join-Path $work 'in-empty.inputtrace'))) 'an empty recording was saved'

        $rp = Invoke-Cli @('cliext.input', 'replay', '--file', $tracePath, '--timing', 'false', '--format', 'json')
        Expect ($rp.exit -eq 0 -and $rp.json.events -eq $stop.json.events) "replay: exit $($rp.exit): $(Short ($rp.stdout + $rp.stderr))"
        $doneCode = @'
var t = System.Type.GetType("UnityCLIExtensions.InputSystem.InputCommands, UnityCLIExtensions.InputSystem");
var r = t.GetField("s_Replay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null) as UnityEngine.InputSystem.LowLevel.InputEventTrace.ReplayController;
return r == null || r.finished ? "ready" : "at " + r.position;
'@
        $w = Invoke-Cli @('cliext.playmode', 'wait_until', '--file', (Write-WorkFile 'in-replay-done.cs' $doneCode), '--timeout', '30', '--interval', '0.25', '--format', 'json') 60
        Expect ($w.exit -eq 0) "the replay didn't finish: $(Short ($w.stdout + $w.stderr))"
        $rs = Invoke-Cli @('cliext.input', 'replay_status', '--format', 'json')
        Expect ($rs.exit -eq 0 -and $rs.json.replaying -eq $false -and $rs.json.position -eq $stop.json.events) "replay_status: $(Short ($rs.stdout + $rs.stderr))"
        $jumps3 = [int](("$(Read-InHook)" -split '\|')[0])
        Expect ($jumps3 -gt $jumps2) "the replay didn't perform Player/Jump (hook count $jumps2 -> $jumps3)"

        $rm = Invoke-Cli @('cliext.input', 'remove_device', '--format', 'json')
        Expect ($rm.exit -eq 0 -and (@($rm.json.removed) -join ',') -eq 'CLI Gamepad,CLI Keyboard') "remove_device: exit $($rm.exit): $(Short ($rm.stdout + $rm.stderr))"
        $left = Invoke-Cli @('cliext.input', 'devices', '--format', 'json')
        Expect (-not (@($left.json) | Where-Object { $_.virtual -or $_.name -like 'CLI *' })) "devices after remove_device: $(Short $left.stdout)"
    }
    finally {
        [void](Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-unhook.cs' $unhook), '--format', 'json'))
        [void](Invoke-Cli @('cliext.input', 'remove_device', '--format', 'json'))
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
    }
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'in-mode2.cs' 'var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return (UnityEditor.EditorApplication.isPlaying ? "playing" : "edit") + "|" + a.isDirty;'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit\|False') "after Play mode: $(Short $mode.stdout)"
    $final = Invoke-Cli @('cliext.input', 'devices', '--format', 'json')
    Expect (-not (@($final.json) | Where-Object { $_.virtual -or $_.name -like 'CLI *' })) "devices after Play mode: $(Short $final.stdout)"
    "Move [0,1] from the stick; Jump x$jumps3 (button, <Keyboard>/space, replay of $($stop.json.events) events); $($seen.Count) failures with the fix; back in Edit mode, scene clean, no virtual devices"
}

# --- cliext.uitk ------------------------------------------------------------------------------------------------------

# Edit checks work on a scratch UXML and USS written fresh by Reset-UiFiles. "In memory" is the imported
# VisualTreeAsset (CloneTree: each named element's type, name, classes and text) and StyleSheet (its rules' selectors
# and property names), read by a snippet and compared with what inspect reads from the file. Play mode checks add a
# UIDocument with that UXML to the running scene from a snippet (gone when Play mode ends), with a Button that changes
# a Label and a Toggle whose change event does too.
$script:uiUxml = "$scratchRel/CliTestPanel.uxml"
$script:uiUss = "$scratchRel/CliTestPanel.uss"
$uiRow = 'cliext.uitk'
$uiUxmlText = @'
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements">
    <Style src="CliTestPanel.uss" />
    <ui:VisualElement name="menu" class="panel">
        <ui:Label name="status" text="idle" class="caption" />
        <ui:Button name="start" text="Start" class="primary" />
        <ui:TextField name="player" value="Ann" />
        <ui:Toggle name="sound" value="true" />
        <ui:Slider name="volume" low-value="0" high-value="10" value="5" />
        <ui:DropdownField name="mode" choices="Easy,Hard" index="0" />
    </ui:VisualElement>
    <ui:VisualElement name="footer" />
</ui:UXML>
'@
$uiUssText = @'
.panel {
    width: 300px;
    padding: 4px;
}

.primary {
    color: #ffffff;
    background-color: #2050c0;
}

#status {
    font-size: 14px;
}
'@

function Reset-UiFiles {
    [void](Write-ScratchFile 'CliTestPanel.uxml' $uiUxmlText)
    [void](Write-ScratchFile 'CliTestPanel.uss' $uiUssText)
    $code = 'UnityEditor.AssetDatabase.Refresh(); UnityEditor.AssetDatabase.ImportAsset("{{uss}}", UnityEditor.ImportAssetOptions.ForceUpdate); UnityEditor.AssetDatabase.ImportAsset("{{uxml}}", UnityEditor.ImportAssetOptions.ForceUpdate); return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>("{{uxml}}") != null && UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.StyleSheet>("{{uss}}") != null ? "ok" : "not imported";'
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-setup.cs' $code.Replace('{{uxml}}', $script:uiUxml).Replace('{{uss}}', $script:uiUss)), '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok') "scratch UXML/USS: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
}
function Get-UiHash { (Get-FileHash (Join-Path $projectPath ($script:uiUxml -replace '/', '\'))).Hash + '|' + (Get-FileHash (Join-Path $projectPath ($script:uiUss -replace '/', '\'))).Hash }
function Get-UiInspect([string]$path) {
    $r = Invoke-Cli @('cliext.uitk', 'inspect', '--path', $path, '--format', 'json')
    Expect ($r.exit -eq 0) "inspect ${path}: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.json
}
function Get-UiSig {
    # The same text Get-UiMemory builds from the imported assets, from inspect's reading of the files.
    $els = New-Object Collections.Generic.List[string]
    function Walk($nodes) { foreach ($n in @($nodes)) { if ($n -eq $null) { continue }; if ($n.name) { $els.Add("$($n.type)#$($n.name)" + (@(("$($n.class)" -split ' ') | Where-Object { $_ } | Sort-Object | ForEach-Object { ".$_" }) -join '') + $(if ($n.text -ne $null) { "=$($n.text)" } else { '' })) }; Walk $n.children } }
    Walk (Get-UiInspect $script:uiUxml).tree
    $rules = @((Get-UiInspect $script:uiUss).rules | ForEach-Object { "$($_.selector){" + (@($_.properties | ForEach-Object { ($_ -split ':')[0] }) -join ',') + '}' })
    ($els -join ',') + '|' + ($rules -join ';')
}
function Get-UiMemory {
    $code = @'
var vta = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>("{{uxml}}");
var ss = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.StyleSheet>("{{uss}}");
if (vta == null || ss == null) return "no asset";
var els = new System.Collections.Generic.List<string>();
UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.VisualElement>(vta.CloneTree()).ForEach(e =>
{
    if (string.IsNullOrEmpty(e.name) || e.name.StartsWith("unity-")) return;
    els.Add(e.GetType().Name + "#" + e.name + string.Concat(e.GetClasses().Where(c => !c.StartsWith("unity-")).OrderBy(c => c, System.StringComparer.Ordinal).Select(c => "." + c)) + (e is UnityEngine.UIElements.TextElement t ? "=" + t.text : ""));
});
var F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
var rules = new System.Collections.Generic.List<string>();
foreach (var rule in (System.Array)ss.GetType().GetProperty("rules", F).GetValue(ss))
{
    var sels = new System.Collections.Generic.List<string>();
    foreach (var s in (System.Array)rule.GetType().GetProperty("complexSelectors", F).GetValue(rule))
        sels.Add(string.Concat(System.Text.RegularExpressions.Regex.Matches(s.ToString(), @"value=([\w\-]+), type=(\w+)").Cast<System.Text.RegularExpressions.Match>()
            .Select(m => (m.Groups[2].Value == "Class" ? "." : m.Groups[2].Value == "ID" ? "#" : "") + m.Groups[1].Value)));
    var names = new System.Collections.Generic.List<string>();
    foreach (var p in (System.Array)rule.GetType().GetProperty("properties", F).GetValue(rule)) names.Add((string)p.GetType().GetProperty("name", F).GetValue(p));
    rules.Add(string.Join(",", sels) + "{" + string.Join(",", names) + "}");
}
return string.Join(",", els) + "|" + string.Join(";", rules);
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-mem.cs' $code.Replace('{{uxml}}', $script:uiUxml).Replace('{{uss}}', $script:uiUss)), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    ($r.stdout.Trim() | ConvertFrom-Json)
}
function Invoke-UiEdit([string]$path, [string]$ops, [string]$name) {
    Invoke-Cli @('cliext.uitk', 'edit', '--path', $path, '--ops-file', (Write-WorkFile $name $ops), '--format', 'json')
}
function Expect-UiFailure($r, [string]$hint, [string]$before, [string]$what, [int]$exit = 6) {
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ($r.exit -eq $exit) "${what}: exit $($r.exit), expected ${exit}: $(Short ($r.stdout + $r.stderr))"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    Expect ("$($r.stderr)" -notmatch '   at |Exception:|\[0x') "${what}: a stack trace instead of a message: $(Short $r.stderr)"
    Expect (@("$($r.stderr)".Trim() -split "`r?`n").Count -eq 1) "${what}: the error isn't one line: $(Short $r.stderr)"
    if ($before) { Expect ((Get-UiHash) -eq $before) "${what}: the UXML or USS changed on disk" }
}

Add-Check 'uitk.inspect' $uiRow 'cliext.uitk inspect reads a UXML as its tree and style sheets and a USS as its rules; a missing .uxml or .uss exits 13 naming how to make one, another file type and a path outside Assets/ exit 2, for inspect and edit' {
    Reset-UiFiles
    $u = Get-UiInspect $script:uiUxml
    Expect ((@($u.styles) -join ',') -eq 'CliTestPanel.uss') "styles: $(@($u.styles) -join ',')"
    $menu = @($u.tree)[0]
    Expect ($menu.name -eq 'menu' -and $menu.class -eq 'panel' -and (@($menu.children | ForEach-Object { "$($_.type)#$($_.name)" }) -join ',') -eq 'Label#status,Button#start,TextField#player,Toggle#sound,Slider#volume,DropdownField#mode') "tree: $(Short ($u.tree | ConvertTo-Json -Compress -Depth 6) 300)"
    $slider = @($menu.children) | Where-Object { $_.name -eq 'volume' }
    Expect ((@($slider.attributes) -join ',') -eq 'low-value=0,high-value=10,value=5') "Slider attributes: $(@($slider.attributes) -join ',')"
    $s = Get-UiInspect $script:uiUss
    Expect ((@($s.rules | ForEach-Object { "$($_.selector)=$(@($_.properties) -join '/')" }) -join ';') -eq '.panel=width: 300px/padding: 4px;.primary=color: #ffffff/background-color: #2050c0;#status=font-size: 14px') "rules: $(Short ($s.rules | ConvertTo-Json -Compress) 300)"
    $f = Write-WorkFile 'ui-one.json' '[{"op":"remove","selector":"#start"}]'
    $cases = @(
        @("$scratchRel/NoSuch.uxml", 'No \.uxml file at.*Create > UI Toolkit > UI Document', 13, 'missing .uxml'),
        @("$scratchRel/NoSuch.uss", 'No \.uss file at.*Create > UI Toolkit > Style Sheet', 13, 'missing .uss'),
        @('Assets/Scenes/TestLevel.unity', 'is a SceneAsset, not a \.uxml or \.uss file', 2, 'a scene as the file'),
        @('ProjectSettings/ProjectVersion.txt', "isn't under Assets/ or Packages/", 2, 'path outside Assets')
    )
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli @('cliext.uitk', 'inspect', '--path', $c[0], '--format', 'json')
        Expect-UiFailure $x $c[1] '' "inspect $($c[3])" $c[2]
        $y = Invoke-Cli @('cliext.uitk', 'edit', '--path', $c[0], '--ops-file', $f, '--format', 'json') 60
        Expect-UiFailure $y $c[1] '' "edit $($c[3])" $c[2]
        Expect ($y.ms -lt 30000) "edit $($c[3]) took $($y.ms) ms"
        $seen += "$($c[3]) $($x.exit)"
    }
    Expect (-not (Test-Path (Join-Path $scratch 'NoSuch.uxml')) -and -not (Test-Path (Join-Path $scratch 'NoSuch.uss'))) 'a failed call created a file'
    "menu with 6 controls, 1 style sheet; 3 rules; $($seen -join ', ')"
}

Add-Check 'uitk.style-link' $uiRow 'a UXML imported right after the USS it names was reimported loads with a null style sheet: cliext.uitk inspect reports linked false with the fix, inspect --reimport true relinks it (the loaded asset agrees); inspect of a linked one has no warning' {
    New-ScratchFolder
    $stale = @'
string d = "{{dir}}";
System.IO.File.WriteAllText(d + "/CliTestLink.uss", "/* empty */\n");
System.IO.File.WriteAllText(d + "/CliTestLink.uxml", "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"></ui:UXML>\n");
UnityEditor.AssetDatabase.ImportAsset(d + "/CliTestLink.uss", UnityEditor.ImportAssetOptions.ForceUpdate);
UnityEditor.AssetDatabase.ImportAsset(d + "/CliTestLink.uxml", UnityEditor.ImportAssetOptions.ForceUpdate);
System.IO.File.WriteAllText(d + "/CliTestLink.uss", ".panel { width: 260px; }\n");
UnityEditor.AssetDatabase.ImportAsset(d + "/CliTestLink.uss", UnityEditor.ImportAssetOptions.ForceUpdate);
System.IO.File.WriteAllText(d + "/CliTestLink.uxml", "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n    <ui:Style src=\"CliTestLink.uss\" />\n    <ui:VisualElement class=\"panel\" />\n</ui:UXML>\n");
UnityEditor.AssetDatabase.ImportAsset(d + "/CliTestLink.uxml", UnityEditor.ImportAssetOptions.ForceUpdate);
return "ok";
'@
    $loaded = "var v = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(`"$scratchRel/CliTestLink.uxml`"); return string.Join(`",`", v.stylesheets.Select(s => s == null ? `"null`" : s.name));"
    $uxml = "$scratchRel/CliTestLink.uxml"
    try {
        Expect ((Invoke-Eval 'ui-stale.cs' $stale.Replace('{{dir}}', $scratchRel)) -eq 'ok') 'scratch UXML and USS'
        $before = Invoke-Eval 'ui-loaded.cs' $loaded
        Expect ($before -eq 'null') "the stale link didn't happen (loaded: $before): the check needs it"
        $i = Invoke-Cli @('cliext.uitk', 'inspect', '--path', $uxml, '--format', 'json')
        Expect ($i.exit -eq 0) "inspect: exit $($i.exit): $(Short $i.stderr)"
        $l = @($i.json.styleSheets)
        Expect ($l.Count -eq 1 -and $l[0].sheet -eq "$scratchRel/CliTestLink.uss" -and $l[0].exists -and -not $l[0].linked) "styleSheets: $(Short ($i.json.styleSheets | ConvertTo-Json -Compress))"
        Expect ((@($i.json.warnings) -join ' ') -match 'holds null.*--reimport true') "warnings: $(@($i.json.warnings) -join ' ')"
        $r = Invoke-Cli @('cliext.uitk', 'inspect', '--path', $uxml, '--reimport', 'true', '--format', 'json')
        Expect ($r.exit -eq 0 -and $r.json.reimported -and @($r.json.styleSheets)[0].linked -and @($r.json.warnings).Count -eq 0) "--reimport: $(Short $r.stdout 300)"
        $after = Invoke-Eval 'ui-loaded.cs' $loaded
        Expect ($after -eq 'CliTestLink') "loaded after --reimport: $after"
        "inspect named the null link and the fix; --reimport relinked it (loaded: $after)"
    }
    finally { [void](Invoke-Eval 'ui-link-clean.cs' "UnityEditor.AssetDatabase.DeleteAsset(`"$uxml`"); UnityEditor.AssetDatabase.DeleteAsset(`"$scratchRel/CliTestLink.uss`"); return `"ok`";") }
}

Add-Check 'uitk.edit' $uiRow 'cliext.uitk edit runs every UXML op (add, remove, move, set_attributes, add_class, remove_class) in one batch and every USS op (set_rule merge, new and replace, remove_property, remove_rule) in another; inspect and the imported assets in memory show them all' {
    Reset-UiFiles
    $uops = @'
[{"op":"add","selector":"#menu","type":"Label","name":"hint","text":"Press start","class":"caption small","index":1},
 {"op":"remove","selector":"#volume"},
 {"op":"move","selector":"#mode","to":"#footer"},
 {"op":"set_attributes","selector":"#start","text":"Go","attributes":"tooltip=Start the game"},
 {"op":"add_class","selector":"Toggle","class":"wide"},
 {"op":"remove_class","selector":"#status","class":"caption"}]
'@
    $r = Invoke-UiEdit $script:uiUxml $uops 'ui-edit.json'
    Expect ($r.exit -eq 0 -and $r.json.applied -eq 6 -and $r.json.saved -eq $true) "UXML edit: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $u = Get-UiInspect $script:uiUxml
    $menu = @($u.tree) | Where-Object { $_.name -eq 'menu' }
    $footer = @($u.tree) | Where-Object { $_.name -eq 'footer' }
    Expect ((@($menu.children | ForEach-Object { "$($_.type)#$($_.name)" }) -join ',') -eq 'Label#status,Label#hint,Button#start,TextField#player,Toggle#sound') "menu: $(Short ($menu.children | ConvertTo-Json -Compress -Depth 3) 300)"
    Expect ((@($footer.children | ForEach-Object { "$($_.type)#$($_.name)" }) -join ',') -eq 'DropdownField#mode') "footer: $(Short ($footer | ConvertTo-Json -Compress -Depth 3))"
    $start = @($menu.children) | Where-Object { $_.name -eq 'start' }
    Expect ($start.text -eq 'Go' -and "$($start.attributes)" -match 'tooltip=Start the game') "start: $(Short ($start | ConvertTo-Json -Compress))"
    Expect ((@($menu.children) | Where-Object { $_.name -eq 'sound' }).class -eq 'wide' -and -not (@($menu.children) | Where-Object { $_.name -eq 'status' }).class) 'classes were not changed'
    $sops = @'
[{"op":"set_rule","selector":".panel","properties":"width: 320px; margin: 2px"},
 {"op":"set_rule","selector":".wide","properties":"flex-grow: 1"},
 {"op":"set_rule","selector":".primary","properties":"color: #000000","replace":true},
 {"op":"remove_property","selector":".panel","properties":"padding"},
 {"op":"remove_rule","selector":"#status"}]
'@
    $s = Invoke-UiEdit $script:uiUss $sops 'ui-edit-uss.json'
    Expect ($s.exit -eq 0 -and $s.json.applied -eq 5 -and @($s.json.import).Count -eq 0) "USS edit: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
    $rules = (@((Get-UiInspect $script:uiUss).rules | ForEach-Object { "$($_.selector)=$(@($_.properties) -join '/')" }) -join ';')
    Expect ($rules -eq '.panel=width: 320px/margin: 2px;.primary=color: #000000;.wide=flex-grow: 1') "rules: $rules"
    $sig = Get-UiSig
    $mem = Get-UiMemory
    Expect ($mem -eq $sig) "in memory differs from the files: '$(Short $mem 200)' vs '$(Short $sig 200)'"
    "6 UXML ops and 5 USS ops; inspect and memory agree: $(Short $sig 120)"
}

Add-Check 'uitk.edit-errors' $uiRow 'cliext.uitk edit fails (exit 6, one line, with the fix) on selectors that match nothing or can''t be read, unknown element types, ops and rules, missing fields, removing the root, ops that would change nothing, and USS the importer warns about; saving nothing' {
    Reset-UiFiles
    $before = Get-UiHash
    $U = $script:uiUxml; $S = $script:uiUss
    $cases = @(
        @($U, '[{"op":"remove","selector":"#strat"}]', "no element named 'strat'. Did you mean: #start\?.*cliext\.uitk inspect", 'unknown name, close match'),
        @($U, '[{"op":"remove","selector":".primry"}]', 'Did you mean: \.primary', 'unknown class'),
        @($U, '[{"op":"remove","selector":"Buton"}]', "no element of type 'Buton'. Did you mean: Button", 'unknown type in a selector'),
        @($U, '[{"op":"remove","selector":"Button > Label"}]', 'its parts are there, but not nested that way', 'a selector matching nothing'),
        @($U, '[{"op":"remove","selector":"Button:hover"}]', "Pseudo-classes such as ':hover' aren't supported", 'a pseudo-class'),
        @($U, '[{"op":"remove","selector":"Button[x]"}]', "Can't read 'Button\[x\]'", 'an unreadable selector'),
        @($U, '[{"op":"add","selector":"#menu","type":"Buton"}]', "No element type 'Buton'.*Did you mean: Button", 'unknown element type'),
        @($U, '[{"op":"add","selector":"#menu","type":"x:Label"}]', "The UXML has no 'x' prefix", 'unknown prefix'),
        @($U, '[{"op":"add","selector":"#menu"}]', 'add needs a type', 'add without a type'),
        @($U, '[{"op":"remove","selector":"UXML"}]', "Can't remove the UXML root", 'removing the root'),
        @($U, '[{"op":"move","selector":"#start"}]', 'move needs to', 'move without to'),
        @($U, '[{"op":"move","selector":"#menu","to":"#status"}]', "Can't move an element into itself", 'move into itself'),
        @($U, '[{"op":"set_attributes","selector":"#start"}]', 'set_attributes needs attributes', 'set_attributes with nothing'),
        @($U, '[{"op":"set_attributes","selector":"#start","attributes":"tooltip"}]', "'tooltip' isn't name=value", 'an attribute without a value'),
        @($U, '[{"op":"set_attributes","selector":"#start","text":"Start"}]', 'would change nothing', 'set_attributes that changes nothing'),
        @($U, '[{"op":"add_class","selector":"#start","class":"primary"}]', 'would change nothing', 'add_class already there'),
        @($U, '[{"op":"remove_class","selector":"#start","class":"nope"}]', 'would change nothing', 'remove_class not there'),
        @($U, '[{"op":"add_class","selector":"#start"}]', 'add_class needs class', 'add_class without a class'),
        @($U, '[{"op":"remov","selector":"#start"}]', "Unknown UXML op 'remov'. Did you mean: remove", 'misspelled op'),
        @($U, '[{"op":"set_rule","selector":".panel","properties":"width: 1px"}]', "'set_rule' is a USS op: pass the \.uss file", 'a USS op on a UXML'),
        @($S, '[{"op":"remove_rule","selector":".primry"}]', "No rule '\.primry'.*Did you mean: \.primary", 'unknown rule'),
        @($S, '[{"op":"remove_property","selector":".panel","properties":"widht"}]', "Rule '\.panel' has no property 'widht'. Did you mean: width", 'unknown property to remove'),
        @($S, '[{"op":"remove_property","selector":".panel"}]', 'remove_property needs properties', 'remove_property with nothing'),
        @($S, '[{"op":"set_rule","selector":".panel"}]', 'set_rule needs properties', 'set_rule with nothing'),
        @($S, '[{"op":"set_rule","selector":".panel","properties":"width"}]', "'width' isn't property: value", 'a property without a value'),
        @($S, '[{"op":"set_rule","selector":".panel","properties":"width: 300px"}]', 'would change nothing', 'set_rule that changes nothing'),
        @($S, '[{"op":"set_rule","selector":".panel","properties":"colr: red"}]', "importer rejected the result.*Unknown property 'colr' \(did you mean 'color'\?\)", 'unknown USS property'),
        @($S, '[{"op":"set_rule","selector":".panel","properties":"width: abc"}]', "importer rejected the result.*found 'abc'", 'a bad USS value'),
        @($S, '[{"op":"set_rule","selector":".pa{nel","properties":"width: 1px"}]', "isn't a USS selector", 'a selector with a brace'),
        @($S, '[{"op":"add","type":"Label"}]', "'add' is a UXML op: pass the \.uxml file", 'a UXML op on a USS')
    )
    $seen = @()
    $i = 0
    foreach ($c in $cases) {
        $i++
        $r = Invoke-UiEdit $c[0] $c[1] "ui-fail-$i.json"
        Expect-UiFailure $r $c[2] $before $c[3]
        Expect ($r.json.saved -eq $false -and $r.json.applied -eq 0) "$($c[3]): reply $(Short $r.stdout)"
        $seen += $c[3]
    }
    $json = Invoke-UiEdit $U '[{"op":' 'ui-badjson.json'
    Expect-UiFailure $json "aren't valid JSON" $before 'broken JSON' 2
    $none = Invoke-UiEdit $U '[]' 'ui-noops.json'
    Expect-UiFailure $none 'No ops given' $before 'no ops' 2
    "$($seen.Count) failures exit 6 with the fix, broken JSON and no ops 2, files unchanged"
}

Add-Check 'uitk.edit-rollback' $uiRow 'a UXML batch that removes, moves and adds elements, then has one bad op, and a USS batch that removes a rule and a property, then sets a property the importer warns about, save nothing, on disk or in memory; the next good edit saves only its own change' {
    Reset-UiFiles
    $before = Get-UiHash
    $sig0 = Get-UiSig
    $m0 = Get-UiMemory
    Expect ($m0 -eq $sig0) "before: memory and files differ: '$(Short $m0 200)' vs '$(Short $sig0 200)'"
    $bad = '[{"op":"remove","selector":"#status"},{"op":"move","selector":"#start","to":"#footer"},{"op":"add","selector":"#menu","type":"Label","name":"extra"},{"op":"remove_class","selector":"#menu","class":"panel"},{"op":"remove","selector":"#nosuch"}]'
    $r = Invoke-UiEdit $script:uiUxml $bad 'ui-bad-batch.json'
    Expect-UiFailure $r 'Op 4 \(remove\) failed.*Nothing was saved' $before 'bad UXML batch'
    Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 4) "reply: $(Short $r.stdout)"
    $bss = '[{"op":"remove_rule","selector":"#status"},{"op":"remove_property","selector":".panel","properties":"padding"},{"op":"set_rule","selector":".primary","properties":"colr: red"}]'
    $q = Invoke-UiEdit $script:uiUss $bss 'ui-bad-uss.json'
    Expect-UiFailure $q "importer rejected the result.*colr.*Nothing was saved" $before 'USS batch the importer warns about'
    $m1 = Get-UiMemory
    Expect ($m1 -eq $m0) "in memory after the failed batches: '$(Short $m0 150)' -> '$(Short $m1 150)'"
    Expect ((Get-UiSig) -eq $sig0) 'inspect after the failed batches differs'
    $g = Invoke-UiEdit $script:uiUxml '[{"op":"remove","selector":"#player"}]' 'ui-good.json'
    Expect ($g.exit -eq 0 -and $g.json.applied -eq 1) "good UXML edit: exit $($g.exit): $(Short ($g.stdout + $g.stderr))"
    $h = Invoke-UiEdit $script:uiUss '[{"op":"remove_property","selector":".primary","properties":"color"}]' 'ui-good-uss.json'
    Expect ($h.exit -eq 0 -and $h.json.applied -eq 1) "good USS edit: exit $($h.exit): $(Short ($h.stdout + $h.stderr))"
    $want = $sig0.Replace('TextField#player,', '').Replace('.primary{color,background-color}', '.primary{background-color}')
    Expect ($want -ne $sig0 -and $want -notmatch 'player') "the expected change isn't in the signature: $(Short $sig0 300)"
    $disk = Get-UiSig
    Expect ($disk -eq $want) "the files after the good edits: $(Short $disk 300)"
    $m2 = Get-UiMemory
    Expect ($m2 -eq $want) "in memory after the good edits: $(Short $m2 300)"
    Expect (-not ((Get-Content (Join-Path $projectPath ($script:uiUxml -replace '/', '\'))) | Where-Object { $_ -match '^\s+$' })) 'remove left a blank line in the UXML'
    "UXML failed at op 4, USS rejected by the importer; files and memory unchanged; next edits removed only #player and .primary's color"
}

Add-Check 'uitk.edit-mode' $uiRow 'in Edit mode, click and set_value exit 6 naming editor.playmode start; tree and query read the documents with a live panel; a query that matches nothing and an unknown document exit 13 with the fix, a bad selector and depth exit 2' {
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-mode.cs' 'return UnityEditor.EditorApplication.isPlaying ? "playing" : "edit";'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit') "not in Edit mode: $(Short $mode.stdout)"
    foreach ($c in @(@(@('click', '--selector', 'Button'), 'click'), @(@('set_value', '--selector', 'Toggle', '--value', 'true'), 'set_value'))) {
        $r = Invoke-Cli (@('cliext.uitk') + $c[0] + @('--format', 'json'))
        Expect-UiFailure $r "cliext\.uitk $($c[1]) acts on the running game's UI: enter Play mode first \(editor\.playmode start\)" '' "$($c[1]) in Edit mode"
    }
    $t = Invoke-Cli @('cliext.uitk', 'tree', '--document', 'HUD', '--format', 'json')
    Expect ($t.exit -eq 0 -and @($t.json).Count -eq 1 -and $t.json.document -eq 'HUD' -and $t.json.tree.type -eq 'UIDocumentRootElement') "tree --document HUD: exit $($t.exit): $(Short ($t.stdout + $t.stderr))"
    $q = Invoke-Cli @('cliext.uitk', 'query', '--selector', 'UIDocumentRootElement', '--document', 'HUD', '--format', 'json')
    Expect ($q.exit -eq 0 -and $q.json.document -eq 'HUD' -and $q.json.element.name -eq 'HUD-container') "query: exit $($q.exit): $(Short ($q.stdout + $q.stderr))"
    $cases = @(
        @(@('query', '--selector', 'Button', '--document', 'HUD'), "Nothing matches 'Button': no element of type 'Button'.*cliext\.uitk tree --document HUD", 13, 'a query matching nothing'),
        @(@('tree', '--document', 'HDU'), "No active UIDocument on a GameObject named 'HDU'. Did you mean: HUD", 13, 'misspelled document'),
        @(@('query', '--selector', 'Button', '--document', 'Nope'), "No active UIDocument on a GameObject named 'Nope'", 13, 'unknown document'),
        @(@('query', '--selector', '#a >'), 'has a > without an element', 2, 'a selector ending in >'),
        @(@('tree', '--depth', '-1'), '--depth must be 0 or more', 2, 'negative depth')
    )
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli (@('cliext.uitk') + $c[0] + @('--format', 'json'))
        Expect-UiFailure $x $c[1] '' $c[3] $c[2]
        $seen += "$($c[3]) $($x.exit)"
    }
    "click, set_value exit 6 naming editor.playmode start; tree and query read HUD; $($seen -join ', ')"
}

Add-Check 'uitk.play-mode' $uiRow 'in Play mode, on a UIDocument made from the scratch UXML: tree and query show it, click on the Button runs its handler (the Label changes), set_value changes a Toggle (its change event fires), a Slider, a DropdownField, a TextField and a Label; clicks and values that would do nothing or can''t be taken fail with the fix; leaving Play mode leaves a clean scene' {
    Reset-UiFiles
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $setup = @'
var go = new UnityEngine.GameObject("CliTestUI");
var doc = go.AddComponent<UnityEngine.UIElements.UIDocument>();
doc.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>("Assets/TestLevel/UI/TestPanelSettings.asset");
doc.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>("{{uxml}}");
var root = doc.rootVisualElement;
var label = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Label>(root, "status");
int n = 0;
UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Button>(root, "start").clicked += () => { n++; label.text = "clicked " + n; };
UnityEngine.UIElements.INotifyValueChangedExtensions.RegisterValueChangedCallback(UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Toggle>(root, "sound"), e => label.text = "sound " + e.newValue);
return root.panel != null && UnityEditor.EditorApplication.isPlaying ? "ok" : "no panel";
'@
    $readValues = @'
var root = UnityEngine.GameObject.Find("CliTestUI").GetComponent<UnityEngine.UIElements.UIDocument>().rootVisualElement;
return UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Toggle>(root, "sound").value + "|" + UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Slider>(root, "volume").value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    + "|" + UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.DropdownField>(root, "mode").value + "|" + UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.TextField>(root, "player").value;
'@
    $disable = 'var root = UnityEngine.GameObject.Find("CliTestUI").GetComponent<UnityEngine.UIElements.UIDocument>().rootVisualElement; UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Slider>(root, "volume").SetEnabled(false); UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Button>(root, "start").SetEnabled(false); return "ok";'
    $D = @('--document', 'CliTestUI')
    function Get-UiStatus { $x = Invoke-Cli (@('cliext.uitk', 'query', '--selector', '#status') + $D + @('--format', 'json')); Expect ($x.exit -eq 0) "query #status: exit $($x.exit): $(Short ($x.stdout + $x.stderr))"; $x.json.element.text }
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $s = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-play-setup.cs' $setup.Replace('{{uxml}}', $script:uiUxml)), '--format', 'json')
        Expect ($s.exit -eq 0 -and "$($s.stdout)" -match 'ok') "making the UIDocument: $(Short ($s.stdout + $s.stderr))"
        $t = Invoke-Cli (@('cliext.uitk', 'tree', '--depth', '2') + $D + @('--format', 'json'))
        Expect ($t.exit -eq 0 -and (@($t.json.tree.children[0].children | ForEach-Object { "$($_.type)#$($_.name)=$($_.text)" }) -join ',') -eq 'Label#status=idle,Button#start=Start,TextField#player=Ann,Toggle#sound=true,Slider#volume=5,DropdownField#mode=Easy') "tree: exit $($t.exit): $(Short ($t.stdout + $t.stderr) 300)"
        $q = Invoke-Cli (@('cliext.uitk', 'query', '--selector', '#menu > Button.primary') + $D + @('--format', 'json'))
        Expect ($q.exit -eq 0 -and @($q.json).Count -eq 1 -and $q.json.path -match 'VisualElement#menu > Button#start$' -and $q.json.element.rect[2] -gt 0) "query: exit $($q.exit): $(Short ($q.stdout + $q.stderr))"

        $c = Invoke-Cli (@('cliext.uitk', 'click', '--selector', '#start') + $D + @('--format', 'json'))
        Expect ($c.exit -eq 0 -and $c.json.clicked -match 'Button#start$') "click: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
        Expect ((Get-UiStatus) -eq 'clicked 1') "after one click the label reads '$(Get-UiStatus)', not 'clicked 1'"
        $v = Invoke-Cli (@('cliext.uitk', 'set_value', '--selector', '#sound', '--value', 'false') + $D + @('--format', 'json'))
        Expect ($v.exit -eq 0 -and $v.json.before -eq 'true' -and $v.json.value -eq 'false') "set_value Toggle: exit $($v.exit): $(Short ($v.stdout + $v.stderr))"
        Expect ((Get-UiStatus) -eq 'sound False') "the Toggle's change event didn't run: the label reads '$(Get-UiStatus)'"
        foreach ($sv in @(@('Slider', '7.5'), @('#mode', 'Hard'), @('TextField#player', 'Bob'))) {
            $x = Invoke-Cli (@('cliext.uitk', 'set_value', '--selector', $sv[0], '--value', $sv[1]) + $D + @('--format', 'json'))
            Expect ($x.exit -eq 0 -and $x.json.value -eq $sv[1]) "set_value $($sv[0]): exit $($x.exit): $(Short ($x.stdout + $x.stderr))"
        }
        $l = Invoke-Cli (@('cliext.uitk', 'set_value', '--selector', '#status', '--value', 'ready') + $D + @('--format', 'json'))
        Expect ($l.exit -eq 0 -and (Get-UiStatus) -eq 'ready') "set_value Label: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
        $vals = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-read.cs' $readValues), '--format', 'json')
        Expect ("$($vals.stdout)" -match '"False\|7\.5\|Hard\|Bob"') "the fields hold: $(Short ($vals.stdout + $vals.stderr))"

        $cases = @(
            @(@('click', '--selector', '#status'), "isn't a Button and has no ClickEvent callback, so a click would do nothing", 6, 'click on a Label with no handler'),
            @(@('click', '--selector', '#strat'), "no element named 'strat'. Did you mean: #start\?.*cliext\.uitk tree --document CliTestUI", 13, 'click on an unknown name'),
            @(@('set_value', '--selector', '#menu', '--value', 'x'), 'has no value to set\. set_value takes a field', 2, 'set_value on a VisualElement'),
            @(@('set_value', '--selector', '#start', '--value', 'x'), 'has no value to set\. Use cliext\.uitk click for a Button', 2, 'set_value on a Button'),
            @(@('set_value', '--selector', 'Slider', '--value', 'abc'), "'abc' isn't a Single value: give a number", 2, 'a bad number'),
            @(@('set_value', '--selector', 'Toggle', '--value', 'maybe'), "'maybe' isn't a Boolean value: give true or false", 2, 'a bad bool'),
            @(@('set_value', '--selector', 'Slider', '--value', '99'), "99 is outside Slider#volume's range 0 to 10", 2, 'out of a slider''s range'),
            @(@('set_value', '--selector', '#mode', '--value', 'Hrd'), "'Hrd' isn't one of DropdownField#mode's choices. Did you mean: Hard", 2, 'not a dropdown choice'),
            @(@('set_value', '--selector', 'Toggle', '--value', 'false'), 'is already false: nothing changed', 6, 'the value it already has'),
            @(@('set_value', '--selector', 'Toggle', '--value', 'true', '--document', 'CliTestU'), 'Did you mean: CliTestUI', 13, 'misspelled document'),
            @(@('query', '--selector', 'Slider.big'), "no element with the class 'big'", 13, 'a query matching nothing'),
            @(@('query', '--selector', 'Button:hover'), "Pseudo-classes such as ':hover' aren't supported", 2, 'a pseudo-class')
        )
        $seen = @()
        foreach ($k in $cases) {
            $args1 = @('cliext.uitk') + $k[0]
            if (($k[0] -join ' ') -notmatch '--document') { $args1 += $D }
            $x = Invoke-Cli ($args1 + @('--format', 'json'))
            Expect-UiFailure $x $k[1] '' $k[3] $k[2]
            $seen += "$($k[3]) $($x.exit)"
        }
        Expect ((Get-UiStatus) -eq 'ready') "a failed call changed the label: '$(Get-UiStatus)'"
        $vals2 = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-read2.cs' $readValues), '--format', 'json')
        Expect ($vals2.stdout -eq $vals.stdout) "a failed call changed a field: $(Short $vals2.stdout)"
        # Every field has the class unity-base-field: an ambiguous selector sets the first (TextField#player) and says so.
        $amb = Invoke-Cli (@('cliext.uitk', 'set_value', '--selector', '.unity-base-field', '--value', 'Cid') + $D + @('--format', 'json'))
        Expect ($amb.exit -eq 0 -and $amb.json.matches -ge 4 -and $amb.json.element -match 'TextField#player$') "set_value on an ambiguous selector: exit $($amb.exit): $(Short ($amb.stdout + $amb.stderr))"
        Expect ("$($amb.stderr)" -match "matched $($amb.json.matches) elements; only the first was set: .*TextField#player; .*Toggle#sound") "no warning naming the matches: $(Short $amb.stderr 300)"
        $one = Invoke-Cli (@('cliext.uitk', 'set_value', '--selector', '#player', '--value', 'Dee') + $D + @('--format', 'json'))
        Expect ($one.exit -eq 0 -and $one.json.matches -eq 1 -and "$($one.stderr)" -notmatch 'matched') "set_value on one match: exit $($one.exit): $(Short ($one.stdout + $one.stderr))"
        [void](Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-disable.cs' $disable), '--format', 'json'))
        $dc = Invoke-Cli (@('cliext.uitk', 'click', '--selector', '#start') + $D + @('--format', 'json'))
        Expect-UiFailure $dc "is disabled: a player can't click it" '' 'click on a disabled Button'
        $dv = Invoke-Cli (@('cliext.uitk', 'set_value', '--selector', 'Slider', '--value', '2') + $D + @('--format', 'json'))
        Expect-UiFailure $dv "is disabled: a player can't change it" '' 'set_value on a disabled Slider'
        Expect ((Get-UiStatus) -eq 'ready') "a disabled Button's click ran: '$(Get-UiStatus)'"
    }
    finally {
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
    }
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'ui-mode2.cs' 'var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return (UnityEditor.EditorApplication.isPlaying ? "playing" : "edit") + "|" + a.isDirty + "|" + (UnityEngine.GameObject.Find("CliTestUI") == null);'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit\|False\|True') "after Play mode: $(Short $mode.stdout)"
    "tree and query; click ran the handler; Toggle, Slider, DropdownField, TextField and Label set and read back; $($seen.Count + 2) failures with the fix; back in Edit mode, scene clean"
}

# --- cliext.cinemachine -----------------------------------------------------------------------------------------------

# Edit mode checks run in a new untitled scene (Open-P3Scene: a tagged Main Camera with no brain, CliTestPlayer and
# CliTestPlain), made with the CLI's own create, then discarded by Close-P3Scene, which reopens the scene that was open
# from disk. The Play mode check uses that saved scene and makes its cameras in Play mode, so nothing is saved.
$cmRow = 'cliext.cinemachine'

function Open-P3Scene {
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $code = @'
string before = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
var cam = new UnityEngine.GameObject("Main Camera"); cam.tag = "MainCamera"; cam.AddComponent<UnityEngine.Camera>(); cam.transform.position = new UnityEngine.Vector3(0, 1, -10);
new UnityEngine.GameObject("CliTestPlayer").transform.position = new UnityEngine.Vector3(0, 0, 5);
new UnityEngine.GameObject("CliTestPlain");
return "ok|" + before;
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'p3-open.cs' $code), '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok\|(Assets/[^"]+\.unity)') "scratch scene: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $Matches[1]
}
function Close-P3Scene([string]$path) {
    # Reopens the scene that was open from disk, discarding the untitled scratch scene; returns path|dirty|count.
    $code = "UnityEditor.SceneManagement.EditorSceneManager.OpenScene(`"$path`", UnityEditor.SceneManagement.OpenSceneMode.Single); var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return a.path + `"|`" + a.isDirty + `"|`" + UnityEngine.SceneManagement.SceneManager.sceneCount;"
    (Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'p3-close.cs' $code), '--format', 'json')).stdout
}
function Expect-P3Failure($r, [string]$hint, [string]$what, [int]$exit = 6) {
    Expect ($r.exit -ne 124) "${what}: timed out"
    Expect ($r.exit -eq $exit) "${what}: exit $($r.exit), expected ${exit}: $(Short ($r.stdout + $r.stderr))"
    Expect ("$($r.stderr)" -match $hint) "${what}: the error doesn't say '$hint': $(Short $r.stderr)"
    Expect ("$($r.stderr)" -notmatch '   at |Exception:') "${what}: a stack trace instead of a message: $(Short $r.stderr)"
}
function Get-CmState {
    # Every CinemachineCamera's fields as the Editor holds them, the brains' blends and the scene's object count.
    $code = @'
var ci = System.Globalization.CultureInfo.InvariantCulture;
var cams = UnityEngine.Object.FindObjectsByType<Unity.Cinemachine.CinemachineCamera>(UnityEngine.FindObjectsInactive.Include).OrderBy(c => c.name)
    .Select(c => c.name + ":p" + c.Priority.Value + ":f" + (c.Target.TrackingTarget == null ? "-" : c.Target.TrackingTarget.name)
        + ":l" + (c.Target.CustomLookAtTarget && c.Target.LookAtTarget != null ? c.Target.LookAtTarget.name : "-")
        + ":fov" + c.Lens.FieldOfView.ToString(ci) + ":" + (c.enabled ? "on" : "off")
        + ":[" + string.Join(",", c.GetComponents<Unity.Cinemachine.CinemachineComponentBase>().Select(x => x.GetType().Name)) + "]");
var brains = UnityEngine.Object.FindObjectsByType<Unity.Cinemachine.CinemachineBrain>(UnityEngine.FindObjectsInactive.Include)
    .Select(b => b.name + ":" + b.DefaultBlend.Style + " " + b.DefaultBlend.Time.ToString(ci) + ":" + (b.CustomBlends == null ? "-" : UnityEditor.AssetDatabase.GetAssetPath(b.CustomBlends) + "{" + string.Join(",", (b.CustomBlends.CustomBlends ?? new Unity.Cinemachine.CinemachineBlenderSettings.CustomBlend[0]).Select(x => x.From + ">" + x.To + "=" + x.Blend.Style + " " + x.Blend.Time.ToString(ci))) + "}"));
return string.Join(";", cams) + "|brains " + string.Join(";", brains) + "|objects " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount;
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'cm-state.cs' $code), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}
function New-CmCameras {
    # CliTestCamA (follow rig on CliTestPlayer, priority 20, fov 50) and CliTestCamB (lookat rig, priority 10).
    $a = Invoke-Cli @('cliext.cinemachine', 'create', '--name', 'CliTestCamA', '--rig', 'follow', '--follow', 'CliTestPlayer', '--priority', '20', '--fov', '50', '--position', '[0,2,-5]', '--format', 'json')
    Expect ($a.exit -eq 0) "create CliTestCamA: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
    $b = Invoke-Cli @('cliext.cinemachine', 'create', '--name', 'CliTestCamB', '--rig', 'lookat', '--lookat', 'CliTestPlayer', '--priority', '10', '--format', 'json')
    Expect ($b.exit -eq 0) "create CliTestCamB: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
    , @($a, $b)
}

Add-Check 'cm.create-list' $cmRow 'in an unsaved scratch scene, cliext.cinemachine create makes a follow and a lookat camera and adds a brain to the Main Camera; list and the cameras'' fields show them, and in Edit mode list says live and blending need Play mode' {
    $orig = Open-P3Scene
    try {
        $made = New-CmCameras
        Expect ($made[0].json.brainAdded -eq 'Main Camera' -and -not $made[1].json.brainAdded) "brainAdded: '$($made[0].json.brainAdded)', then '$($made[1].json.brainAdded)'"
        Expect ($made[0].json.created.follow -eq 'CliTestPlayer' -and $made[1].json.created.lookAt -eq 'CliTestPlayer') "reply targets: $(Short $made[0].stdout)"
        $l = Invoke-Cli @('cliext.cinemachine', 'list', '--format', 'json')
        Expect ($l.exit -eq 0) "list: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
        $cams = @($l.json.cameras)
        Expect ((@($cams | ForEach-Object { "$($_.name)@$($_.priority)" }) -join ',') -eq 'CliTestCamA@20,CliTestCamB@10') "list cameras: $(Short ($cams | ConvertTo-Json -Compress))"
        Expect ((@($cams[0].components) -join ',') -eq 'CinemachineFollow,CinemachineRotationComposer' -and $cams[0].fov -eq 50 -and $cams[0].follow -eq 'CliTestPlayer') "CliTestCamA: $(Short ($cams[0] | ConvertTo-Json -Compress))"
        Expect ((@($cams[1].components) -join ',') -eq 'CinemachineHardLookAt' -and $cams[1].lookAt -eq 'CliTestPlayer') "CliTestCamB: $(Short ($cams[1] | ConvertTo-Json -Compress))"
        $brain = @($l.json.brains)
        Expect ($brain.Count -eq 1 -and $brain[0].camera -eq 'Main Camera' -and $null -eq $brain[0].live -and $null -eq $cams[0].live) "brains: $(Short ($brain | ConvertTo-Json -Compress))"
        Expect ($l.json.playing -eq $false -and "$($l.json.note)" -match 'Play mode.*editor\.playmode start') "the Edit mode note: '$($l.json.note)'"
        $s = Get-CmState
        Expect ($s -match '^CliTestCamA:p20:fCliTestPlayer:l-:fov50:on:\[CinemachineFollow,CinemachineRotationComposer\];CliTestCamB:p10:f-:lCliTestPlayer:fov40:on:\[CinemachineHardLookAt\]\|brains Main Camera:') "in the Editor: $s"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "2 cameras and a brain; list shows priorities, targets, rigs, and the Edit mode note; scratch scene discarded"
}

Add-Check 'cm.set' $cmRow 'cliext.cinemachine set changes a camera''s priority, follow target, look-at (and clears it with none), fov and enabled; the cameras'' fields and list show it, and only what differed is reported changed' {
    $orig = Open-P3Scene
    try {
        [void](New-CmCameras)
        $r = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestCamB', '--priority', '30', '--follow', 'CliTestPlain', '--fov', '35', '--format', 'json')
        Expect ($r.exit -eq 0 -and (@($r.json.changed) -join ',') -eq 'priority,follow,fov') "set: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
        $c = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestCamB', '--lookat', 'none', '--enabled', 'false', '--priority', '30', '--format', 'json')
        Expect ($c.exit -eq 0 -and (@($c.json.changed) -join ',') -eq 'lookAt,enabled') "clear look-at, disable: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
        $s = Get-CmState
        Expect ($s -match 'CliTestCamB:p30:fCliTestPlain:l-:fov35:off:') "in the Editor: $s"
        $l = Invoke-Cli @('cliext.cinemachine', 'list', '--format', 'json')
        $b = @($l.json.cameras) | Where-Object { $_.name -eq 'CliTestCamB' }
        Expect ($b.priority -eq 30 -and $b.follow -eq 'CliTestPlain' -and $null -eq $b.lookAt -and $b.fov -eq 35 -and $b.enabled -eq $false) "list: $(Short ($b | ConvertTo-Json -Compress))"
        # CamB has the lookat rig (no body): a follow target alone doesn't move it, and set says so; --rig follow adds one.
        Expect ((@($r.json.warnings) -join ' ') -match 'CliTestCamB has no position \(body\) component.*--rig follow') "a follow target on a camera with no body: warnings $(Short ($r.json.warnings -join ' | '))"
        $g = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestCamB', '--rig', 'follow', '--format', 'json')
        Expect ($g.exit -eq 0 -and (@($g.json.changed) -join ',') -eq 'rig' -and (@($g.json.camera.components) -join ',') -eq 'CinemachineFollow,CinemachineRotationComposer' -and $null -eq $g.json.warnings) "set --rig follow: exit $($g.exit): $(Short ($g.stdout + $g.stderr))"
        $g2 = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestCamB', '--rig', 'follow', '--format', 'json')
        Expect-P3Failure $g2 'already has rig follow: nothing would change' 'the same rig again' 6
        $g3 = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestCamB', '--rig', 'orbitl', '--format', 'json')
        Expect-P3Failure $g3 "Unknown rig 'orbitl'\. Did you mean: orbital\?" 'a misspelled rig' 2
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "priority 30, follow CliTestPlain, fov 35, look-at cleared, disabled; no-body warning; --rig follow replaced HardLookAt; $s"
}

Add-Check 'cm.blend' $cmRow 'cliext.cinemachine blend sets the brain''s default blend and adds a custom blend between two cameras in a new Custom Blends asset; the brain and list show both' {
    $orig = Open-P3Scene
    $asset = "$scratchRel/CliTestBlends.asset"
    try {
        [void](New-CmCameras)
        $mk = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'cm-folder.cs' 'if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests")) UnityEditor.AssetDatabase.CreateFolder("Assets", "_CliextTests"); if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests/CliTest")) UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests", "CliTest"); return "ok";'), '--format', 'json')
        Expect ($mk.exit -eq 0) "scratch folder: $(Short ($mk.stdout + $mk.stderr))"
        $d = Invoke-Cli @('cliext.cinemachine', 'blend', '--style', 'Linear', '--time', '0.5', '--format', 'json')
        Expect ($d.exit -eq 0 -and $d.json.defaultBlend -eq 'Linear 0.5s') "default blend: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        $c = Invoke-Cli @('cliext.cinemachine', 'blend', '--style', 'EaseIn', '--time', '1.5', '--from', 'CliTestCamA', '--to', 'CliTestCamB', '--asset', $asset, '--format', 'json')
        Expect ($c.exit -eq 0 -and $c.json.created -eq $true -and $c.json.asset -eq $asset -and $c.json.count -eq 1) "custom blend: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
        $any = Invoke-Cli @('cliext.cinemachine', 'blend', '--style', 'Cut', '--to', 'CliTestCamA', '--format', 'json')
        Expect ($any.exit -eq 0 -and $any.json.from -eq 'any' -and $any.json.count -eq 2 -and $any.json.created -eq $false) "any -> CamA: exit $($any.exit): $(Short ($any.stdout + $any.stderr))"
        Expect (Test-Path (Join-Path $projectPath ($asset -replace '/', '\'))) 'the Custom Blends asset is not on disk'
        $s = Get-CmState
        Expect ($s -match "\|brains Main Camera:Linear 0\.5:$([regex]::Escape($asset))\{CliTestCamA>CliTestCamB=EaseIn 1\.5,\*\*ANY CAMERA\*\*>CliTestCamA=Cut 1\}") "the brain: $s"
        $l = Invoke-Cli @('cliext.cinemachine', 'list', '--format', 'json')
        $b = @($l.json.brains)[0]
        Expect ($b.defaultBlend -eq 'Linear 0.5s' -and $b.customBlends -eq $asset -and (@($b.custom) -join ';') -eq 'CliTestCamA -> CliTestCamB: EaseIn 1.5s;any -> CliTestCamA: Cut 1s') "list brain: $(Short ($b | ConvertTo-Json -Compress))"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "default Linear 0.5s; custom blends A->B EaseIn 1.5s and any->A Cut in a new asset; scratch scene discarded"
}

Add-Check 'cm.errors' $cmRow 'cliext.cinemachine create, set and blend with unknown cameras and targets, bad rigs, styles, numbers and asset paths, no brain, and changes that would do nothing exit 13, 2 or 6 with the fix, and change and create nothing' {
    $orig = Open-P3Scene
    $seen = @()
    try {
        # Before any camera or brain exists.
        $nb = Invoke-Cli @('cliext.cinemachine', 'blend', '--style', 'Linear', '--format', 'json')
        Expect-P3Failure $nb 'No CinemachineBrain.*cliext\.cinemachine create adds one' 'blend with no brain' 13
        $nc = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestCamA', '--priority', '5', '--format', 'json')
        Expect-P3Failure $nc 'the open scenes have none\. cliext\.cinemachine create makes one' 'set with no cameras' 13
        [void](New-CmCameras)
        $mk = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'cm-folder2.cs' 'if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests")) UnityEditor.AssetDatabase.CreateFolder("Assets", "_CliextTests"); if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests/CliTest")) UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests", "CliTest"); return "ok";'), '--format', 'json')
        Expect ($mk.exit -eq 0) "scratch folder: $(Short ($mk.stdout + $mk.stderr))"
        $before = Get-CmState
        $cases = @(
            @(@('create', '--name', 'CliTestBad', '--rig', 'orbitl'), 'Did you mean: orbital.*Use one of: follow, orbital', 2, 'create with a misspelled rig'),
            @(@('create', '--name', 'CliTestBad', '--follow', 'CliTestPlayr'), "No GameObject 'CliTestPlayr'.*Did you mean: CliTestPlayer.*scene get_hierarchy", 13, 'create following an unknown object'),
            @(@('create', '--name', 'CliTestBad', '--lookat', 'NoSuchTarget'), "No GameObject 'NoSuchTarget'", 13, 'create looking at an unknown object'),
            @(@('create', '--name', 'CliTestBad', '--position', '[1,x]'), "--position: '\[1,x\]' isn't a Vector3 value", 2, 'create at a bad position'),
            @(@('create', '--name', 'CliTestBad', '--fov', '200'), 'more than 0 and less than 180', 2, 'create with fov 200'),
            @(@('create', '--name', 'CliTestCamA'), "already exists: change it with cliext\.cinemachine set --camera CliTestCamA", 6, 'create a camera that exists'),
            @(@('set', '--camera', 'CliTestCamZ', '--priority', '5'), "No CinemachineCamera 'CliTestCamZ'.*Did you mean: CliTestCam", 13, 'set an unknown camera'),
            @(@('set', '--camera', 'CliTestPlain', '--priority', '5'), "'CliTestPlain' has no CinemachineCamera.*cliext\.cinemachine list", 13, 'set an object without a camera'),
            @(@('set', '--camera', 'CliTestCamA', '--priority', '99', '--follow', 'NoSuchTarget'), "No GameObject 'NoSuchTarget'", 13, 'set a priority and an unknown target'),
            @(@('set', '--camera', 'CliTestCamA'), 'Nothing to change on CliTestCamA: pass --priority', 2, 'set with nothing to change'),
            @(@('set', '--camera', 'CliTestCamA', '--priority', '20', '--follow', 'CliTestPlayer'), 'already has priority 20, follow CliTestPlayer: nothing would change', 6, 'set to the values it has'),
            @(@('set', '--camera', 'CliTestCamA', '--fov', '0'), 'more than 0 and less than 180', 2, 'set fov 0'),
            @(@('set', '--camera', 'CliTestCamA', '--enabled', 'maybe'), 'expects Boolean', 2, 'set enabled to a non-bool'),
            @(@('blend', '--style', 'Smooth'), "'Smooth' isn't a blend style\. Use one of: Cut, EaseInOut", 2, 'unknown blend style'),
            @(@('blend', '--style', '3'), "'3' isn't a blend style", 2, 'blend style as a number'),
            @(@('blend', '--style', 'Linear', '--time', '-1'), '--time -1 isn''t a duration', 2, 'negative blend time'),
            @(@('blend', '--style', 'EaseInOut', '--time', '2'), 'already EaseInOut 2s: nothing would change', 6, 'the default blend it has'),
            @(@('blend', '--style', 'Linear', '--asset', "$scratchRel/X.asset"), '--asset is only for a custom blend', 2, '--asset without --from or --to'),
            @(@('blend', '--style', 'Linear', '--from', 'CliTestCamZ'), "No CinemachineCamera 'CliTestCamZ'.*Did you mean", 13, 'custom blend from an unknown camera'),
            @(@('blend', '--style', 'Linear', '--from', 'CliTestCamA', '--to', 'CliTestCamA'), 'both CliTestCamA', 2, 'custom blend from a camera to itself'),
            @(@('blend', '--style', 'Linear', '--from', 'CliTestCamA', '--to', 'CliTestCamB'), 'pass --asset Assets/', 2, 'custom blend with no asset'),
            @(@('blend', '--style', 'Linear', '--from', 'CliTestCamA', '--asset', 'C:/CliTestBlends.asset'), "isn't under Assets/ or Packages/", 2, 'asset outside Assets'),
            @(@('blend', '--style', 'Linear', '--from', 'CliTestCamA', '--asset', "$scratchRel/CliTestBlends.txt"), 'must end in \.asset', 2, 'asset not ending .asset'),
            @(@('blend', '--style', 'Linear', '--from', 'CliTestCamA', '--asset', "$scratchRel/NoSuchFolder/B.asset"), "No folder '.*NoSuchFolder' for --asset: create it first", 13, 'asset in a missing folder')
        )
        foreach ($c in $cases) {
            $x = Invoke-Cli (@('cliext.cinemachine') + $c[0] + @('--format', 'json'))
            Expect-P3Failure $x $c[1] $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        $after = Get-CmState
        Expect ($after -eq $before) "a failed call changed the scene: '$before' -> '$after'"
        Expect (-not (Test-Path (Join-Path $scratch 'NoSuchFolder')) -and -not (Test-Path 'C:\CliTestBlends.asset') -and -not (Test-Path (Join-Path $scratch 'CliTestBlends.txt'))) 'a failed blend created a file'
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "$($seen.Count + 2) failures with the fix, cameras, brain and objects unchanged"
}

Add-Check 'cm.play-mode' $cmRow 'in Play mode, cameras made with create go live by priority: list names the live camera, a set priority starts a blend that list shows (from, to, weight), and blend changes the default; leaving Play mode leaves the scene clean' {
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $a = Invoke-Cli @('cliext.cinemachine', 'create', '--name', 'CliTestPlayA', '--rig', 'fixed', '--priority', '100', '--position', '[0,3,-8]', '--format', 'json')
        Expect ($a.exit -eq 0) "create CliTestPlayA: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        $b = Invoke-Cli @('cliext.cinemachine', 'create', '--name', 'CliTestPlayB', '--rig', 'fixed', '--priority', '1', '--position', '[8,3,0]', '--format', 'json')
        Expect ($b.exit -eq 0) "create CliTestPlayB: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
        $bl = Invoke-Cli @('cliext.cinemachine', 'blend', '--style', 'Linear', '--time', '30', '--format', 'json')
        Expect ($bl.exit -eq 0 -and $bl.json.defaultBlend -eq 'Linear 30s') "blend in Play mode: exit $($bl.exit): $(Short ($bl.stdout + $bl.stderr))"
        $l = Invoke-Cli @('cliext.cinemachine', 'list', '--format', 'json')
        $brain = @($l.json.brains)[0]
        Expect ($l.json.playing -eq $true -and $null -eq $l.json.note -and $brain.live -eq 'CliTestPlayA' -and $brain.defaultBlend -eq 'Linear 30s') "list before the switch: $(Short ($l.json | ConvertTo-Json -Compress -Depth 4) 300)"
        Expect ((@($l.json.cameras) | Where-Object { $_.name -eq 'CliTestPlayA' }).live -eq $true) 'CliTestPlayA is not live in the camera list'
        $s = Invoke-Cli @('cliext.cinemachine', 'set', '--camera', 'CliTestPlayB', '--priority', '200', '--format', 'json')
        Expect ($s.exit -eq 0) "set priority: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $l2 = Invoke-Cli @('cliext.cinemachine', 'list', '--format', 'json')
        $brain2 = @($l2.json.brains)[0]
        Expect ($brain2.live -eq 'CliTestPlayB' -and $brain2.blending.from -eq 'CliTestPlayA' -and $brain2.blending.to -eq 'CliTestPlayB' -and $brain2.blending.duration -eq 30 -and $brain2.blending.weight -lt 1) "list after the switch: $(Short ($brain2 | ConvertTo-Json -Compress -Depth 4) 300)"
    }
    finally {
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
    }
    $mode = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'cm-mode.cs' 'var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return (UnityEditor.EditorApplication.isPlaying ? "playing" : "edit") + "|" + a.isDirty + "|" + (UnityEngine.GameObject.Find("CliTestPlayA") == null);'), '--format', 'json')
    Expect ("$($mode.stdout)" -match 'edit\|False\|True') "after Play mode: $(Short $mode.stdout)"
    "live CliTestPlayA, then blending to CliTestPlayB (weight $($brain2.blending.weight), Linear 30s); back in Edit mode, scene clean"
}

# --- cliext.splines ---------------------------------------------------------------------------------------------------

# Splines are made with the CLI's create in the untitled scene of Open-P3Scene and discarded with it. Reads go through
# inspect and a snippet of the knots as the Editor holds them; Undo is driven from snippets (Undo.PerformUndo/Redo).
$splRow = 'cliext.splines'

function Get-SplKnots([string]$name = 'CliTestPath') {
    $code = @'
var ci = System.Globalization.CultureInfo.InvariantCulture;
var c = UnityEngine.Object.FindObjectsByType<UnityEngine.Splines.SplineContainer>(UnityEngine.FindObjectsInactive.Include).FirstOrDefault(x => x.name == "{{name}}");
if (c == null) return "none";
return string.Join("|", c.Splines.Select(s => (s.Closed ? "closed:" : "open:") + string.Join(";", Enumerable.Range(0, s.Count).Select(i =>
    s[i].Position.x.ToString("0.###", ci) + "," + s[i].Position.y.ToString("0.###", ci) + "," + s[i].Position.z.ToString("0.###", ci) + "/" + s.GetTangentMode(i)))));
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'spl-knots.cs' $code.Replace('{{name}}', $name)), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}
function Invoke-SplUndo([string]$what = 'Undo') {
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile "spl-$what.cs" "UnityEditor.Undo.Perform$what(); return `"ok`";"), '--format', 'json')
    Expect ($r.exit -eq 0) "${what}: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
}
function New-SplPath {
    $r = Invoke-Cli @('cliext.splines', 'create', '--name', 'CliTestPath', '--knots', '[0,0,0];[4,0,0];[4,0,4]', '--mode', 'Linear', '--position', '[1,0,0]', '--format', 'json')
    Expect ($r.exit -eq 0) "create: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r
}

Add-Check 'spl.create-inspect-list' $splRow 'in an unsaved scratch scene, cliext.splines create makes a SplineContainer through three knots at an offset; inspect shows local and world knots and modes, list its knot count and length' {
    $orig = Open-P3Scene
    try {
        $c = New-SplPath
        Expect ($c.json.knots -eq 3 -and $c.json.length -eq 8) "create reply: $(Short $c.stdout)"
        $i = Invoke-Cli @('cliext.splines', 'inspect', '--object', 'CliTestPath', '--format', 'json')
        Expect ($i.exit -eq 0) "inspect: exit $($i.exit): $(Short ($i.stdout + $i.stderr))"
        $pts = @($i.json.splines[0].points)
        Expect ((@($pts | ForEach-Object { "$($_.position)>$($_.world)/$($_.mode)" }) -join ' ') -eq '[0,0,0]>[1,0,0]/Linear [4,0,0]>[5,0,0]/Linear [4,0,4]>[5,0,4]/Linear') "inspect knots: $(Short ($pts | ConvertTo-Json -Compress))"
        $l = Invoke-Cli @('cliext.splines', 'list', '--format', 'json')
        $rec = @($l.json) | Where-Object { $_.name -eq 'CliTestPath' }
        Expect ($l.exit -eq 0 -and $rec -and $rec.splines[0].knots -eq 3 -and $rec.splines[0].closed -eq $false -and $rec.splines[0].length -eq 8) "list: exit $($l.exit): $(Short $l.stdout)"
        $k = Get-SplKnots
        Expect ($k -eq 'open:0,0,0/Linear;4,0,0/Linear;4,0,4/Linear') "in the Editor: $k"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "3 Linear knots, world offset [1,0,0], length 8; $k"
}

Add-Check 'spl.edit' $splRow 'cliext.splines edit adds, moves (in world space) and removes knots, closes the loop, changes tangent modes and adds a spline in one call; inspect and the knots show it, and one Undo takes the whole batch back' {
    $orig = Open-P3Scene
    try {
        [void](New-SplPath)
        $k0 = Get-SplKnots
        $ops = '[{"op":"add_knot","position":"[0,0,4]"},{"op":"set_knot","index":1,"position":"[5,2,0]","world":true},{"op":"add_knot","index":1,"position":"[2,0,0]","mode":"Broken"},{"op":"remove_knot","index":4},{"op":"set_closed","value":"true"},{"op":"set_tangent_mode","mode":"Continuous","index":0},{"op":"add_spline","knots":"[0,1,0];[0,2,0]","mode":"Linear"}]'
        $r = Invoke-Cli @('cliext.splines', 'edit', '--object', 'CliTestPath', '--ops-file', (Write-WorkFile 'spl-edit.json' $ops), '--format', 'json')
        Expect ($r.exit -eq 0 -and $r.json.applied -eq 7 -and @($r.json.splines).Count -eq 2) "edit: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
        $k = Get-SplKnots
        Expect ($k -eq 'closed:0,0,0/Continuous;2,0,0/Broken;4,2,0/Linear;4,0,4/Linear|open:0,1,0/Linear;0,2,0/Linear') "in the Editor: $k"
        $i = Invoke-Cli @('cliext.splines', 'inspect', '--object', 'CliTestPath', '--format', 'json')
        Expect ($i.json.splines[0].closed -eq $true -and $i.json.splines[0].points[2].world -eq '[5,2,0]' -and $i.json.splines[1].knots -eq 2) "inspect: $(Short $i.stdout)"
        Invoke-SplUndo 'Undo'
        $ku = Get-SplKnots
        Expect ($ku -eq $k0) "one Undo: expected '$k0', got '$ku'"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "7 ops in one call ($k); one Undo back to $k0"
}

Add-Check 'spl.sample' $splRow 'cliext.splines sample returns the world position and tangent at --t and --distance, the nearest point to --near with its distance, and --count evenly spaced samples' {
    $orig = Open-P3Scene
    try {
        $c = Invoke-Cli @('cliext.splines', 'create', '--name', 'CliTestLine', '--knots', '[0,0,0];[4,0,0]', '--mode', 'Linear', '--position', '[1,0,0]', '--format', 'json')
        Expect ($c.exit -eq 0) "create: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
        $t = Invoke-Cli @('cliext.splines', 'sample', '--object', 'CliTestLine', '--t', '0.5', '--format', 'json')
        Expect ($t.exit -eq 0 -and $t.json.position -eq '[3,0,0]' -and $t.json.tangent -eq '[1,0,0]') "t 0.5: exit $($t.exit): $(Short ($t.stdout + $t.stderr))"
        $d = Invoke-Cli @('cliext.splines', 'sample', '--object', 'CliTestLine', '--distance', '1', '--format', 'json')
        $dx = if ($d.json.position) { [double](($d.json.position.Trim('[]') -split ',')[0]) } else { -1 }
        Expect ($d.exit -eq 0 -and [math]::Abs($dx - 2) -lt 0.01 -and $d.json.t -eq 0.25) "distance 1: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        $n = Invoke-Cli @('cliext.splines', 'sample', '--object', 'CliTestLine', '--near', '[3,5,0]', '--format', 'json')
        Expect ($n.exit -eq 0 -and $n.json.nearest -eq '[3,0,0]' -and $n.json.distance -eq 5 -and $n.json.t -eq 0.5) "near: exit $($n.exit): $(Short ($n.stdout + $n.stderr))"
        $k = Invoke-Cli @('cliext.splines', 'sample', '--object', 'CliTestLine', '--count', '3', '--format', 'json')
        Expect ($k.exit -eq 0 -and (@($k.json | ForEach-Object { $_.position }) -join ' ') -eq '[1,0,0] [3,0,0] [5,0,0]') "count 3: exit $($k.exit): $(Short ($k.stdout + $k.stderr))"
        # A closed spline: --count N is t = k/N, N different points (t = 1 would be the start again).
        $sq = Invoke-Cli @('cliext.splines', 'create', '--name', 'CliTestSquare', '--knots', '[0,0,0];[4,0,0];[4,0,4];[0,0,4]', '--mode', 'Linear', '--closed', 'true', '--format', 'json')
        Expect ($sq.exit -eq 0) "create closed: exit $($sq.exit): $(Short ($sq.stdout + $sq.stderr))"
        $q = Invoke-Cli @('cliext.splines', 'sample', '--object', 'CliTestSquare', '--count', '4', '--format', 'json')
        Expect ($q.exit -eq 0 -and (@($q.json | ForEach-Object { ([double]$_.t).ToString([Globalization.CultureInfo]::InvariantCulture) }) -join ' ') -eq '0 0.25 0.5 0.75' -and (@($q.json | ForEach-Object { $_.position }) -join ' ') -eq '[0,0,0] [4,0,0] [4,0,4] [0,0,4]') "closed count 4: exit $($q.exit): $(Short ($q.stdout + $q.stderr))"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    't 0.5 [3,0,0], distance 1 [2,0,0], near [3,5,0] -> [3,0,0] at 5, count 3 [1..5]; closed square count 4 = its 4 corners at t k/4'
}

Add-Check 'spl.errors' $splRow 'cliext.splines inspect, sample, create and edit with unknown objects and splines, objects without a container, out-of-range t and distance, bad vectors, modes and knots, duplicate names and bad JSON exit 13, 2 or 6 with the fix; edit ops that fail (knot out of range, unknown op, bad mode or value, a change that does nothing) exit 6 and change nothing' {
    $orig = Open-P3Scene
    $seen = @()
    try {
        $none = Invoke-Cli @('cliext.splines', 'inspect', '--object', 'CliTestPath', '--format', 'json')
        Expect-P3Failure $none 'the open scenes have none\. cliext\.splines create makes one' 'inspect with no containers' 13
        [void](New-SplPath)
        $k0 = Get-SplKnots
        $cases = @(
            @(@('inspect', '--object', 'CliTestPth'), "No SplineContainer on 'CliTestPth'.*Did you mean: CliTestPath", 13, 'inspect a misspelled object'),
            @(@('inspect', '--object', 'CliTestPlain'), "'CliTestPlain' has no SplineContainer\. Did you mean: CliTestPath", 13, 'inspect an object without a container'),
            @(@('sample', '--object', 'NoSuchPath', '--t', '0.5'), "No SplineContainer on 'NoSuchPath'.*cliext\.splines list", 13, 'sample an unknown object'),
            @(@('sample', '--object', 'CliTestPath', '--spline', '3'), 'No spline 3 on CliTestPath: it has 1 \(0 to 0\)', 13, 'sample a spline out of range'),
            @(@('sample', '--object', 'CliTestPath', '--t', '5'), '--t 5 is outside 0-1', 2, 'sample t 5'),
            @(@('sample', '--object', 'CliTestPath', '--distance', '99'), '--distance 99 is past the end of spline 0 \(8 long\)', 2, 'sample past the end'),
            @(@('sample', '--object', 'CliTestPath', '--near', '[1,x]'), "--near: '\[1,x\]' isn't a Vector3 value", 2, 'sample near a bad point'),
            @(@('sample', '--object', 'CliTestPath', '--t', '0.5', '--count', '3'), 'Give only one of --t, --distance, --near and --count', 2, 'sample with t and count'),
            @(@('sample', '--object', 'CliTestPath', '--count', '1'), '--count 1: give 2 or more', 2, 'sample count 1'),
            @(@('create', '--name', 'CliTestBad', '--knots', '[0,0,0];[1,0,0]', '--mode', 'Smooth'), "'Smooth' isn't a tangent mode\. Use one of: AutoSmooth, Linear", 2, 'create with an unknown mode'),
            @(@('create', '--name', 'CliTestBad', '--knots', '[0,0,0]'), 'at least two knots', 2, 'create with one knot'),
            @(@('create', '--name', 'CliTestBad', '--knots', '[0,0,0];[1,x,0]'), "Knot 1: '\[1,x,0\]' isn't a Vector3 value", 2, 'create with a bad knot'),
            @(@('create', '--name', 'CliTestBad', '--knots', '[0,0,0];[1,0,0]', '--position', 'up'), "--position: 'up' isn't a Vector3 value", 2, 'create at a bad position'),
            @(@('create', '--name', 'CliTestPath', '--knots', '[0,0,0];[1,0,0]'), 'already exists: change it with cliext\.splines edit --object CliTestPath', 6, 'create a container that exists'),
            @(@('edit', '--object', 'CliTestPth', '--ops', '[{"op":"set_closed"}]'), 'Did you mean: CliTestPath', 13, 'edit a misspelled object')
        )
        foreach ($c in $cases) {
            $x = Invoke-Cli (@('cliext.splines') + $c[0] + @('--format', 'json'))
            Expect-P3Failure $x $c[1] $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        $bad = Get-SplKnots 'CliTestBad'
        Expect ($bad -eq 'none') "a failed create made CliTestBad: $bad"
        $ops = @(
            @('[{"op":"remove_knot","index":7}]', 'No knot 7: spline 0 has 3 \(0 to 2\)\. cliext\.splines inspect', 'knot index out of range'),
            @('[{"op":"set_knot","position":"[1,1,1]"}]', 'set_knot needs an index', 'set_knot without an index'),
            @('[{"op":"set_knot","index":0}]', 'set_knot needs position, tangentIn.*would change nothing', 'set_knot with nothing to set'),
            @('[{"op":"set_knot","index":1,"position":"[4,0,0]"}]', 'already has those values', 'set_knot to the values it has'),
            @('[{"op":"set_knot","index":0,"position":"[1,x]"}]', "position: '\[1,x\]' isn't a Vector3 value", 'bad position'),
            @('[{"op":"set_closed","value":"false"}]', 'already open: set_closed would change nothing', 'open an open spline'),
            @('[{"op":"set_closed","value":"maybe"}]', "value: 'maybe' isn't a Boolean value: give true or false", 'non-boolean value'),
            @('[{"op":"set_tangent_mode","mode":"Smooth"}]', "'Smooth' isn't a tangent mode\. Use one of: AutoSmooth", 'unknown tangent mode'),
            @('[{"op":"set_tangent_mode","mode":"Linear"}]', 'already Linear', 'tangent mode it has'),
            @('[{"op":"add_knot"}]', 'add_knot needs a position', 'add_knot without a position'),
            @('[{"op":"add_knot","index":9,"position":"[0,0,0]"}]', 'add_knot index 9 is past the end', 'add_knot past the end'),
            @('[{"op":"remove_spline","spline":2}]', 'No spline 2: CliTestPath has 1', 'spline out of range'),
            @('[{"op":"add_knt","position":"[0,0,0]"}]', 'Did you mean: add_knot', 'misspelled op'),
            @('[{"op":"frob"}]', "Unknown op 'frob'\. Ops: add_knot, set_knot", 'unknown op')
        )
        $i = 0
        foreach ($o in $ops) {
            $i++
            $r = Invoke-Cli @('cliext.splines', 'edit', '--object', 'CliTestPath', '--ops-file', (Write-WorkFile "spl-fail-$i.json" $o[0]), '--format', 'json')
            Expect-P3Failure $r $o[1] $o[2]
            Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 0) "$($o[2]): reply $(Short $r.stdout)"
            $seen += $o[2]
        }
        $j = Invoke-Cli @('cliext.splines', 'edit', '--object', 'CliTestPath', '--ops-file', (Write-WorkFile 'spl-badjson.json' '[{"op":'), '--format', 'json')
        Expect-P3Failure $j "aren't valid JSON" 'broken JSON' 2
        $e = Invoke-Cli @('cliext.splines', 'edit', '--object', 'CliTestPath', '--ops-file', (Write-WorkFile 'spl-empty.json' '[]'), '--format', 'json')
        Expect-P3Failure $e 'No ops given' 'no ops' 2
        $k1 = Get-SplKnots
        Expect ($k1 -eq $k0) "a failed call changed the spline: '$k0' -> '$k1'"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "$($seen.Count + 3) failures with the fix; the spline unchanged ($k1)"
}

Add-Check 'spl.edit-rollback' $splRow 'a splines edit batch that adds knots and a spline, closes the loop and changes modes, then hits a knot out of range, leaves the spline as before in the scene and in Undo: one Undo then takes back the edit before it, and Redo restores that edit' {
    $orig = Open-P3Scene
    try {
        [void](New-SplPath)
        $k0 = Get-SplKnots
        $good = Invoke-Cli @('cliext.splines', 'edit', '--object', 'CliTestPath', '--ops-file', (Write-WorkFile 'spl-good.json' '[{"op":"set_knot","index":1,"position":"[4,1,0]"}]'), '--format', 'json')
        Expect ($good.exit -eq 0) "good edit: exit $($good.exit): $(Short ($good.stdout + $good.stderr))"
        $k1 = Get-SplKnots
        $i1 = (Invoke-Cli @('cliext.splines', 'inspect', '--object', 'CliTestPath', '--format', 'json')).stdout
        $ops = '[{"op":"add_knot","position":"[0,0,4]"},{"op":"set_closed"},{"op":"add_spline","knots":"[0,1,0];[0,2,0]"},{"op":"set_tangent_mode","mode":"Broken"},{"op":"remove_knot","index":0},{"op":"remove_knot","index":40}]'
        $r = Invoke-Cli @('cliext.splines', 'edit', '--object', 'CliTestPath', '--ops-file', (Write-WorkFile 'spl-bad-batch.json' $ops), '--format', 'json')
        Expect-P3Failure $r 'Op 5 \(remove_knot\) failed: No knot 40.*Nothing was changed' 'bad batch'
        Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq 5 -and $r.json.applied -eq 0) "reply: $(Short $r.stdout)"
        $k2 = Get-SplKnots
        Expect ($k2 -eq $k1) "in the scene: '$k1' -> '$k2'"
        $i2 = (Invoke-Cli @('cliext.splines', 'inspect', '--object', 'CliTestPath', '--format', 'json')).stdout
        Expect ($i2 -eq $i1) "inspect after the failed batch differs: $(Short $i2)"
        Invoke-SplUndo 'Undo'
        $ku = Get-SplKnots
        Expect ($ku -eq $k0) "one Undo after the failed batch: expected the state before the good edit '$k0', got '$ku'"
        Invoke-SplUndo 'Redo'
        $kr = Get-SplKnots
        Expect ($kr -eq $k1) "Redo: expected the good edit '$k1', got '$kr'"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "failed at op 5, spline unchanged ($k2); Undo -> $k0, Redo -> $k1"
}

# --- cliext.navmesh ---------------------------------------------------------------------------------------------------

# These groups change PROJECT SETTINGS. Every check that changes one reads it first, puts it back with the command's own
# set in a finally, and then writes the settings files' original bytes back: Unity re-serializes a settings file it
# saves (a newer serializedVersion, new default fields), so the values come back but the file text would not. The
# checks compare a hash of every serialized value of those settings (Get-SettingsSig) before and after, and git status
# of ProjectSettings when the project is a git repository.
$settingsFiles = @('ProjectSettings/DynamicsManager.asset', 'ProjectSettings/Physics2DSettings.asset', 'ProjectSettings/NavMeshAreas.asset', 'ProjectSettings/QualitySettings.asset', 'ProjectSettings/ProjectSettings.asset', 'ProjectSettings/TagManager.asset')

function Save-SettingsFiles {
    $h = @{}
    foreach ($f in $settingsFiles) {
        $p = Join-Path $projectPath ($f -replace '/', '\')
        if (Test-Path $p) { $h[$f] = [IO.File]::ReadAllBytes($p) }
    }
    $h
}
function Restore-SettingsFiles($saved) {
    # Writes back each file whose bytes changed; returns the ones it wrote.
    $put = @()
    foreach ($f in @($saved.Keys)) {
        $p = Join-Path $projectPath ($f -replace '/', '\')
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($p)) -ne [Convert]::ToBase64String($saved[$f])) { [IO.File]::WriteAllBytes($p, $saved[$f]); $put += $f }
    }
    , $put
}
function Get-SettingsGit {
    if (-not (Test-Path (Join-Path $projectPath '.git'))) { return '(no git)' }
    $ErrorActionPreference = 'Continue'
    "$(& git -C $projectPath status --porcelain -- ProjectSettings 2>$null)"
}
function Get-SettingsSig {
    # A hash of every serialized value in the physics, NavMesh and quality settings and the graphics APIs of three
    # platforms, as the Editor holds them.
    $code = @'
var sb = new System.Text.StringBuilder();
foreach (var path in new[] { "ProjectSettings/DynamicsManager.asset", "ProjectSettings/Physics2DSettings.asset", "ProjectSettings/NavMeshAreas.asset", "ProjectSettings/QualitySettings.asset" })
{
    var it = new UnityEditor.SerializedObject(UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path)[0]).GetIterator();
    while (it.Next(true))
    {
        string v = it.propertyType == UnityEditor.SerializedPropertyType.Integer ? it.longValue.ToString()
            : it.propertyType == UnityEditor.SerializedPropertyType.Enum || it.propertyType == UnityEditor.SerializedPropertyType.LayerMask ? it.intValue.ToString()
            : it.propertyType == UnityEditor.SerializedPropertyType.Float ? it.floatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : it.propertyType == UnityEditor.SerializedPropertyType.String ? it.stringValue
            : it.propertyType == UnityEditor.SerializedPropertyType.Boolean ? it.boolValue.ToString()
            : it.propertyType == UnityEditor.SerializedPropertyType.ObjectReference ? (it.objectReferenceValue == null ? "null" : UnityEditor.AssetDatabase.GetAssetPath(it.objectReferenceValue))
            : it.isArray ? "n" + it.arraySize : "";
        sb.Append(it.propertyPath).Append('=').Append(v).Append('\n');
    }
}
foreach (var t in new[] { UnityEditor.BuildTarget.StandaloneWindows64, UnityEditor.BuildTarget.Android, UnityEditor.BuildTarget.WebGL, UnityEditor.EditorUserBuildSettings.activeBuildTarget })
    sb.Append(t).Append(UnityEditor.PlayerSettings.GetUseDefaultGraphicsAPIs(t)).Append(string.Join(",", UnityEditor.PlayerSettings.GetGraphicsAPIs(t))).Append('\n');
for (int a = 0; a < 32; a++) for (int b = 0; b < 32; b++) if (UnityEngine.Physics.GetIgnoreLayerCollision(a, b)) sb.Append(a).Append('x').Append(b).Append(' ');
sb.Append("|q" + UnityEngine.QualitySettings.GetQualityLevel() + " " + UnityEngine.QualitySettings.shadowDistance + " " + UnityEngine.QualitySettings.antiAliasing);
var md5 = System.Security.Cryptography.MD5.Create();
return System.BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "") + "|" + sb.Length;
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'settings-sig.cs' $code), '--format', 'json')
    Expect ($r.exit -eq 0) "settings signature: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}
function Invoke-Eval([string]$name, [string]$code) {
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile $name $code), '--format', 'json')
    Expect ($r.exit -eq 0) "script eval ${name}: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $r.stdout.Trim().Trim('"')
}
function New-ScratchFolder {
    [void](Invoke-Eval 'scratch-folder.cs' 'if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests")) UnityEditor.AssetDatabase.CreateFolder("Assets", "_CliextTests"); if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/_CliextTests/CliTest")) UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests", "CliTest"); return "ok";')
}
function Expect-SettingsBack($saved, [string]$sig0, [string]$git0, [string]$what) {
    # After the finally put the values back: same values in the Editor, then the original file bytes, then git.
    $sig1 = Get-SettingsSig
    $put = Restore-SettingsFiles $saved
    Expect ($sig1 -eq $sig0) "${what}: the settings were not put back ($sig0 -> $sig1)"
    $git1 = Get-SettingsGit
    Expect ($git1 -eq $git0) "${what}: git status of ProjectSettings changed: '$git0' -> '$git1'"
    $(if ($put.Count) { "values restored; re-serialized $($put -join ', ') written back" } else { 'values restored, files untouched' })
}
function Test-SettingsFailures([string]$group, [object[]]$cases) {
    # Each case: args, hint regex, exit, what. Returns the labels; the settings and their files must not change.
    $saved = Save-SettingsFiles
    $sig0 = Get-SettingsSig
    $git0 = Get-SettingsGit
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli (@($group) + $c[0] + @('--format', 'json'))
        Expect-P3Failure $x $c[1] $c[3] $c[2]
        $seen += "$($c[3]) $($x.exit)"
    }
    $sig1 = Get-SettingsSig
    $put = Restore-SettingsFiles $saved
    Expect ($sig1 -eq $sig0) "a failed $group call changed the settings ($sig0 -> $sig1)"
    Expect ($put.Count -eq 0) "a failed $group call wrote $($put -join ', ')"
    Expect ((Get-SettingsGit) -eq $git0) "a failed $group call changed git status of ProjectSettings"
    , $seen
}

# Area and agent changes go to the project's NavMesh settings and are put back in a finally. Bakes run in a scratch
# scene saved in the scratch folder (a 30 x 30 floor with a NavMeshSurface, a wall across its middle and an island
# that doesn't connect), so the baked data goes next to it there; the scene that was open is reopened from disk after,
# and TestLevel's own surface is never baked.
$navRow = 'cliext.navmesh'

function Open-NavScene([string]$name) {
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    New-ScratchFolder
    $code = @'
string before = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
var s = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
var floor = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Plane); floor.name = "CliTestFloor"; floor.transform.localScale = new UnityEngine.Vector3(3, 1, 3);
var wall = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube); wall.name = "CliTestWall"; wall.transform.SetParent(floor.transform, false);
wall.transform.localPosition = new UnityEngine.Vector3(0, 1, 0); wall.transform.localScale = new UnityEngine.Vector3(20f / 3, 2, 1f / 3);
var island = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Plane); island.name = "CliTestIsland"; island.transform.SetParent(floor.transform, false);
island.transform.localPosition = new UnityEngine.Vector3(40f / 3, 0, 0); island.transform.localScale = new UnityEngine.Vector3(1f / 3, 1, 1f / 3);
floor.AddComponent<Unity.AI.Navigation.NavMeshSurface>().collectObjects = Unity.AI.Navigation.CollectObjects.Children;
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(s, "{{path}}");
return "ok|" + before;
'@
    $r = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'nav-open.cs' $code.Replace('{{path}}', "$scratchRel/$name.unity")), '--format', 'json')
    Expect ($r.exit -eq 0 -and "$($r.stdout)" -match 'ok\|(Assets/[^"]+\.unity)') "scratch NavMesh scene: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
    $Matches[1]
}
function Invoke-NavBake([string]$scene) {
    $b = Invoke-Cli @('cliext.navmesh', 'bake', '--surface', 'CliTestFloor', '--format', 'json') 300
    Expect ($b.exit -eq 0) "bake: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
    $b
}

Add-Check 'nav.settings-set' $navRow 'cliext.navmesh set names area 3 and sets its cost, and changes the Humanoid agent''s radius and height; settings and NavMesh.GetAreaFromName/GetAreaCost/GetSettingsByID show them; put back in a finally (--name none clears the area), the settings and their files are as before' {
    $saved = Save-SettingsFiles; $sig0 = Get-SettingsSig; $git0 = Get-SettingsGit
    $s0 = Invoke-Cli @('cliext.navmesh', 'settings', '--format', 'json')
    Expect ($s0.exit -eq 0) "settings: exit $($s0.exit): $(Short ($s0.stdout + $s0.stderr))"
    $area3 = @($s0.json.areas) | Where-Object { $_.index -eq 3 }
    Expect (-not $area3) "area 3 is already named: $(Short ($area3 | ConvertTo-Json -Compress))"
    $agent = @($s0.json.agents)[0]
    Expect ($null -ne $agent.drop -and $null -ne $agent.jump) "settings has no drop and jump: $(Short ($agent | ConvertTo-Json -Compress))"
    $ci = [Globalization.CultureInfo]::InvariantCulture
    try {
        $a = Invoke-Cli @('cliext.navmesh', 'set', '--index', '3', '--name', 'CliTestMud', '--cost', '3.5', '--format', 'json')
        Expect ($a.exit -eq 0 -and @($a.json.changes).Count -eq 2) "set area: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        $g = Invoke-Cli @('cliext.navmesh', 'set', '--agent', $agent.name, '--set', 'agentRadius=0.4;agentHeight=1.8', '--format', 'json')
        Expect ($g.exit -eq 0 -and (@($g.json.changes) -join ';') -match 'agentRadius: .* -> 0\.4;.*agentHeight: .* -> 1\.8') "set agent: exit $($g.exit): $(Short ($g.stdout + $g.stderr))"
        $s1 = Invoke-Cli @('cliext.navmesh', 'settings', '--format', 'json')
        $m = @($s1.json.areas) | Where-Object { $_.index -eq 3 }; $ag = @($s1.json.agents)[0]
        Expect ($m.name -eq 'CliTestMud' -and $m.cost -eq 3.5 -and $ag.radius -eq 0.4 -and $ag.height -eq 1.8) "settings after: $(Short $s1.stdout 300)"
        $e = Invoke-Eval 'nav-api.cs' "var s = UnityEngine.AI.NavMesh.GetSettingsByID($($agent.id)); var ci = System.Globalization.CultureInfo.InvariantCulture; return UnityEngine.AI.NavMesh.GetAreaFromName(`"CliTestMud`") + `"|`" + UnityEngine.AI.NavMesh.GetAreaCost(3).ToString(ci) + `"|`" + s.agentRadius.ToString(ci) + `"|`" + s.agentHeight.ToString(ci);"
        Expect ($e -eq '3|3.5|0.4|1.8') "NavMesh API: $e"
    }
    finally {
        $ba = Invoke-Cli @('cliext.navmesh', 'set', '--index', '3', '--name', 'none', '--cost', '1', '--format', 'json')
        $bg = Invoke-Cli @('cliext.navmesh', 'set', '--agent', $agent.name, '--set', "agentRadius=$(([double]$agent.radius).ToString($ci));agentHeight=$(([double]$agent.height).ToString($ci))", '--format', 'json')
    }
    Expect ($ba.exit -eq 0 -and $bg.exit -eq 0) "set back: area exit $($ba.exit) $(Short $ba.stderr); agent exit $($bg.exit) $(Short $bg.stderr)"
    $restored = Expect-SettingsBack $saved $sig0 $git0 'navmesh set'
    "area 3 CliTestMud cost 3.5, $($agent.name) radius 0.4 height 1.8 (NavMesh API $e); $restored"
}

Add-Check 'nav.bake-query' $navRow 'in a scratch scene saved in the scratch folder, cliext.navmesh surfaces lists the unbaked surface, bake writes its data next to the scene, surfaces then names it, query finds a path around the wall (NavMesh.CalculatePath agrees) and --sample the nearest point' {
    $orig = Open-NavScene 'CliTestNav'
    $data = "$scratchRel/CliTestNav/NavMesh-CliTestFloor.asset"
    try {
        $s = Invoke-Cli @('cliext.navmesh', 'surfaces', '--format', 'json')
        $rec = @($s.json) | Where-Object { $_.gameObject -eq 'CliTestFloor' }
        Expect ($s.exit -eq 0 -and $rec -and $null -eq $rec.data -and $rec.collect -eq 'Children' -and $rec.agent -eq 'Humanoid') "surfaces before: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $b = Invoke-NavBake
        Expect ($b.json.surface -eq 'CliTestFloor' -and $b.json.data -eq $data) "bake reply: $(Short $b.stdout)"
        Expect (Test-Path (Join-Path $projectPath ($data -replace '/', '\'))) "no data asset at $data"
        $s2 = Invoke-Cli @('cliext.navmesh', 'surfaces', '--format', 'json')
        Expect ((@($s2.json) | Where-Object { $_.gameObject -eq 'CliTestFloor' }).data -eq $data) "surfaces after: $(Short $s2.stdout)"
        $q = Invoke-Cli @('cliext.navmesh', 'query', '--from', '[-5,0,-5]', '--to', '[-5,0,5]', '--format', 'json')
        Expect ($q.exit -eq 0 -and $q.json.status -eq 'PathComplete' -and @($q.json.corners).Count -gt 2 -and $q.json.length -gt 12) "query: exit $($q.exit): $(Short ($q.stdout + $q.stderr))"
        $e = Invoke-Eval 'nav-path.cs' 'var p = new UnityEngine.AI.NavMeshPath(); UnityEngine.AI.NavMeshHit a, b; UnityEngine.AI.NavMesh.SamplePosition(new UnityEngine.Vector3(-5, 0, -5), out a, 2, -1); UnityEngine.AI.NavMesh.SamplePosition(new UnityEngine.Vector3(-5, 0, 5), out b, 2, -1); UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, -1, p); return p.status + "|" + p.corners.Length;'
        Expect ($e -eq "PathComplete|$(@($q.json.corners).Count)") "NavMesh.CalculatePath: $e, query had $(@($q.json.corners).Count) corners"
        $w = Invoke-Cli @('cliext.navmesh', 'query', '--from', '[-5,0,-5]', '--to', '[5,0,-5]', '--agent', 'Humanoid', '--areas', 'Walkable', '--format', 'json')
        Expect ($w.exit -eq 0 -and $w.json.status -eq 'PathComplete' -and @($w.json.corners).Count -eq 2) "straight query with agent and areas: exit $($w.exit): $(Short ($w.stdout + $w.stderr))"
        $sm = Invoke-Cli @('cliext.navmesh', 'query', '--sample', '[3,1,3]', '--format', 'json')
        Expect ($sm.exit -eq 0 -and $sm.json.found -eq $true -and $sm.json.area -eq 'Walkable' -and $sm.json.distance -lt 1.1) "sample: exit $($sm.exit): $(Short ($sm.stdout + $sm.stderr))"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "baked to $data; path around the wall $($q.json.length) long, $(@($q.json.corners).Count) corners (CalculatePath $e); sample $($sm.json.position)"
}

Add-Check 'nav.errors' $navRow 'cliext.navmesh query with no NavMesh, points off it, a partial path, unknown areas and agents and bad arguments; set with bad indexes, names, costs, agents, settings and values and changes that do nothing; bake of an unknown surface, a bad timeout and an unsaved scene exit 13, 2 or 6 with the fix, and change nothing' {
    $seen = @()
    # An untitled scene with a surface: nothing baked, and a bake would put its data in Assets/.
    $orig = Open-P3Scene
    try {
        [void](Invoke-Eval 'nav-untitled.cs' 'var f = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Plane); f.name = "CliTestFloor"; f.AddComponent<Unity.AI.Navigation.NavMeshSurface>(); return "ok";')
        $assets0 = @(Get-ChildItem (Join-Path $projectPath 'Assets') -File -Filter 'NavMesh*' | ForEach-Object { $_.Name })
        $seen += Test-SettingsFailures 'cliext.navmesh' @(
            @(@('query', '--sample', '[0,0,0]'), 'no baked NavMesh: bake one with cliext\.navmesh bake', 13, 'query with no NavMesh'),
            @(@('bake'), "'CliTestFloor' is in an unsaved scene: save the scene first", 6, 'bake in an unsaved scene')
        )
        $assets1 = @(Get-ChildItem (Join-Path $projectPath 'Assets') -File -Filter 'NavMesh*' | ForEach-Object { $_.Name })
        Expect (($assets1 -join ',') -eq ($assets0 -join ',')) "a refused bake wrote to Assets/: $($assets1 -join ', ')"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the untitled scene: $(Short $closed)"
    $orig = Open-NavScene 'CliTestNavErr'
    try {
        [void](Invoke-NavBake)
        $seen += Test-SettingsFailures 'cliext.navmesh' @(
            @(@('query', '--from', '[100,0,100]', '--to', '[0,0,-5]'), '--from \[100,0,100\] isn''t within 2 of the NavMesh: move it onto the NavMesh \(cliext\.navmesh query --sample', 13, 'a path from a point off the NavMesh'),
            @(@('query', '--from', '[-5,0,-5]', '--to', '[0,9,-5]'), '--to \[0,9,-5\] isn''t within 2', 13, 'a path to a point above the NavMesh'),
            @(@('query', '--sample', '[100,0,100]'), 'No NavMesh within 2 of \[100,0,100\]: raise --distance', 13, 'a sample far from the NavMesh'),
            @(@('query', '--sample', '[0,0,-5]', '--areas', 'Jump'), 'No NavMesh within 2 of .*or allow more --areas', 13, 'a sample in an area with no NavMesh'),
            @(@('query', '--from', '[-5,0,-5]', '--to', '[40,0,0]'), 'Only a partial path: the NavMesh doesn''t connect', 6, 'a path to an unconnected island'),
            @(@('query', '--from', '[0,0,-5]', '--to', '[0,0,5]', '--areas', 'Walkabel'), "--areas: no area 'Walkabel'\. Did you mean: Walkable\? Areas: Walkable, Not Walkable, Jump\. cliext\.navmesh settings", 2, 'a misspelled area'),
            @(@('query', '--sample', '[0,0,-5]', '--agent', 'Humanod'), "No agent type 'Humanod'\. Did you mean: Humanoid\? Agent types: Humanoid", 2, 'a misspelled agent'),
            @(@('query', '--sample', '[0,0,-5]', '--agent', '12345'), "No agent type '12345'\..*Agent types: Humanoid\. cliext\.navmesh settings", 2, 'an unknown agent id'),
            @(@('query', '--sample', '[0,0]'), "--sample: Expected 3 numbers, got '\[0,0\]'", 2, 'a bad sample point'),
            @(@('query', '--from', '[0,0,-5]'), 'A path needs both ends: pass --to too', 2, 'a path without --to'),
            @(@('query'), 'Pass --from and --to for a path, or --sample', 2, 'query with nothing'),
            @(@('query', '--sample', '[0,0,-5]', '--from', '[0,0,-5]', '--to', '[0,0,5]'), 'Give --sample, or --from and --to, not both', 2, 'query with --sample and a path'),
            @(@('query', '--sample', '[0,0,-5]', '--distance', '0'), '--distance 0: give more than 0', 2, 'query with distance 0'),
            @(@('set', '--index', '1', '--name', 'CliTestX'), 'Areas 0-2 are built in .*their names can''t change', 2, 'renaming a built-in area'),
            @(@('set', '--index', '40', '--cost', '2'), '--index 40: areas are 0-31', 2, 'an area index out of range'),
            @(@('set', '--index', '3', '--cost', '0.5'), '--cost 0\.5: area costs are 1 or more', 2, 'a cost under 1'),
            @(@('set', '--index', '3', '--name', 'Jump'), "Area 2 is already named 'Jump': pick another name", 2, 'a name another area has'),
            @(@('set', '--index', '3'), '--index 3: pass --name or --cost', 2, 'an index with nothing to change'),
            @(@('set', '--name', 'CliTestX'), '--name and --cost change the area at --index: pass --index', 2, '--name without --index'),
            @(@('set'), 'Nothing to change: pass --index with --name or --cost, or --agent with --set', 2, 'set with nothing'),
            @(@('set', '--index', '2', '--cost', '2'), 'Area 2 \(Jump\) already has cost 2: nothing would change', 6, 'an area cost it has'),
            @(@('set', '--agent', 'Humanod', '--set', 'agentRadius=0.4'), "No agent type 'Humanod'\. Did you mean: Humanoid", 2, 'set a misspelled agent'),
            @(@('set', '--agent', 'Humanoid'), '--agent Humanoid: pass --set', 2, 'an agent with nothing to set'),
            @(@('set', '--set', 'agentRadius=0.4'), '--set changes the agent type named by --agent: pass --agent', 2, '--set without --agent'),
            @(@('set', '--agent', 'Humanoid', '--set', 'agentRadus=0.4'), "No setting 'agentRadus'\. Did you mean: agentRadius\? Settings: agentRadius, agentHeight", 2, 'a misspelled agent setting'),
            @(@('set', '--agent', 'Humanoid', '--set', 'agentTypeID=5'), "No setting 'agentTypeID'", 2, 'setting the agent id'),
            @(@('set', '--agent', 'Humanoid', '--set', 'agentRadius=abc'), "agentRadius: 'abc' isn't a Single value: give a number", 2, 'a radius that is not a number'),
            @(@('set', '--agent', 'Humanoid', '--set', 'agentHeight=1.5;agentRadius=-1'), 'agentRadius -1 is out of range: give more than 0', 2, 'a negative radius (after a good height)'),
            @(@('set', '--agent', 'Humanoid', '--set', 'agentSlope=75'), 'agentSlope 75 is out of range: give 0 to 60', 2, 'a slope of 75'),
            @(@('set', '--agent', 'Humanoid', '--set', 'agentRadius'), "'agentRadius' isn't name=value", 2, 'a setting without a value'),
            @(@('set', '--index', '3', '--name', 'CliTestX', '--agent', 'Humanoid', '--set', 'agentRadius=-1'), 'agentRadius -1 is out of range', 2, 'a good area change with a bad agent value'),
            @(@('bake', '--surface', 'CliTestFlor'), "No active NavMeshSurface on 'CliTestFlor'\. Did you mean: CliTestFloor\?", 13, 'bake a misspelled surface'),
            @(@('bake', '--surface', 'Nothing'), "No active NavMeshSurface on 'Nothing'\. Surfaces: CliTestFloor\. cliext\.navmesh surfaces lists them", 13, 'bake an unknown surface')
        )
        # --timeout is also the CLI's own wait, which refuses 0 before the command runs (a usage envelope on stdout).
        $to = Invoke-Cli @('cliext.navmesh', 'bake', '--timeout', '0', '--format', 'json')
        Expect ($to.exit -eq 2 -and "$($to.stdout)$($to.stderr)" -match 'Timeout must be a positive number') "bake --timeout 0: exit $($to.exit): $(Short ($to.stdout + $to.stderr))"
        $seen += 'bake with timeout 0 2'
        $s = Invoke-Cli @('cliext.navmesh', 'surfaces', '--format', 'json')
        Expect ((@($s.json) | Where-Object { $_.gameObject -eq 'CliTestFloor' }).data -eq "$scratchRel/CliTestNavErr/NavMesh-CliTestFloor.asset") "the surface's data changed: $(Short $s.stdout)"
        $agentState = Invoke-Cli @('cliext.navmesh', 'settings', '--format', 'json')
        Expect (-not (@($agentState.json.areas) | Where-Object { $_.index -eq 3 })) "area 3 was named by a failed call: $(Short $agentState.stdout)"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "$($seen.Count) failures with the fix; settings, files and the baked data unchanged"
}

Add-Check 'nav.bake-play-mode' $navRow 'in Play mode cliext.navmesh bake exits 6 naming editor.playmode stop and bakes nothing; back in Edit mode the scene is clean and no data folder was made next to it' {
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    $active = @($scenes.json) | Where-Object { $_.isActive } | Select-Object -First 1
    Expect ($active -and -not (@($scenes.json) | Where-Object { $_.isDirty })) "no clean saved scene open: $(Short $scenes.stdout)"
    $dataDir = Join-Path $projectPath (($active.path -replace '\.unity$', '') -replace '/', '\')
    $hadDir = Test-Path $dataDir
    $start = Invoke-Cli @('editor.playmode', 'start', '--format', 'json')
    try {
        Expect ((Wait-Editor 120) -eq 0) "Play mode didn't start: $(Short ($start.stdout + $start.stderr))"
        $b = Invoke-Cli @('cliext.navmesh', 'bake', '--format', 'json')
        Expect-P3Failure $b 'works in Edit mode .*stop Play mode first \(editor\.playmode stop\)' 'bake in Play mode' 6
        $q = Invoke-Cli @('cliext.navmesh', 'surfaces', '--format', 'json')
        Expect ($q.exit -eq 0) "surfaces in Play mode: exit $($q.exit)"
    }
    finally {
        [void](Invoke-Cli @('editor.playmode', 'stop', '--format', 'json'))
        [void](Wait-Editor 120)
    }
    $mode = Invoke-Eval 'nav-mode.cs' 'var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return (UnityEditor.EditorApplication.isPlaying ? "playing" : "edit") + "|" + a.isDirty;'
    Expect ($mode -eq 'edit|False') "after Play mode: $mode"
    Expect ((Test-Path $dataDir) -eq $hadDir) "a data folder appeared next to $($active.path)"
    "bake refused in Play mode ($(Short $b.stderr 90)); back in Edit mode, $($active.name) clean"
}

# --- cliext.physics ---------------------------------------------------------------------------------------------------

# set_matrix changes the project's collision matrix and is put back in a finally. Raycasts run in the untitled scene of
# Open-P3Scene with a cube (layer Water) over a plane (Default), discarded with it. Materials go in the scratch folder.
$physRow = 'cliext.physics'

function Add-PhysTargets {
    [void](Invoke-Eval 'phys-targets.cs' @'
var box = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube); box.name = "CliTestBox"; box.transform.position = new UnityEngine.Vector3(0, 0.5f, 0); box.layer = 4;
var ground = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Plane); ground.name = "CliTestGround";
return "ok";
'@)
}

Add-Check 'phys.matrix-set' $physRow 'cliext.physics set_matrix makes Default and Water ignore each other: Physics.GetIgnoreLayerCollision and matrix (both rows) show it; the 2D matrix reads; set back in a finally, the settings and their files are as before' {
    $saved = Save-SettingsFiles; $sig0 = Get-SettingsSig; $git0 = Get-SettingsGit
    $before = Invoke-Eval 'phys-ignore0.cs' 'return UnityEngine.Physics.GetIgnoreLayerCollision(0, 4).ToString();'
    Expect ($before -eq 'False') "Default and Water already ignore each other: $before"
    try {
        $r = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', 'Default', '--b', 'Water', '--collide', 'false', '--format', 'json')
        Expect ($r.exit -eq 0 -and $r.json.a -eq 'Default' -and $r.json.b -eq 'Water' -and $r.json.collide -eq $false) "set_matrix: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
        $now = Invoke-Eval 'phys-ignore1.cs' 'return UnityEngine.Physics.GetIgnoreLayerCollision(0, 4) + "|" + UnityEngine.Physics.GetIgnoreLayerCollision(4, 0);'
        Expect ($now -eq 'True|True') "GetIgnoreLayerCollision(0,4)|(4,0): $now"
        $m = Invoke-Cli @('cliext.physics', 'matrix', '--format', 'json')
        $d = @($m.json) | Where-Object { $_.layer -eq 'Default' }; $w = @($m.json) | Where-Object { $_.layer -eq 'Water' }
        Expect ($m.exit -eq 0 -and (@($d.ignores) -contains 'Water') -and (@($w.ignores) -contains 'Default')) "matrix: $(Short $m.stdout)"
        $m2 = Invoke-Cli @('cliext.physics', 'matrix', '--2d', 'true', '--format', 'json')
        Expect ($m2.exit -eq 0 -and @($m2.json).Count -ge 1 -and (@($m2.json) | Where-Object { $_.layer -eq 'Default' })) "matrix --2d: exit $($m2.exit): $(Short ($m2.stdout + $m2.stderr))"
    }
    finally { $back = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', 'Default', '--b', 'Water', '--collide', 'true', '--format', 'json') }
    Expect ($back.exit -eq 0) "set back: exit $($back.exit): $(Short ($back.stdout + $back.stderr))"
    $restored = Expect-SettingsBack $saved $sig0 $git0 'set_matrix'
    "Default x Water ignored (GetIgnoreLayerCollision True both ways), matrix shows both rows; $restored"
}

Add-Check 'phys.layers-only' $physRow 'cliext.physics layers names two free user layers (LayerMask.NameToLayer agrees), refuses a used or built-in index and exits 6 on names it has; set_matrix --only makes the first collide with just the second (GetIgnoreLayerCollision over all 32, both ways) and matrix shows the unnamed layers it ignores; all put back in a finally, the settings and their files as before' {
    $saved = Save-SettingsFiles; $sig0 = Get-SettingsSig; $git0 = Get-SettingsGit
    $l0 = Invoke-Cli @('cliext.physics', 'layers', '--format', 'json')
    Expect ($l0.exit -eq 0 -and @($l0.json).Count -eq 32 -and (@($l0.json) | Where-Object { $_.index -eq 4 }).name -eq 'Water' -and (@($l0.json) | Where-Object { $_.index -eq 4 }).builtin) "layers: exit $($l0.exit): $(Short $l0.stdout)"
    $free = @(@($l0.json) | Where-Object { -not $_.name -and $_.index -ge 8 } | ForEach-Object { [int]$_.index })
    Expect ($free.Count -ge 2) "no two free user layers: $(Short $l0.stdout)"
    $ia = $free[0]; $ib = $free[1]
    try {
        $s = Invoke-Cli @('cliext.physics', 'layers', '--set', "$ia=CliTestA;$ib=CliTestB", '--format', 'json')
        Expect ($s.exit -eq 0 -and (@($s.json.changes) -join ';') -eq "${ia}: (none) -> CliTestA;${ib}: (none) -> CliTestB") "layers --set: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $n = Invoke-Eval 'phys-layers.cs' 'return UnityEngine.LayerMask.NameToLayer("CliTestA") + "|" + UnityEngine.LayerMask.NameToLayer("CliTestB");'
        Expect ($n -eq "$ia|$ib") "LayerMask.NameToLayer: $n"
        $again = Invoke-Cli @('cliext.physics', 'layers', '--set', "$ia=CliTestA", '--format', 'json')
        Expect-P3Failure $again 'nothing would change' 'layers set to what they are' 6
        $used = Invoke-Cli @('cliext.physics', 'layers', '--set', "$ia=CliTestOther", '--format', 'json')
        Expect-P3Failure $used "layer $ia is already CliTestA: pick a free index \(free: .*\), or clear it first with $ia=none" 'a used index' 2
        $bi = Invoke-Cli @('cliext.physics', 'layers', '--set', '4=CliTestOther', '--format', 'json')
        Expect-P3Failure $bi "layer 4 is Unity's built-in Water, whose name can't change" 'a built-in index' 2
        $o = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', 'CliTestA', '--only', 'CliTestB', '--format', 'json')
        Expect ($o.exit -eq 0 -and (@($o.json.collidesOnlyWith) -join ',') -eq 'CliTestB' -and @($o.json.changed).Count -eq 31) "set_matrix --only: exit $($o.exit): $(Short ($o.stdout + $o.stderr))"
        $ig = Invoke-Eval 'phys-only.cs' "var sb = new System.Text.StringBuilder(); for (int b = 0; b < 32; b++) { bool i1 = UnityEngine.Physics.GetIgnoreLayerCollision($ia, b), i2 = UnityEngine.Physics.GetIgnoreLayerCollision(b, $ia); sb.Append(i1 == i2 ? (i1 ? '1' : '0') : '?'); } return sb.ToString();"
        $want = -join (0..31 | ForEach-Object { if ($_ -eq $ib) { '0' } else { '1' } })
        Expect ($ig -eq $want) "GetIgnoreLayerCollision($ia, 0..31): $ig, want $want"
        $m = Invoke-Cli @('cliext.physics', 'matrix', '--format', 'json')
        $row = @($m.json) | Where-Object { $_.layer -eq 'CliTestA' }
        Expect ((@($row.ignores) -contains 'Default') -and (@($row.ignores) -notcontains 'CliTestB') -and $row.unnamedIgnored -match '\d') "matrix row for CliTestA: $(Short ($row | ConvertTo-Json -Compress))"
        $all = Invoke-Cli @('cliext.physics', 'matrix', '--all', 'true', '--format', 'json')
        Expect (@($all.json).Count -eq 32) "matrix --all lists 32 rows: $(@($all.json).Count)"
        $o2 = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', 'CliTestA', '--only', 'CliTestB', '--format', 'json')
        Expect-P3Failure $o2 'already collides only with CliTestB in the 3D matrix: nothing would change' 'the same --only again' 6
        $o3 = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', 'CliTestA', '--only', 'CliTestB', '--b', 'Default', '--format', 'json')
        Expect-P3Failure $o3 '--only sets every pair for --a: leave out --b and --collide' '--only with --b' 2
        $o4 = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', 'CliTestA', '--format', 'json')
        Expect-P3Failure $o4 'Pass --b and --collide true\|false for one pair, or --only' 'set_matrix with neither' 2
    }
    finally {
        $back = Invoke-Cli @('cliext.physics', 'set_matrix', '--a', "$ia", '--only', ((0..31) -join ';'), '--format', 'json')
        $clear = Invoke-Cli @('cliext.physics', 'layers', '--set', "$ia=none;$ib=none", '--format', 'json')
    }
    Expect ($back.exit -eq 0 -and $clear.exit -eq 0) "put back: matrix exit $($back.exit), layers exit $($clear.exit): $(Short ($back.stderr + $clear.stderr))"
    $restored = Expect-SettingsBack $saved $sig0 $git0 'layers and set_matrix --only'
    "layers $ia/$ib named; used and built-in refused; --only: collides with $ib alone ($ig); $restored"
}

Add-Check 'phys.material-assign' $physRow 'cliext.physics material --assign creates a bouncy material and puts it on a scratch object''s collider (Collider.sharedMaterial shows it); again exits 6; an unknown object or one with no collider exits 13 and makes no file' {
    New-ScratchFolder
    $orig = Open-P3Scene
    $mat = "$scratchRel/CliTestAssign.physicMaterial"
    try {
        Add-PhysTargets
        [void](Invoke-Eval 'phys-bare.cs' 'new UnityEngine.GameObject("CliTestBare"); return "ok";')
        $a = Invoke-Cli @('cliext.physics', 'material', '--path', $mat, '--set', 'bounciness=0.9', '--assign', '/CliTestGround', '--format', 'json')
        Expect ($a.exit -eq 0 -and $a.json.created -eq $true -and (@($a.json.assigned) -join ',') -eq 'CliTestGround MeshCollider') "material --assign: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        $r = Invoke-Eval 'phys-assign.cs' 'var c = UnityEngine.GameObject.Find("CliTestGround").GetComponent<UnityEngine.Collider>(); return c.sharedMaterial == null ? "none" : UnityEditor.AssetDatabase.GetAssetPath(c.sharedMaterial) + "|" + c.sharedMaterial.bounciness.ToString(System.Globalization.CultureInfo.InvariantCulture);'
        Expect ($r -eq "$mat|0.9") "Collider.sharedMaterial: $r"
        $again = Invoke-Cli @('cliext.physics', 'material', '--path', $mat, '--assign', 'CliTestGround', '--format', 'json')
        Expect-P3Failure $again "CliTestGround's collider\(s\) already use it: nothing would change" 'assign it again' 6
        $box = Invoke-Cli @('cliext.physics', 'material', '--path', $mat, '--assign', 'CliTestBox', '--format', 'json')
        Expect ($box.exit -eq 0 -and (@($box.json.assigned) -join ',') -eq 'CliTestBox BoxCollider' -and @($box.json.set).Count -eq 0) "assign an existing material: exit $($box.exit): $(Short ($box.stdout + $box.stderr))"
        $bad = "$scratchRel/CliTestAssignBad.physicMaterial"
        $u = Invoke-Cli @('cliext.physics', 'material', '--path', $bad, '--assign', 'CliTestGrund', '--format', 'json')
        Expect-P3Failure $u "--assign: no GameObject 'CliTestGrund' in the open scenes\. Did you mean: CliTestGround\?" 'an unknown object' 13
        $n = Invoke-Cli @('cliext.physics', 'material', '--path', $bad, '--assign', 'CliTestBare', '--format', 'json')
        Expect-P3Failure $n "'CliTestBare' has no Collider: add one first \(component add --type BoxCollider\)" 'an object with no collider' 13
        Expect (-not (Test-Path (Join-Path $scratch 'CliTestAssignBad.physicMaterial'))) 'a refused --assign created the material'
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "assigned $mat (bounciness 0.9) to CliTestGround, then CliTestBox; again exit 6; unknown and collider-less objects exit 13"
}

Add-Check 'nav.obstacle' $navRow 'in a scratch scene, cliext.navmesh obstacle adds a carving box NavMeshObstacle (size, center), changes it to a capsule, exits 6 on values it has, 2 on a bad shape or size, 13 on an unknown object, and --remove takes it off' {
    $orig = Open-P3Scene
    try {
        Add-PhysTargets
        $a = Invoke-Cli @('cliext.navmesh', 'obstacle', '--object', 'CliTestBox', '--size', '[2,2,2]', '--center', '[0,0.5,0]', '--carve', 'true', '--format', 'json')
        Expect ($a.exit -eq 0 -and $a.json.shape -eq 'Box' -and $a.json.size -eq '[2,2,2]' -and $a.json.carve -eq $true -and @($a.json.changes)[0] -eq 'added NavMeshObstacle') "obstacle: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        $read = 'var o = UnityEngine.GameObject.Find("CliTestBox").GetComponent<UnityEngine.AI.NavMeshObstacle>(); return o == null ? "none" : o.shape + "|" + o.size + "|" + o.center + "|" + o.carving + "|" + o.radius + "|" + o.size.y;'
        $s1 = Invoke-Eval 'nav-obst1.cs' $read
        Expect ($s1 -match '^Box\|\(2\.0+, 2\.0+, 2\.0+\)\|\(0\.0+, 0\.50*, 0\.0+\)\|True\|') "NavMeshObstacle after add: $s1"
        $same = Invoke-Cli @('cliext.navmesh', 'obstacle', '--object', 'CliTestBox', '--size', '[2,2,2]', '--carve', 'true', '--format', 'json')
        Expect-P3Failure $same "CliTestBox's NavMeshObstacle already has those values: nothing would change" 'obstacle values it has' 6
        $c = Invoke-Cli @('cliext.navmesh', 'obstacle', '--object', 'CliTestBox', '--shape', 'Capsule', '--radius', '0.75', '--height', '3', '--format', 'json')
        Expect ($c.exit -eq 0 -and $c.json.shape -eq 'Capsule' -and $c.json.radius -eq 0.75 -and $c.json.height -eq 3) "to a capsule: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
        $s2 = Invoke-Eval 'nav-obst2.cs' $read
        Expect ($s2 -match '^Capsule\|.*\|True\|0\.75\|3$') "NavMeshObstacle as a capsule: $s2"
        $seen = @()
        foreach ($x in @(
                @(@('--object', 'CliTestBox', '--shape', 'Sphere'), "--shape: 'Sphere' isn't an obstacle shape\..*Shapes: ", 2, 'an unknown shape'),
                @(@('--object', 'CliTestBox', '--shape', 'Box', '--size', '[1,0,1]'), 'every side must be more than 0', 2, 'a flat box'),
                @(@('--object', 'CliTestBox', '--size', '[1,1,1]'), 'a capsule takes --radius and --height', 2, '--size on a capsule'),
                @(@('--object', 'CliTestBoks', '--carve', 'false'), "--object: no GameObject 'CliTestBoks' in the open scenes\. Did you mean: CliTestBox\?", 13, 'an unknown object'))) {
            $r = Invoke-Cli (@('cliext.navmesh', 'obstacle') + $x[0] + @('--format', 'json'))
            Expect-P3Failure $r $x[1] $x[3] $x[2]
            $seen += "$($x[3]) $($r.exit)"
        }
        Expect ((Invoke-Eval 'nav-obst3.cs' $read) -eq $s2) 'a refused obstacle call changed the obstacle'
        $rm = Invoke-Cli @('cliext.navmesh', 'obstacle', '--object', 'CliTestBox', '--remove', 'true', '--format', 'json')
        Expect ($rm.exit -eq 0 -and $rm.json.removed -eq $true -and (Invoke-Eval 'nav-obst4.cs' $read) -eq 'none') "--remove: exit $($rm.exit): $(Short ($rm.stdout + $rm.stderr))"
        $rm2 = Invoke-Cli @('cliext.navmesh', 'obstacle', '--object', 'CliTestBox', '--remove', 'true', '--format', 'json')
        Expect-P3Failure $rm2 "has no NavMeshObstacle: nothing would change" 'remove twice' 6
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "box 2x2x2 carving -> capsule r 0.75 h 3; $($seen.Count) refusals; removed"
}

Add-Check 'phys.material' $physRow 'cliext.physics material creates a 3D material with frictions, bounciness and combines, then changes one field of it, and creates a 2D one; the assets'' fields as loaded show the values' {
    New-ScratchFolder
    $p3 = "$scratchRel/CliTestBouncy.physicMaterial"; $p2 = "$scratchRel/CliTestSlide.physicsMaterial2D"
    $c = Invoke-Cli @('cliext.physics', 'material', '--path', $p3, '--set', 'dynamicFriction=0.2;staticFriction=0.3;bounciness=0.8;frictionCombine=Minimum;bounceCombine=Maximum', '--format', 'json')
    Expect ($c.exit -eq 0 -and $c.json.created -eq $true -and (@($c.json.set) -join ',') -eq 'dynamicFriction,staticFriction,bounciness,frictionCombine,bounceCombine') "create: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
    $read = 'var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.PhysicsMaterial>("{{p}}"); var ci = System.Globalization.CultureInfo.InvariantCulture; return m == null ? "none" : m.dynamicFriction.ToString(ci) + "|" + m.staticFriction.ToString(ci) + "|" + m.bounciness.ToString(ci) + "|" + m.frictionCombine + "|" + m.bounceCombine;'
    $f1 = Invoke-Eval 'phys-mat1.cs' $read.Replace('{{p}}', $p3)
    Expect ($f1 -eq '0.2|0.3|0.8|Minimum|Maximum') "the asset after create: $f1"
    $e = Invoke-Cli @('cliext.physics', 'material', '--path', $p3, '--set', 'bounciness=0.1;frictionCombine=Minimum', '--format', 'json')
    Expect ($e.exit -eq 0 -and $e.json.created -eq $false -and (@($e.json.set) -join ',') -eq 'bounciness') "change: exit $($e.exit): $(Short ($e.stdout + $e.stderr))"
    $f2 = Invoke-Eval 'phys-mat2.cs' $read.Replace('{{p}}', $p3)
    Expect ($f2 -eq '0.2|0.3|0.1|Minimum|Maximum') "the asset after the change: $f2"
    $d = Invoke-Cli @('cliext.physics', 'material', '--path', $p2, '--2d', 'true', '--set', 'friction=0.05;bounciness=0.5', '--format', 'json')
    Expect ($d.exit -eq 0 -and $d.json.created -eq $true) "2D create: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
    $f3 = Invoke-Eval 'phys-mat3.cs' "var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.PhysicsMaterial2D>(`"$p2`"); return m == null ? `"none`" : m.friction.ToString(System.Globalization.CultureInfo.InvariantCulture) + `"|`" + m.bounciness.ToString(System.Globalization.CultureInfo.InvariantCulture);"
    Expect ($f3 -eq '0.05|0.5') "the 2D asset: $f3"
    "3D $f1 -> $f2; 2D $f3"
}

Add-Check 'phys.raycast' $physRow 'in a scratch scene with a cube (Water) over a plane, cliext.physics raycast down hits the cube first, --all hits both in order, --layers Default skips the cube, --to casts toward a point; Physics.Raycast agrees' {
    $orig = Open-P3Scene
    try {
        Add-PhysTargets
        $r = Invoke-Cli @('cliext.physics', 'raycast', '--from', '[0,5,0]', '--format', 'json')
        Expect ($r.exit -eq 0 -and $r.json.gameObject -eq 'CliTestBox' -and $r.json.distance -eq 4 -and $r.json.layer -eq 'Water' -and $r.json.point -eq '[0,1,0]') "down: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
        $a = Invoke-Cli @('cliext.physics', 'raycast', '--from', '[0,5,0]', '--direction', '[0,-1,0]', '--all', 'true', '--format', 'json')
        Expect ($a.exit -eq 0 -and (@($a.json | ForEach-Object { "$($_.gameObject)@$([double]$_.distance)" }) -join ',') -eq 'CliTestBox@4,CliTestGround@5') "--all: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
        $l = Invoke-Cli @('cliext.physics', 'raycast', '--from', '[0,5,0]', '--layers', 'Default', '--format', 'json')
        Expect ($l.exit -eq 0 -and $l.json.gameObject -eq 'CliTestGround' -and $l.json.distance -eq 5) "--layers Default: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
        $t = Invoke-Cli @('cliext.physics', 'raycast', '--from', '[-5,0.5,0]', '--to', '[5,0.5,0]', '--format', 'json')
        Expect ($t.exit -eq 0 -and $t.json.gameObject -eq 'CliTestBox' -and $t.json.distance -eq 4.5 -and $t.json.normal -eq '[-1,0,0]') "--to: exit $($t.exit): $(Short ($t.stdout + $t.stderr))"
        $e = Invoke-Eval 'phys-ray.cs' 'UnityEngine.Physics.SyncTransforms(); UnityEngine.RaycastHit h; bool ok = UnityEngine.Physics.Raycast(new UnityEngine.Vector3(0, 5, 0), UnityEngine.Vector3.down, out h, 1000f); return ok ? h.collider.name + "@" + h.distance.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";'
        Expect ($e -eq 'CliTestBox@4') "Physics.Raycast: $e"
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "down: CliTestBox at 4 (Physics.Raycast $e); --all box then ground; --layers Default ground at 5; --to box at 4.5; scratch scene discarded"
}

Add-Check 'phys.errors' $physRow 'cliext.physics set_matrix, raycast and material with unknown layers, bad vectors, distances and values, a ray that hits nothing, wrong paths and extensions, a missing folder, and changes that would do nothing exit 2, 13 or 6 with the fix, and change and create nothing' {
    New-ScratchFolder
    $orig = Open-P3Scene
    $mat = "$scratchRel/CliTestErr.physicMaterial"
    try {
        Add-PhysTargets
        $mk = Invoke-Cli @('cliext.physics', 'material', '--path', $mat, '--set', 'bounciness=0.3', '--format', 'json')
        Expect ($mk.exit -eq 0) "make the material: exit $($mk.exit): $(Short ($mk.stdout + $mk.stderr))"
        $matHash = (Get-FileHash (Join-Path $projectPath ($mat -replace '/', '\'))).Hash
        $seen = Test-SettingsFailures 'cliext.physics' @(
            @(@('set_matrix', '--a', 'Defalt', '--b', 'Water', '--collide', 'false'), "--a: no layer 'Defalt'\. Did you mean: Default\? Layers: Default, .*cliext\.physics matrix", 2, 'set_matrix with a misspelled layer'),
            @(@('set_matrix', '--a', 'Default', '--b', '40', '--collide', 'false'), '--b: layer 40 is outside 0-31', 2, 'set_matrix with layer 40'),
            @(@('set_matrix', '--a', 'Default', '--b', 'Water', '--collide', 'true'), 'already collide in the 3D matrix: nothing would change', 6, 'set_matrix to what it is'),
            @(@('set_matrix', '--a', 'Default', '--b', 'Water', '--collide', 'maybe'), 'expects Boolean', 2, 'set_matrix with a non-bool'),
            @(@('raycast', '--from', '[0,5,0]', '--direction', '[0,1,0]'), 'hit nothing within 1000: aim it at a collider, raise --distance', 13, 'a ray that hits nothing'),
            @(@('raycast', '--from', '[0,5,0]', '--distance', '2'), 'hit nothing within 2', 13, 'a ray too short to hit'),
            @(@('raycast', '--from', '[0,5,0]', '--layers', 'UI'), 'hit nothing within 1000 on UI: .*change --layers', 13, 'a ray on a layer with no colliders'),
            @(@('raycast', '--from', '[0,5]'), "--from: Expected 3 numbers, got '\[0,5\]'", 2, 'raycast from a bad point'),
            @(@('raycast', '--from', '[0,5,0]', '--to', '[1,x,0]'), "--to: '\[1,x,0\]' isn't a Vector3 value: give a vector", 2, 'raycast to a point that is not numbers'),
            @(@('raycast', '--from', '[0,5,0]', '--direction', '[0,0,0]'), 'has no direction', 2, 'raycast with a zero direction'),
            @(@('raycast', '--from', '[0,5,0]', '--to', '[0,5,0]'), '--to is the same point as --from', 2, 'raycast to its own origin'),
            @(@('raycast', '--from', '[0,5,0]', '--to', '[0,0,0]', '--direction', '[0,-1,0]'), 'Give --direction or --to, not both', 2, 'raycast with --to and --direction'),
            @(@('raycast', '--from', '[0,5,0]', '--distance', '-1'), '--distance -1: give more than 0', 2, 'raycast with a negative distance'),
            @(@('raycast', '--from', '[0,5,0]', '--layers', 'Watr'), "--layers: no layer 'Watr'\. Did you mean: Water", 2, 'raycast on a misspelled layer'),
            @(@('material', '--path', 'C:/CliTest.physicMaterial'), "isn't under Assets/ or Packages/", 2, 'material outside Assets'),
            @(@('material', '--path', "$scratchRel/CliTest.mat"), 'a physics material ends in \.physicMaterial', 2, 'material with the wrong extension'),
            @(@('material', '--path', "$scratchRel/CliTest.physicsMaterial2D"), 'for a 2D one, pass --2d true', 2, 'a 2D path without --2d'),
            @(@('material', '--path', "$scratchRel/CliTest.physicMaterial", '--2d', 'true'), 'ends in \.physicsMaterial2D \(for a 3D one, leave out --2d\)', 2, '--2d with a 3D path'),
            @(@('material', '--path', "$scratchRel/NoSuchFolder/CliTest.physicMaterial"), "No folder '.*NoSuchFolder' for --path: create it first", 13, 'material in a missing folder'),
            @(@('material', '--path', "$scratchRel/CliTestNew.physicMaterial", '--set', 'bouncyness=1'), "has no 'bouncyness'\. Did you mean: bounciness\? Settings: dynamicFriction", 2, 'a misspelled material setting'),
            @(@('material', '--path', "$scratchRel/CliTestNew.physicMaterial", '--set', 'bounciness=2'), 'bounciness 2 is out of range: give 0 to 1', 2, 'bounciness 2'),
            @(@('material', '--path', "$scratchRel/CliTestNew.physicMaterial", '--set', 'dynamicFriction=abc'), "dynamicFriction: 'abc' isn't a Single value: give a number", 2, 'a friction that is not a number'),
            @(@('material', '--path', "$scratchRel/CliTestNew.physicMaterial", '--set', 'dynamicFriction=0.5;frictionCombine=Lowest'), "'Lowest' isn't a frictionCombine value\. Use one of: Average, Multiply, Minimum, Maximum", 2, 'an unknown combine (after a good value)'),
            @(@('material', '--path', "$scratchRel/CliTestNew.physicMaterial", '--set', 'bounciness'), "'bounciness' isn't name=value", 2, 'a setting without a value'),
            @(@('material', '--path', $mat), 'Nothing to change on .*pass --set', 2, 'an existing material with no --set'),
            @(@('material', '--path', $mat, '--set', 'bounciness=0.3'), 'already has bounciness 0\.3: nothing would change', 6, 'a material set to what it has')
        )
        Expect (-not (Test-Path (Join-Path $scratch 'CliTestNew.physicMaterial')) -and -not (Test-Path (Join-Path $scratch 'CliTest.mat')) -and -not (Test-Path (Join-Path $scratch 'NoSuchFolder')) -and -not (Test-Path (Join-Path $scratch 'CliTest.physicsMaterial2D'))) 'a failed material call created a file'
        Expect ((Get-FileHash (Join-Path $projectPath ($mat -replace '/', '\'))).Hash -eq $matHash) 'a failed material call changed the existing material'
    }
    finally { $closed = Close-P3Scene $orig }
    Expect ("$closed" -match "^`"?$([regex]::Escape($orig))\|False\|1") "after closing the scratch scene: $(Short $closed)"
    "$($seen.Count) failures with the fix; settings, files and the material unchanged, nothing created"
}

# --- cliext.quality ---------------------------------------------------------------------------------------------------

# Quality changes go to the project's QualitySettings and are put back in a finally from what levels read first. The
# graphics APIs are only read and refused: setting them for real can need an Editor restart, so the checks call the
# set path only where it would change nothing (exit 6) or is refused before anything changes.
$qualRow = 'cliext.quality'

Add-Check 'qual.levels-set' $qualRow 'cliext.quality set changes the current level''s shadow distance and anti-aliasing (2 is 2x), another level''s excluded platforms and a platform''s default level; levels and QualitySettings show them; put back in a finally, the settings and their files are as before' {
    $saved = Save-SettingsFiles; $sig0 = Get-SettingsSig; $git0 = Get-SettingsGit
    $l0 = Invoke-Cli @('cliext.quality', 'levels', '--format', 'json')
    Expect ($l0.exit -eq 0 -and @($l0.json.levels).Count -ge 2) "levels: exit $($l0.exit), need 2 levels: $(Short ($l0.stdout + $l0.stderr))"
    $cur = $l0.json.current
    $curLevel = @($l0.json.levels) | Where-Object { $_.settings.name -eq $cur }
    $other = @($l0.json.levels) | Where-Object { $_.settings.name -ne $cur } | Select-Object -First 1
    $origShadow = $curLevel.settings.shadowDistance; $origAa = $curLevel.settings.antiAliasing
    $origEx = @($other.excludedPlatforms)
    $plat = @($l0.json.platformDefaults.PSObject.Properties | Where-Object { $_.Value -ne $cur } | Select-Object -First 1)[0]
    Expect ($null -ne $plat) 'every platform already defaults to the current level'
    $newEx = if ($origEx.Count) { 'none' } else { 'WebGL' }
    $one = Invoke-Cli @('cliext.quality', 'levels', '--level', $other.settings.name, '--format', 'json')
    Expect ($one.exit -eq 0 -and @($one.json.levels).Count -eq 1 -and $one.json.levels[0].settings.name -eq $other.settings.name) "levels --level $($other.settings.name): $(Short $one.stdout)"
    try {
        $s = Invoke-Cli @('cliext.quality', 'set', '--level', $cur, '--set', 'shadowDistance=25.5;antiAliasing=2', '--format', 'json')
        Expect ($s.exit -eq 0 -and (@($s.json.changes) -join ';') -match 'shadowDistance: .* -> 25\.5;.*antiAliasing: .* -> 2x Multi Sampling') "set: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $x = Invoke-Cli @('cliext.quality', 'set', '--level', $other.index, '--exclude', $newEx, '--format', 'json')
        Expect ($x.exit -eq 0) "exclude: exit $($x.exit): $(Short ($x.stdout + $x.stderr))"
        $d = Invoke-Cli @('cliext.quality', 'set', '--platform', $plat.Name, '--default', $cur, '--format', 'json')
        Expect ($d.exit -eq 0 -and "$(@($d.json.changes))" -match "$([regex]::Escape($plat.Name)) default: $([regex]::Escape($plat.Value)) -> $([regex]::Escape($cur))") "default: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        $l1 = Invoke-Cli @('cliext.quality', 'levels', '--format', 'json')
        $c1 = @($l1.json.levels) | Where-Object { $_.settings.name -eq $cur }; $o1 = @($l1.json.levels) | Where-Object { $_.index -eq $other.index }
        Expect ($c1.settings.shadowDistance -eq '25.5' -and $c1.settings.antiAliasing -eq '2x Multi Sampling') "levels after set: $(Short ($c1.settings | ConvertTo-Json -Compress))"
        Expect ((@($o1.excludedPlatforms) -join ';') -eq $(if ($newEx -eq 'none') { '' } else { $newEx })) "levels after exclude: $(@($o1.excludedPlatforms) -join ';')"
        Expect ($l1.json.platformDefaults.($plat.Name) -eq $cur) "levels after default: $($plat.Name) -> $($l1.json.platformDefaults.($plat.Name))"
        $e = Invoke-Eval 'qual-api.cs' 'return UnityEngine.QualitySettings.names[UnityEngine.QualitySettings.GetQualityLevel()] + "|" + UnityEngine.QualitySettings.shadowDistance.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + UnityEngine.QualitySettings.antiAliasing;'
        Expect ($e -eq "$cur|25.5|2") "QualitySettings: $e"
    }
    finally {
        $b1 = Invoke-Cli @('cliext.quality', 'set', '--level', $cur, '--set', "shadowDistance=$origShadow;antiAliasing=$origAa", '--format', 'json')
        $b2 = Invoke-Cli @('cliext.quality', 'set', '--level', $other.index, '--exclude', $(if ($origEx.Count) { $origEx -join ';' } else { 'none' }), '--format', 'json')
        $b3 = Invoke-Cli @('cliext.quality', 'set', '--platform', $plat.Name, '--default', $plat.Value, '--format', 'json')
    }
    Expect ($b1.exit -eq 0 -and $b2.exit -eq 0 -and $b3.exit -eq 0) "set back: $($b1.exit) $(Short $b1.stderr 60); $($b2.exit) $(Short $b2.stderr 60); $($b3.exit) $(Short $b3.stderr 60)"
    $restored = Expect-SettingsBack $saved $sig0 $git0 'quality set'
    "$cur shadowDistance 25.5, antiAliasing 2x (QualitySettings $e); $($other.settings.name) excluded '$newEx'; $($plat.Name) default $cur; $restored"
}

Add-Check 'qual.graphics' $qualRow 'cliext.quality graphics reads the active and the Android graphics APIs (PlayerSettings agrees) with the render pipeline assets; --auto on an automatic platform exits 6, and unknown platforms and APIs, a repeated API and --set with --auto exit 2; nothing changes (a real API change is not made: it can need an Editor restart)' {
    $g = Invoke-Cli @('cliext.quality', 'graphics', '--format', 'json')
    Expect ($g.exit -eq 0 -and $g.json.platform -and @($g.json.apis).Count -ge 1 -and @($g.json.qualityOverrides).Count -ge 1) "graphics: exit $($g.exit): $(Short ($g.stdout + $g.stderr))"
    $e = Invoke-Eval 'qual-gfx.cs' 'var t = UnityEditor.EditorUserBuildSettings.activeBuildTarget; return t + "|" + UnityEditor.PlayerSettings.GetUseDefaultGraphicsAPIs(t) + "|" + string.Join(",", UnityEditor.PlayerSettings.GetGraphicsAPIs(t)) + "|" + string.Join(",", UnityEditor.PlayerSettings.GetGraphicsAPIs(UnityEditor.BuildTarget.Android)) + "|" + UnityEngine.QualitySettings.GetRenderPipelineAssetAt(0).name;'
    Expect ($e -eq "$($g.json.platform)|$($g.json.automatic)|$(@($g.json.apis) -join ',')|$(((Invoke-Cli @('cliext.quality', 'graphics', '--platform', 'android', '--format', 'json')).json.apis) -join ',')|$([IO.Path]::GetFileNameWithoutExtension($g.json.qualityOverrides[0].pipeline))") "PlayerSettings: $e; graphics: $(Short $g.stdout)"
    $cases = @(
        @(@('graphics', '--platform', 'Andriod'), "--platform: 'Andriod' isn't a build target\. Did you mean: Android\? Build targets: ", 2, 'a misspelled platform'),
        @(@('graphics', '--platform', '13'), "'13' isn't a build target", 2, 'a platform as a number'),
        @(@('graphics', '--platform', 'Android', '--set', 'Vulkn'), "--set: 'Vulkn' isn't a graphics API\. Did you mean: Vulkan\?", 2, 'a misspelled API'),
        @(@('graphics', '--platform', 'Android', '--set', 'Vulkan;OpenGLES3;Vulkan'), '--set lists Vulkan twice', 2, 'an API listed twice'),
        @(@('graphics', '--platform', 'Android', '--set', 'Vulkan', '--auto', 'true'), 'Give --set or --auto true, not both', 2, '--set with --auto'),
        @(@('graphics', '--auto', 'maybe'), 'expects Boolean', 2, '--auto not a bool')
    )
    if ($g.json.automatic) { $cases += , @(@('graphics', '--auto', 'true'), "$($g.json.platform) already uses Unity's default graphics APIs: nothing would change", 6, '--auto on an automatic platform') }
    else { $cases += , @(@('graphics', '--set', (@($g.json.apis) -join ';')), "already uses .*: nothing would change", 6, '--set the list it uses') }
    $seen = Test-SettingsFailures 'cliext.quality' $cases
    "$($g.json.platform): $(@($g.json.apis) -join ', ') (automatic $($g.json.automatic)); PlayerSettings agrees; $($seen.Count) refusals, nothing changed"
}

Add-Check 'qual.errors' $qualRow 'cliext.quality levels and set with unknown levels, settings and platforms, bad numbers, enums and assets, missing pairs of arguments, nothing to change and values the level has exit 2, 13 or 6 with the fix, and change nothing' {
    $l0 = Invoke-Cli @('cliext.quality', 'levels', '--format', 'json')
    $cur = $l0.json.current
    $c0 = @($l0.json.levels) | Where-Object { $_.settings.name -eq $cur }
    $seen = Test-SettingsFailures 'cliext.quality' @(
        @(@('levels', '--level', 'Ultra'), "--level: no quality level 'Ultra'\..* Levels: .*cliext\.quality levels lists them", 2, 'levels of an unknown level'),
        @(@('levels', '--level', '9'), "no quality level '9'.*Indexes are 0-", 2, 'levels of an index out of range'),
        @(@('set', '--level', ($cur + 'x'), '--set', 'shadowDistance=10'), "no quality level '$([regex]::Escape($cur))x'\. Did you mean: $([regex]::Escape($cur))\?", 2, 'set a misspelled level'),
        @(@('set', '--level', $cur, '--set', 'shadowDistnce=10'), "No setting 'shadowDistnce'\. Did you mean: shadowDistance\? Settings: ", 2, 'a misspelled setting'),
        @(@('set', '--level', $cur, '--set', 'shadowDistance=far'), "shadowDistance: 'far' isn't a Single value: give a number", 2, 'a distance that is not a number'),
        @(@('set', '--level', $cur, '--set', 'shadowDistance=10;antiAliasing=3'), "antiAliasing: '3' isn't one of: Disabled, 2x Multi Sampling", 2, 'an anti-aliasing value that does not exist (after a good distance)'),
        @(@('set', '--level', $cur, '--set', 'vSyncCount=Sometimes'), "vSyncCount: 'Sometimes' isn't one of: Don't Sync", 2, 'an unknown vSync name'),
        @(@('set', '--level', $cur, '--set', 'softParticles=maybe'), "softParticles: 'maybe' isn't a Boolean value: give true or false", 2, 'a bool that is not one'),
        @(@('set', '--level', $cur, '--set', 'customRenderPipeline=Assets/Settings/NoSuch.asset'), "customRenderPipeline: No asset at 'Assets/Settings/NoSuch\.asset'", 13, 'a render pipeline asset that is missing'),
        @(@('set', '--level', $cur, '--set', 'customRenderPipeline=Assets/Scenes/TestLevel.unity'), "customRenderPipeline: 'Assets/Scenes/TestLevel\.unity' is a SceneAsset, which customRenderPipeline can't hold", 2, 'a scene as the render pipeline'),
        @(@('set', '--level', $cur, '--set', 'excludedTargetPlatforms=Android'), "No setting 'excludedTargetPlatforms'", 2, 'excluded platforms through --set'),
        @(@('set', '--level', $cur, '--set', 'textureMipmapLimitSettings=1'), "No setting|Can't set textureMipmapLimitSettings \(a list\)", 2, 'a list setting from text'),
        @(@('set', '--level', $cur, '--set', 'shadowDistance'), "'shadowDistance' isn't name=value", 2, 'a setting without a value'),
        @(@('set', '--level', $cur, '--exclude', 'Androd'), "--exclude: no platform 'Androd'\. Did you mean: Android\? Platforms: ", 2, 'exclude a misspelled platform'),
        @(@('set', '--platform', 'Androd', '--default', $cur), "--platform: no platform 'Androd'\. Did you mean: Android", 2, 'default on a misspelled platform'),
        @(@('set', '--platform', 'Android', '--default', 'Ultra'), "--default: no quality level 'Ultra'", 2, 'an unknown default level'),
        @(@('set', '--platform', 'Android'), '--platform needs --default', 2, '--platform without --default'),
        @(@('set', '--default', $cur), '--default needs --platform', 2, '--default without --platform'),
        @(@('set', '--set', 'shadowDistance=10'), '--set and --exclude change the level named by --level: pass --level', 2, '--set without --level'),
        @(@('set', '--level', $cur), "--level ${cur}: pass --set or --exclude", 2, 'a level with nothing to change'),
        @(@('set'), 'Nothing to change: pass --level with --set or --exclude, or --platform with --default', 2, 'set with nothing'),
        @(@('set', '--level', $cur, '--set', "shadowDistance=$($c0.settings.shadowDistance)"), 'already have those values: nothing would change', 6, 'a value the level has'),
        @(@('set', '--level', $cur, '--exclude', $(if (@($c0.excludedPlatforms).Count) { @($c0.excludedPlatforms) -join ';' } else { 'none' })), 'nothing would change', 6, 'the exclusions it has')
    )
    "$($seen.Count) failures with the fix; settings and their files unchanged"
}

# --- cliext.import, cliext.presets ------------------------------------------------------------------------------------

# Import settings are changed only on copies: New-ImpTextures copies four textures of the committed VFXTexturePack
# (AssetDatabase.CopyAsset, so new GUIDs) into the scratch folder, Imp (A, B, Sub/C), ImpMixed (D and a .txt) and
# ImpText (a .txt only), with an empty ImpPresets folder for the presets. The pack's own files are checked with git
# after every check. Default presets go to ProjectSettings/PresetManager.asset: a check that sets one takes it out
# again with --remove in a finally, then writes the file's original bytes back, and compares the default presets the
# Editor holds (Get-PresetSig) and git status of ProjectSettings.
$impRow = 'cliext.import, cliext.presets'
$impRel = "$scratchRel/Imp"
$impPack = 'Assets/ThirdParty/VFXTexturePack'
$presetFiles = @('ProjectSettings/PresetManager.asset')

function New-ImpTextures {
    New-ScratchFolder
    $code = @'
string root = "Assets/_CliextTests/CliTest";
foreach (var d in new[] { "Imp", "ImpMixed", "ImpText", "ImpPresets" }) UnityEditor.AssetDatabase.DeleteAsset(root + "/" + d);
UnityEditor.AssetDatabase.CreateFolder(root, "Imp"); UnityEditor.AssetDatabase.CreateFolder(root + "/Imp", "Sub");
UnityEditor.AssetDatabase.CreateFolder(root, "ImpMixed"); UnityEditor.AssetDatabase.CreateFolder(root, "ImpText"); UnityEditor.AssetDatabase.CreateFolder(root, "ImpPresets");
string src = "{{pack}}/";
bool ok = UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_directional_01.png", root + "/Imp/A.png")
    & UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_directional_02.png", root + "/Imp/B.png")
    & UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_radial_01.png", root + "/Imp/Sub/C.png")
    & UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_radial_02.png", root + "/ImpMixed/D.png");
System.IO.File.WriteAllText(root + "/ImpMixed/Notes.txt", "scratch");
System.IO.File.WriteAllText(root + "/ImpText/Readme.txt", "scratch");
UnityEditor.AssetDatabase.ImportAsset(root + "/ImpMixed/Notes.txt");
UnityEditor.AssetDatabase.ImportAsset(root + "/ImpText/Readme.txt");
return ok ? "ok" : "copy failed";
'@
    $r = Invoke-Eval 'imp-new.cs' $code.Replace('{{pack}}', $impPack)
    Expect ($r -eq 'ok') "scratch textures: $r"
}
function Get-ImpState([string]$dir = $impRel) {
    # Every texture under $dir as name:maxTextureSize,mipmapEnabled,userData,Android overridden,Android maxTextureSize.
    $code = @'
var paths = new System.Collections.Generic.List<string>();
foreach (var g in UnityEditor.AssetDatabase.FindAssets("t:Texture2D", new[] { "{{dir}}" })) paths.Add(UnityEditor.AssetDatabase.GUIDToAssetPath(g));
paths.Sort(System.StringComparer.Ordinal);
var sb = new System.Text.StringBuilder();
foreach (var p in paths)
{
    var ti = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(p);
    var a = ti.GetPlatformTextureSettings("Android");
    sb.Append(System.IO.Path.GetFileName(p)).Append(':').Append(ti.maxTextureSize).Append(',').Append(ti.mipmapEnabled).Append(',').Append(ti.userData).Append(',').Append(a.overridden).Append(',').Append(a.maxTextureSize).Append(' ');
}
return sb.ToString().Trim();
'@
    Invoke-Eval 'imp-state.cs' $code.Replace('{{dir}}', $dir)
}
function Get-ImpMetas {
    # A hash of every .meta file in the scratch import folders.
    $all = @('Imp', 'ImpMixed', 'ImpText', 'ImpPresets') | ForEach-Object { Join-Path $scratch $_ } | Where-Object { Test-Path $_ } |
        ForEach-Object { Get-ChildItem $_ -Recurse -File } | Sort-Object FullName | ForEach-Object { "$($_.Name)=$((Get-FileHash $_.FullName).Hash)" }
    ($all -join ';')
}
function Get-PackGit {
    if (-not (Test-Path (Join-Path $projectPath '.git'))) { return '(no git)' }
    $ErrorActionPreference = 'Continue'
    "$(& git -C $projectPath status --porcelain -- $impPack 2>$null)"
}
function Expect-PackClean([string]$what) { $g = Get-PackGit; Expect ($g -eq '' -or $g -eq '(no git)') "${what}: the committed pack changed: $g" }
function Save-PresetFiles {
    $h = @{}
    foreach ($f in $presetFiles) { $p = Join-Path $projectPath ($f -replace '/', '\'); if (Test-Path $p) { $h[$f] = [IO.File]::ReadAllBytes($p) } }
    $h
}
function Get-PresetSig {
    # The default presets the Editor holds, as type|preset|filter|enabled; (quotes in filters as ').
    $code = @'
var sb = new System.Text.StringBuilder();
foreach (var t in UnityEditor.Presets.Preset.GetAllDefaultTypes())
    foreach (var d in UnityEditor.Presets.Preset.GetDefaultPresetsForType(t))
        sb.Append(t.GetManagedTypeName()).Append('|').Append(d.preset == null ? "null" : UnityEditor.AssetDatabase.GetAssetPath(d.preset)).Append('|').Append(d.filter).Append('|').Append(d.enabled).Append(';');
return sb.Length == 0 ? "(none)" : sb.ToString().Replace('"', '\'');
'@
    Invoke-Eval 'preset-sig.cs' $code
}
function Expect-PresetsBack($saved, [string]$sig0, [string]$git0, [string]$what) {
    $sig1 = Get-PresetSig
    $put = Restore-SettingsFiles $saved
    Expect ($sig1 -eq $sig0) "${what}: the default presets were not put back ($sig0 -> $sig1)"
    Expect ((Get-SettingsGit) -eq $git0) "${what}: git status of ProjectSettings changed"
    $(if ($put.Count) { "defaults restored; $($put -join ', ') written back" } else { 'defaults restored, PresetManager.asset untouched' })
}
function Test-ImpFailures([string]$group, [object[]]$cases) {
    # Each case: args, hint regex, exit, what. Importer settings, .meta files, default presets and their file, and the
    # committed pack must not change.
    $state0 = "$(Get-ImpState $scratchRel)"; $metas0 = Get-ImpMetas; $saved = Save-PresetFiles; $sig0 = Get-PresetSig; $git0 = Get-SettingsGit
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli (@($group) + $c[0] + @('--format', 'json')) 60
        Expect-P3Failure $x $c[1] $c[3] $c[2]
        $seen += "$($c[3]) $($x.exit)"
    }
    Expect ("$(Get-ImpState $scratchRel)" -eq $state0) "a failed $group call changed import settings: $state0 -> $(Get-ImpState $scratchRel)"
    Expect ((Get-ImpMetas) -eq $metas0) "a failed $group call changed a .meta or file in the scratch folders"
    Expect ((Get-PresetSig) -eq $sig0) "a failed $group call changed the default presets"
    $put = Restore-SettingsFiles $saved
    Expect ($put.Count -eq 0) "a failed $group call wrote $($put -join ', ')"
    Expect ((Get-SettingsGit) -eq $git0) "a failed $group call changed git status of ProjectSettings"
    Expect-PackClean "a failed $group call"
    , $seen
}

Add-Check 'imp.batch' $impRow 'cliext.import batch sets maxTextureSize and mipmapEnabled on the three scratch textures (subfolder included), then an Android override; TextureImporter and GetPlatformTextureSettings show them, the committed pack does not change' {
    New-ImpTextures
    $s0 = Get-ImpState
    Expect ($s0 -match '^A\.png:2048,True,,False,\d+ B\.png:2048,True,,False,\d+ C\.png:2048,True,,False,\d+$') "scratch textures start at 2048, mipmaps on, no override: $s0"
    $b = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--type', 'Texture2D', '--set', 'maxTextureSize=512;mipmapEnabled=false', '--format', 'json')
    Expect ($b.exit -eq 0 -and @($b.json).Count -eq 3 -and (@($b.json)[0].changes -join ';') -eq 'maxTextureSize: 2048 -> 512;mipmapEnabled: true -> false') "batch: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
    $s1 = Get-ImpState
    Expect ($s1 -match '^A\.png:512,False,,False,\d+ B\.png:512,False,,False,\d+ C\.png:512,False,,False,\d+$') "TextureImporter after batch: $s1"
    $p = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--type', 'Texture2D', '--filter', 'B.png', '--platform', 'Android', '--set', 'maxTextureSize=256', '--format', 'json')
    Expect ($p.exit -eq 0 -and @($p.json).Count -eq 1 -and (@($p.json)[0].changes -join ';') -match 'Android maxTextureSize: \d+ -> 256;Android overridden: false -> true') "platform batch: exit $($p.exit): $(Short ($p.stdout + $p.stderr))"
    $s2 = Get-ImpState
    Expect ($s2 -match '^A\.png:512,False,,False,\d+ B\.png:512,False,,True,256 C\.png:512,False,,False,\d+$') "after the Android override: $s2"
    Expect-PackClean 'batch'
    "$s0 -> $s2; pack unchanged"
}

Add-Check 'imp.batch-dry-run' $impRow 'cliext.import batch --dry-run true lists each asset''s changes and changes nothing (importer values and .meta files as before); the same set on values the assets have exits 6' {
    New-ImpTextures
    $s0 = Get-ImpState; $m0 = Get-ImpMetas
    $d = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--type', 'Texture2D', '--set', 'maxTextureSize=1024;textureType=Sprite', '--dry-run', 'true', '--format', 'json')
    Expect ($d.exit -eq 0 -and @($d.json).Count -eq 3 -and (@($d.json)[2].changes -join ';') -eq 'maxTextureSize: 2048 -> 1024;textureType: Default -> Sprite' -and "$($d.stderr)$($d.stdout)" -notmatch 'error') "dry run: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
    Expect ((Get-ImpState) -eq $s0) "a dry run changed import settings: $(Get-ImpState)"
    Expect ((Get-ImpMetas) -eq $m0) 'a dry run changed a .meta file'
    $n = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--type', 'Texture2D', '--set', 'maxTextureSize=2048;mipmapEnabled=true', '--format', 'json')
    Expect-P3Failure $n 'already have maxTextureSize=2048;mipmapEnabled=true: nothing would change' 'a set the assets have' 6
    Expect ((Get-ImpMetas) -eq $m0) 'a set that changes nothing wrote a .meta file'
    Expect-PackClean 'dry run'
    "3 assets would change (maxTextureSize, textureType); nothing changed; values they have exit 6"
}

Add-Check 'imp.batch-errors' $impRow 'cliext.import batch with a missing or outside folder, a file as the folder, an unknown type, nothing matching, unknown settings, bad numbers, bools, enums and texture sizes, an unknown platform, a mixed folder and a platform on a non-texture exits 13 or 2 with the fix, and changes nothing' {
    New-ImpTextures
    $seen = Test-ImpFailures 'cliext.import' @(
        @(@('batch', '--folder', 'Assets/NoSuchFolder', '--set', 'maxTextureSize=1024'), "--folder: no folder 'Assets/NoSuchFolder'\. Check the path .*asset create_folder", 13, 'a missing folder'),
        @(@('batch', '--folder', 'C:/Temp', '--set', 'maxTextureSize=1024'), "--folder 'C:/Temp' isn't under Assets/ or Packages/", 2, 'a folder outside the project'),
        @(@('batch', '--folder', "$impRel/A.png", '--set', 'maxTextureSize=1024'), "is a file, not a folder: give the folder that holds it .*--filter A\.png", 2, 'a file as the folder'),
        @(@('batch', '--folder', $impRel, '--type', 'Texure2D', '--set', 'maxTextureSize=1024'), "--type: no asset type 'Texure2D'\. Did you mean: Texture2D", 2, 'a misspelled type'),
        @(@('batch', '--folder', $impRel, '--filter', '*.tga', '--set', 'maxTextureSize=1024'), "No assets under '.*Imp' matching --filter \*\.tga: nothing to set", 13, 'a filter nothing matches'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTexureSize=1024'), "TextureImporter has no setting 'maxTexureSize'\. Did you mean: maxTextureSize\? Settings: .*mipmapEnabled", 2, 'a misspelled setting'),
        @(@('batch', '--folder', $impRel, '--set', 'globalScale=2'), "TextureImporter has no setting 'globalScale'", 2, 'a model setting on textures'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTextureSize=big'), "maxTextureSize: 'big' isn't a Int32 value: give a number", 2, 'a size that is not a number'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTextureSize=1000'), 'maxTextureSize: 1000 isn''t a texture size: give one of 32, 64', 2, 'a size that is not a power of two'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTextureSize=1024;mipmapEnabled=maybe'), "mipmapEnabled: 'maybe' isn't a Boolean value: give true or false", 2, 'a bool that is not one (after a good size)'),
        @(@('batch', '--folder', $impRel, '--set', 'textureType=Normal'), "textureType: 'Normal' isn't a TextureImporterType\. Did you mean: NormalMap\? Use one of: Default, NormalMap", 2, 'an enum name that is close'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTextureSize'), "'maxTextureSize' isn't name=value", 2, 'a setting without a value'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTextureSize=512;MaxTextureSize=256'), '--set names MaxTextureSize twice', 2, 'a setting given twice'),
        @(@('batch', '--folder', $impRel, '--platform', 'Andriod', '--set', 'maxTextureSize=512'), "--platform: no texture platform 'Andriod'\. Did you mean: Android\? Platforms: ", 2, 'a misspelled platform'),
        @(@('batch', '--folder', "$scratchRel/ImpMixed", '--set', 'maxTextureSize=512'), "Notes\.txt' \(a TextAsset\) has no setting 'maxTextureSize', which the TextureImporter assets in the batch have: narrow the batch with --type Texture2D or --filter\. Nothing was changed", 2, 'a mixed folder: the .txt after the texture'),
        @(@('batch', '--folder', "$scratchRel/ImpMixed", '--platform', 'Android', '--set', 'maxTextureSize=512'), '--platform overrides are for textures, and .*Notes\.txt.*add --type Texture2D', 2, 'a platform override on a mixed folder'),
        @(@('batch', '--folder', $impRel, '--set', 'maxTextureSize=1024', '--dry-run', 'maybe'), 'expects Boolean|maybe', 2, '--dry-run not a bool')
    )
    "$($seen.Count) refusals with the fix; nothing changed"
}

Add-Check 'imp.batch-rollback' $impRow 'cliext.import batch that fails part-way (B.png.meta locked against writes while A and C change) exits 6, says the batch was put back, and leaves every importer value and .meta file as before' {
    New-ImpTextures
    $s0 = Get-ImpState; $m0 = Get-ImpMetas
    $meta = Join-Path $scratch 'Imp\B.png.meta'
    $lock = [IO.File]::Open($meta, 'Open', 'Read', 'Read')
    try { $r = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--type', 'Texture2D', '--set', 'maxTextureSize=1024', '--format', 'json') 90 }
    finally { $lock.Close() }
    Expect-P3Failure $r "Unity didn't keep maxTextureSize = 1024 on '.*B\.png'.*put back as they were.*nothing was changed" 'a batch with a locked .meta' 6
    Expect ("$($r.stdout)" -notmatch 'changes') "a failed batch still listed changes: $(Short $r.stdout)"
    $s1 = Get-ImpState
    Expect ($s1 -eq $s0) "the batch was not put back: $s0 -> $s1"
    Expect ((Get-ImpMetas) -eq $m0) 'the batch left a .meta file changed'
    Expect-PackClean 'rollback'
    "exit 6: $(Short $r.stderr 110); $s1"
}

Add-Check 'imp.presets-defaults' $impRow 'cliext.presets create makes a preset from A.png, set_default makes it the default for the Imp folder (Preset.GetDefaultPresetsForType and list show it), audit names B (changed by batch) and not A, exit 6; set_default again exits 6; removed in a finally, PresetManager.asset as before' {
    New-ImpTextures
    $preset = "$scratchRel/ImpPresets/Tex2048.preset"
    $c = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $preset, '--format', 'json')
    Expect ($c.exit -eq 0 -and $c.json.preset -eq $preset -and $c.json.type -eq 'UnityEditor.TextureImporter') "create: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
    $e = Invoke-Eval 'preset-load.cs' "var p = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Presets.Preset>(`"$preset`"); return p == null ? `"none`" : p.GetPresetType().GetManagedTypeName() + `"|`" + p.CanBeAppliedTo(UnityEditor.AssetImporter.GetAtPath(`"$impRel/B.png`"));"
    Expect ($e -eq 'UnityEditor.TextureImporter|True') "the saved preset: $e"
    $b = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--filter', 'B.png', '--set', 'maxTextureSize=256', '--format', 'json')
    Expect ($b.exit -eq 0) "batch B: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
    $saved = Save-PresetFiles; $sig0 = Get-PresetSig; $git0 = Get-SettingsGit
    try {
        $s = Invoke-Cli @('cliext.presets', 'set_default', '--preset', $preset, '--filter', $impRel, '--format', 'json')
        Expect ($s.exit -eq 0 -and $s.json.type -eq 'UnityEditor.TextureImporter' -and @($s.json.defaults)[0].preset -eq $preset -and @($s.json.defaults)[0].filter -eq "glob:`"$impRel/**`"") "set_default: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $sig1 = Get-PresetSig
        Expect ($sig1 -like "*UnityEditor.TextureImporter|$preset|glob:'$impRel/**'|True;*") "Preset.GetDefaultPresetsForType: $sig1"
        $presetGuid = ((Get-Content (Join-Path $projectPath ($preset + '.meta')) | Where-Object { $_ -match '^guid:' }) -replace '^guid:\s*', '').Trim()
        $pmText = [IO.File]::ReadAllText((Join-Path $projectPath 'ProjectSettings\PresetManager.asset'))
        Expect ($presetGuid -and $pmText.Contains($presetGuid)) "set_default did not write ProjectSettings/PresetManager.asset (no guid $presetGuid in it)"
        $l = Invoke-Cli @('cliext.presets', 'list', '--format', 'json')
        Expect ($l.exit -eq 0 -and @(@($l.json) | Where-Object { $_.preset -eq $preset -and $_.enabled }).Count -eq 1) "list: $(Short $l.stdout)"
        $again = Invoke-Cli @('cliext.presets', 'set_default', '--preset', $preset, '--filter', $impRel, '--format', 'json')
        Expect-P3Failure $again 'is already the first default for UnityEditor.TextureImporter with filter .*nothing would change' 'set_default twice' 6
        $a = Invoke-Cli @('cliext.presets', 'audit', '--folder', $impRel, '--format', 'json')
        Expect-P3Failure $a '1 of 3 asset\(s\) under .* with default presets break them\. Fix them with cliext\.presets apply' 'audit with B changed' 6
        $rec = @($a.json)
        Expect ($rec.Count -eq 1 -and $rec[0].path -eq "$impRel/B.png" -and (@($rec[0].differences) -join ';') -match 'm_MaxTextureSize: is 256, preset 2048') "audit records: $(Short $a.stdout)"
        $ok = Invoke-Cli @('cliext.presets', 'audit', '--folder', "$impRel/Sub", '--format', 'json')
        Expect ($ok.exit -eq 0 -and $ok.json.audited -eq 1 -and $ok.json.breaking -eq 0 -and @($ok.json.presets) -contains $preset -and $ok.json.note -match '0 of 1 asset') "audit of Sub (C matches) prints a summary record: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
    }
    finally { $rm = Invoke-Cli @('cliext.presets', 'set_default', '--preset', $preset, '--remove', 'true', '--format', 'json') }
    Expect ($rm.exit -eq 0 -and @($rm.json.defaults | Where-Object { $_.preset -eq $preset }).Count -eq 0) "remove: exit $($rm.exit): $(Short ($rm.stdout + $rm.stderr))"
    $rm2 = Invoke-Cli @('cliext.presets', 'set_default', '--preset', $preset, '--remove', 'true', '--format', 'json')
    Expect-P3Failure $rm2 "isn't a default preset: nothing would change\. cliext\.presets list lists the defaults" 'remove twice' 6
    $restored = Expect-PresetsBack $saved $sig0 $git0 'set_default'
    Expect-PackClean 'presets'
    "preset $($c.json.properties) properties; default for $impRel; audit: B maxTextureSize 256 vs 2048; $restored"
}

Add-Check 'imp.presets-only' $impRow 'cliext.presets create --only keeps the named properties and the whole platform settings block (the rest are the preset''s excludedProperties), --exclude leaves properties out, a misspelled name exits 2 with Did you mean and makes no file; list with no defaults and audit of a folder no preset covers print a summary record' {
    New-ImpTextures
    $only = "$scratchRel/ImpPresets/Only.preset"; $ex = "$scratchRel/ImpPresets/Exclude.preset"; $bad = "$scratchRel/ImpPresets/Bad.preset"
    $o = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $only, '--only', 'textureType;enableMipMap;maxTextureSize', '--format', 'json')
    Expect ($o.exit -eq 0 -and $o.json.platformSettings -eq 'kept' -and @($o.json.excluded) -contains 'm_sRGBTexture' -and @($o.json.excluded) -notcontains 'm_PlatformSettings' -and $o.json.kept -gt 3) "create --only: exit $($o.exit): $(Short ($o.stdout + $o.stderr))"
    $code = @'
var p = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Presets.Preset>("{{p}}");
var ex = new System.Collections.Generic.HashSet<string>(p.excludedProperties);
return (ex.Contains("m_sRGBTexture") && ex.Contains("m_TextureSettings") && !ex.Contains("m_MaxTextureSize") && !ex.Contains("m_EnableMipMap") && !ex.Contains("m_TextureType") && !ex.Contains("m_PlatformSettings")) ? "ok" : string.Join(",", p.excludedProperties);
'@
    $e = Invoke-Eval 'preset-only.cs' $code.Replace('{{p}}', $only)
    Expect ($e -eq 'ok') "the --only preset's excludedProperties: $e"
    $x = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $ex, '--exclude', 'sRGBTexture;alphaUsage;swizzle', '--format', 'json')
    Expect ($x.exit -eq 0 -and (@($x.json.excluded) -join ';') -eq 'm_sRGBTexture;m_AlphaUsage;m_Swizzle' -and $x.json.platformSettings -eq 'kept') "create --exclude: exit $($x.exit): $(Short ($x.stdout + $x.stderr))"
    $b = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $bad, '--only', 'maxTexureSize', '--format', 'json')
    Expect-P3Failure $b "--only: the preset has no property 'maxTexureSize'\. Did you mean: m_MaxTextureSize\?" 'a misspelled --only' 2
    $b2 = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $bad, '--exclude', 'm_PlatformSettings.Array.data[0].m_MaxTextureSize', '--format', 'json')
    Expect-P3Failure $b2 'keeps or excludes whole: name m_PlatformSettings' 'a property inside the platform settings' 2
    Expect (-not (Test-Path (Join-Path $scratch 'ImpPresets\Bad.preset'))) 'a refused create made a file'
    $a = Invoke-Cli @('cliext.presets', 'audit', '--folder', $impRel, '--format', 'json')
    Expect ($a.exit -eq 0 -and $a.json.breaking -eq 0 -and $null -ne $a.json.note) "audit prints a summary record: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
    $note = 'list has defaults (empty-list case not checked)'
    if ((Get-PresetSig) -eq '(none)') {
        $l = Invoke-Cli @('cliext.presets', 'list', '--format', 'json')
        Expect ($l.exit -eq 0 -and $l.json.defaults -eq 0 -and $l.json.note -match 'No default presets') "an empty list says so: exit $($l.exit): $(Short ($l.stdout + $l.stderr))"
        $note = 'empty list says no default presets'
    }
    Expect-PackClean 'presets --only'
    "--only kept $($o.json.kept) of $($o.json.properties) (platform block kept); --exclude 3; misspelling refused; audit: $(Short $a.json.note 60); $note"
}

Add-Check 'imp.presets-apply' $impRow 'cliext.presets apply --dry-run lists B and C (changed by batch) and changes nothing; apply sets them back to the preset (TextureImporter shows 2048, mipmaps on) and keeps B''s per-asset userData; apply again exits 6' {
    New-ImpTextures
    $preset = "$scratchRel/ImpPresets/Tex2048.preset"
    $c = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $preset, '--format', 'json')
    Expect ($c.exit -eq 0) "create: exit $($c.exit): $(Short ($c.stdout + $c.stderr))"
    foreach ($n in 'B.png', 'C.png') {
        $b = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--filter', $n, '--set', 'maxTextureSize=256;mipmapEnabled=false', '--format', 'json')
        Expect ($b.exit -eq 0) "batch ${n}: exit $($b.exit): $(Short ($b.stdout + $b.stderr))"
    }
    $u = Invoke-Cli @('cliext.import', 'batch', '--folder', $impRel, '--filter', 'B.png', '--set', 'userData=keep-me', '--format', 'json')
    Expect ($u.exit -eq 0) "userData: exit $($u.exit): $(Short ($u.stdout + $u.stderr))"
    $s0 = Get-ImpState; $m0 = Get-ImpMetas
    Expect ($s0 -match '^A\.png:2048,True,,False,\d+ B\.png:256,False,keep-me,False,\d+ C\.png:256,False,,False,\d+$') "before apply: $s0"
    $d = Invoke-Cli @('cliext.presets', 'apply', '--preset', $preset, '--folder', $impRel, '--dry-run', 'true', '--format', 'json')
    Expect ($d.exit -eq 0 -and (@($d.json).path -join ',') -eq "$impRel/B.png,$impRel/Sub/C.png" -and (@(@($d.json)[0].differences) -join ';') -match 'm_MaxTextureSize: is 256, preset 2048' -and (@(@($d.json)[0].differences) -join ';') -notmatch 'm_UserData') "dry run: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
    Expect ((Get-ImpState) -eq $s0 -and (Get-ImpMetas) -eq $m0) 'a dry run changed something'
    $a = Invoke-Cli @('cliext.presets', 'apply', '--preset', $preset, '--folder', $impRel, '--format', 'json')
    Expect ($a.exit -eq 0 -and @($a.json).Count -eq 2) "apply: exit $($a.exit): $(Short ($a.stdout + $a.stderr))"
    $s1 = Get-ImpState
    Expect ($s1 -match '^A\.png:2048,True,,False,\d+ B\.png:2048,True,keep-me,False,\d+ C\.png:2048,True,,False,\d+$') "after apply: $s1"
    $again = Invoke-Cli @('cliext.presets', 'apply', '--preset', $preset, '--folder', $impRel, '--format', 'json')
    Expect-P3Failure $again "All 3 asset\(s\) under .* that take '.*Tex2048\.preset' already match it: nothing would change" 'apply twice' 6
    Expect-PackClean 'apply'
    "$s0 -> $s1 (userData kept)"
}

Add-Check 'imp.presets-errors' $impRow 'cliext.presets create, set_default, apply and audit with missing or wrong-type presets, assets and folders, paths outside the project, an existing --out, a preset for another importer, a folder nothing takes it in and a bad --limit exit 13, 2 or 6 with the fix, and change nothing' {
    New-ImpTextures
    $preset = "$scratchRel/ImpPresets/Tex.preset"; $txtPreset = "$scratchRel/ImpPresets/Text.preset"
    $c1 = Invoke-Cli @('cliext.presets', 'create', '--from', "$impRel/A.png", '--out', $preset, '--format', 'json')
    $c2 = Invoke-Cli @('cliext.presets', 'create', '--from', "$scratchRel/ImpText/Readme.txt", '--out', $txtPreset, '--format', 'json')
    Expect ($c1.exit -eq 0 -and $c2.exit -eq 0 -and $c2.json.type -match 'TextScriptImporter') "presets: $($c1.exit) $($c2.exit) $(Short ($c2.stdout + $c2.stderr))"
    $seen = Test-ImpFailures 'cliext.presets' @(
        @(@('create', '--from', "$impRel/Nope.png", '--out', "$scratchRel/ImpPresets/X.preset"), "--from: no asset at '.*Nope\.png'\. Check the path", 13, 'create from a missing asset'),
        @(@('create', '--from', "$impRel/Sub", '--out', "$scratchRel/ImpPresets/X.preset"), "--from '.*Sub' is a folder: give an asset in it", 2, 'create from a folder'),
        @(@('create', '--from', "$impRel/A.png", '--out', 'Assets/NoSuchFolder/X.preset'), "--out: no folder 'Assets/NoSuchFolder'\. Create it first \(asset create_folder", 13, 'create into a missing folder'),
        @(@('create', '--from', "$impRel/A.png", '--out', 'C:/Temp/X.preset'), "--out 'C:/Temp/X\.preset' isn't under Assets/ or Packages/", 2, 'create outside the project'),
        @(@('create', '--from', "$impRel/A.png", '--out', $preset), "--out '.*Tex\.preset' already exists: pick another path, or delete it first", 6, 'create over an existing preset'),
        @(@('set_default', '--preset', "$scratchRel/ImpPresets/None.preset"), "--preset: no preset at '.*None\.preset'\. Make one with cliext\.presets create", 13, 'set_default of a missing preset'),
        @(@('set_default', '--preset', "$impRel/A.png"), "--preset '.*A\.png' is a Texture2D, not a Preset", 2, 'set_default of a texture'),
        @(@('set_default', '--preset', $preset, '--filter', 'Assets/NoSuchFolder'), "--filter: no folder 'Assets/NoSuchFolder'\. Create it .*or pass a search filter", 13, 'set_default for a missing folder'),
        @(@('set_default', '--preset', $preset, '--filter', 'D:/Art'), "--filter 'D:/Art' isn't under Assets/ or Packages/", 2, 'set_default for a folder outside the project'),
        @(@('set_default', '--preset', $preset, '--remove', 'true'), "isn't a default preset: nothing would change", 6, 'remove a preset that is not a default'),
        @(@('apply', '--preset', "$scratchRel/ImpPresets/None.preset", '--folder', $impRel), "--preset: no preset at", 13, 'apply a missing preset'),
        @(@('apply', '--preset', "$impRel/A.png", '--folder', $impRel), 'is a Texture2D, not a Preset', 2, 'apply a texture as the preset'),
        @(@('apply', '--preset', $preset, '--folder', 'Assets/NoSuchFolder'), "--folder: no folder 'Assets/NoSuchFolder'", 13, 'apply to a missing folder'),
        @(@('apply', '--preset', $preset, '--folder', $impRel, '--filter', '*.tga'), 'No assets under .* matching --filter \*\.tga: nothing to apply the preset to', 13, 'apply where nothing matches'),
        @(@('apply', '--preset', $txtPreset, '--folder', $impRel), "is a preset of UnityEditor\.TextScriptImporter, and no asset under .* takes it \(they use TextureImporter\): give a preset made from one of them", 2, 'a preset for another importer type'),
        @(@('apply', '--preset', $preset, '--folder', "$scratchRel/ImpText"), 'no asset under .*ImpText.* takes it \(they use TextScriptImporter\)', 2, 'a texture preset on a folder of text'),
        @(@('audit', '--folder', 'Assets/NoSuchFolder'), "--folder: no folder 'Assets/NoSuchFolder'", 13, 'audit a missing folder'),
        @(@('audit', '--folder', 'C:/Temp'), "isn't under Assets/ or Packages/", 2, 'audit outside the project'),
        @(@('audit', '--folder', $impRel, '--limit', '0'), '--limit 0: give 1 or more', 2, 'audit with --limit 0')
    )
    "$($seen.Count) refusals with the fix; nothing changed"
}

# --- cliext.package ---------------------------------------------------------------------------------------------------

# The package is made here: AssetDatabase.ExportPackage of a scratch folder (a texture copy and a .txt) into the temp
# work folder; the folder is then deleted, so the import has to create it, with the same GUIDs. The imported folder
# is deleted at the end of each check (and with the scratch folder at the end of the run). Unity's own import stops
# at a "Missing signature" dialog for an unsigned package: the checks count those in the dialog journal.
$pkgRow = 'cliext.package'
$pkgRel = "$scratchRel/PkgSrc"
$pkgFile = Join-Path $work 'cliext-test.unitypackage'

function New-TestPackage {
    New-ScratchFolder
    $code = @'
string dir = "{{dir}}";
UnityEditor.AssetDatabase.DeleteAsset(dir);
UnityEditor.AssetDatabase.CreateFolder("Assets/_CliextTests/CliTest", "PkgSrc");
UnityEditor.AssetDatabase.CopyAsset("{{pack}}/Tx_Burst_radial_03.png", dir + "/PkgTex.png");
System.IO.File.WriteAllText(dir + "/PkgNotes.txt", "from the package");
UnityEditor.AssetDatabase.ImportAsset(dir + "/PkgNotes.txt");
UnityEditor.AssetDatabase.ExportPackage(dir, @"{{out}}", UnityEditor.ExportPackageOptions.Recurse);
string guids = UnityEditor.AssetDatabase.AssetPathToGUID(dir) + "," + UnityEditor.AssetDatabase.AssetPathToGUID(dir + "/PkgTex.png") + "," + UnityEditor.AssetDatabase.AssetPathToGUID(dir + "/PkgNotes.txt");
UnityEditor.AssetDatabase.DeleteAsset(dir);
return guids;
'@
    $g = Invoke-Eval 'pkg-new.cs' $code.Replace('{{dir}}', $pkgRel).Replace('{{pack}}', $impPack).Replace('{{out}}', $pkgFile)
    Expect ((Test-Path $pkgFile) -and $g -match '^[0-9a-f]{32},[0-9a-f]{32},[0-9a-f]{32}$') "test package: $g"
    Expect (-not (Test-Path (Join-Path $scratch 'PkgSrc'))) 'the exported folder was not deleted'
    $g
}
function Get-PkgGuids {
    Invoke-Eval 'pkg-guids.cs' "string d = `"$pkgRel`"; return UnityEditor.AssetDatabase.AssetPathToGUID(d) + `",`" + UnityEditor.AssetDatabase.AssetPathToGUID(d + `"/PkgTex.png`") + `",`" + UnityEditor.AssetDatabase.AssetPathToGUID(d + `"/PkgNotes.txt`") + `"|`" + (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(d + `"/PkgTex.png`") != null);"
}
function Get-SignatureDialogs {
    $j = Join-Path $projectPath 'Library\CLIExtensions\dialogs.jsonl'
    if (-not (Test-Path $j)) { return 0 }
    @(Get-Content $j | Where-Object { $_ -match '"title":"Missing signature"' -and $_ -notmatch '"detail":"MissingSignatureAnswer' }).Count
}
function Remove-PkgImport { [void](Invoke-Eval 'pkg-remove.cs' "UnityEditor.AssetDatabase.DeleteAsset(`"$pkgRel`"); return `"ok`";") }

Add-Check 'pkg.import' $pkgRow 'cliext.package import of a package exported from scratch: --dry-run lists the folder, texture and .txt as created and writes nothing; the import creates them with the package''s GUIDs (AssetDatabase and disk), opens no dialog and returns in time; a second import exits 6; a changed file is listed as replaced; deleted after' {
    $guids = New-TestPackage
    $dialogs0 = Get-SignatureDialogs
    try {
        $d = Invoke-Cli @('cliext.package', 'import', '--path', $pkgFile, '--dry-run', 'true', '--format', 'json') 60
        Expect ($d.exit -eq 0 -and $d.json.dryRun -eq $true -and (@($d.json.created) -join ',') -eq "$pkgRel,$pkgRel/PkgNotes.txt,$pkgRel/PkgTex.png") "dry run: exit $($d.exit): $(Short ($d.stdout + $d.stderr))"
        Expect (-not (Test-Path (Join-Path $scratch 'PkgSrc'))) 'a dry run wrote the package''s files'
        $i = Invoke-Cli @('cliext.package', 'import', '--path', $pkgFile, '--format', 'json') 60
        Expect ($i.exit -ne 124) 'import timed out (a dialog?)'
        Expect ($i.exit -eq 0 -and $i.json.assets -eq 3 -and @($i.json.created).Count -eq 3) "import: exit $($i.exit): $(Short ($i.stdout + $i.stderr))"
        Expect ((Test-Path (Join-Path $scratch 'PkgSrc\PkgTex.png')) -and (Test-Path (Join-Path $scratch 'PkgSrc\PkgNotes.txt.meta'))) 'the files are not on disk'
        $g = Get-PkgGuids
        Expect ($g -eq "$guids|True") "AssetDatabase after import: $g, package GUIDs $guids"
        Expect ((Get-SignatureDialogs) -eq $dialogs0) 'a Missing signature dialog opened'
        $again = Invoke-Cli @('cliext.package', 'import', '--path', $pkgFile, '--format', 'json') 60
        Expect-P3Failure $again "All 3 asset\(s\) in '.*cliext-test\.unitypackage' are already in the project, unchanged: nothing would change" 'import twice' 6
        [IO.File]::WriteAllText((Join-Path $scratch 'PkgSrc\PkgNotes.txt'), 'changed here')
        $r = Invoke-Cli @('cliext.package', 'import', '--path', $pkgFile, '--format', 'json') 60
        Expect ($r.exit -eq 0 -and (@($r.json.replaced) -join ',') -eq "$pkgRel/PkgNotes.txt" -and $r.json.unchanged -eq 2) "reimport over a changed file: exit $($r.exit): $(Short ($r.stdout + $r.stderr))"
        Expect ([IO.File]::ReadAllText((Join-Path $scratch 'PkgSrc\PkgNotes.txt')) -eq 'from the package') 'the changed file was not replaced'
        $st = Invoke-Cli @('cliext', 'status', '--format', 'json') 30
        Expect ($st.exit -eq 0) "the Editor stopped answering after the import: $(Short ($st.stdout + $st.stderr))"
    }
    finally { Remove-PkgImport }
    Expect (-not (Test-Path (Join-Path $scratch 'PkgSrc'))) 'the imported folder was not deleted'
    Expect-PackClean 'package import'
    "created 3 with the package's GUIDs in $($i.ms) ms, no dialog; again exit 6; changed .txt replaced; removed"
}

Add-Check 'pkg.signature-watchdog' $pkgRow 'Unity''s own AssetDatabase.ImportPackage of the unsigned test package opens "Missing signature"; watchdog -Status reports it as a unity-modal with the missing-signature rule (focus) and a note naming cliext.package import; -Click "Import Anyway" brings it to the front so MissingSignatureAnswer answers it (journal), the import lands and the Editor answers again; deleted after' {
    $guids = New-TestPackage
    $wd = Join-Path $tools 'Watchdog\cliext-watchdog.ps1'
    $t0 = [DateTime]::UtcNow
    try {
        $code = "UnityEditor.AssetDatabase.ImportPackage(@`"$pkgFile`", false); return `"returned`";"
        $x = Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'pkg-unity-import.cs' $code), '--timeout', '6', '--format', 'json') 60
        $seen = 'answered before the status'
        $st = (& powershell -NoProfile -ExecutionPolicy Bypass -File $wd -Project $projectPath -Status | Out-String) | ConvertFrom-Json
        $d = @($st.dialogs) | Where-Object { $_.title -eq 'Missing signature' } | Select-Object -First 1
        if ($d) {
            Expect ($d.kind -eq 'unity-modal' -and $d.rule -eq 'missing-signature' -and $d.action -eq 'focus' -and $st.state -eq 'blocked-by-dialog') "status of the dialog: $(Short ($d | ConvertTo-Json -Compress))"
            Expect ("$($d.note)" -match 'unity command cliext\.package import --path' -and "$($d.note)" -match 'Import Anyway or Reimport Anyway, Cancel, Learn More') "the dialog's note: $(Short $d.note)"
            $c = (& powershell -NoProfile -ExecutionPolicy Bypass -File $wd -Project $projectPath -Click 'Import Anyway' 2>&1 | Out-String)
            $seen = 'seen by -Status, answered after -Click'
            Expect ($c -notmatch 'still open|No button') "-Click Import Anyway: $(Short $c)"
        }
        $w = Invoke-Cli @('cliext.wait', 'idle', '--timeout', '120', '--format', 'json') 150
        Expect ($w.exit -eq 0) "the Editor after the dialog: exit $($w.exit): $(Short ($w.stdout + $w.stderr))"
        $j = Join-Path $projectPath 'Library\CLIExtensions\dialogs.jsonl'
        $answered = @(Get-Content $j | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } } | Where-Object { $_.title -eq 'Missing signature' -and $_.kind -eq 'answered' -and $_.detail -eq 'MissingSignatureAnswer' -and ([DateTime]::Parse("$($_.time)").ToUniversalTime()) -ge $t0.AddSeconds(-1) })
        Expect ($answered.Count -ge 1) 'no MissingSignatureAnswer entry in the journal for this import'
        $g = Get-PkgGuids
        Expect ($g -eq "$guids|True") "AssetDatabase after Unity's import: $g, package GUIDs $guids"
    }
    finally { Remove-PkgImport }
    Expect (-not (Test-Path (Join-Path $scratch 'PkgSrc'))) 'the imported folder was not deleted'
    Expect-PackClean 'Unity package import'
    "Missing signature $seen ($($answered[0].answer)); imported with the package's GUIDs; removed"
}

Add-Check 'pkg.errors' $pkgRow 'cliext.package import of a missing file, a folder, a .png, a text file and a gzip file named .unitypackage, and a package with a path outside Assets/ exits 13 or 2 with the fix, opens no dialog and writes nothing' {
    New-ScratchFolder
    $dialogs0 = Get-SignatureDialogs
    $text = Write-WorkFile 'not-a-package.unitypackage' 'just text'
    $gz = Join-Path $work 'gzip-only.unitypackage'
    $fs = [IO.File]::Create($gz); $z = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress); $bytes = [Text.Encoding]::ASCII.GetBytes(('x' * 700)); $z.Write($bytes, 0, $bytes.Length); $z.Close(); $fs.Close()
    # A tar.gz whose one asset is at ../Outside.txt.
    $evil = Join-Path $work 'outside.unitypackage'
    $ms = New-Object IO.MemoryStream
    $entry = {
        param($name, [byte[]]$data)
        $h = New-Object byte[] 512
        [Text.Encoding]::ASCII.GetBytes($name).CopyTo($h, 0)
        [Text.Encoding]::ASCII.GetBytes('0000644').CopyTo($h, 100)
        [Text.Encoding]::ASCII.GetBytes([Convert]::ToString($data.Length, 8).PadLeft(11, '0')).CopyTo($h, 124)
        $h[156] = [byte][char]'0'
        $ms.Write($h, 0, 512); $ms.Write($data, 0, $data.Length); $pad = (512 - $data.Length % 512) % 512; $ms.Write((New-Object byte[] $pad), 0, $pad)
    }
    & $entry '0123456789abcdef0123456789abcdef/pathname' ([Text.Encoding]::ASCII.GetBytes('../Outside.txt'))
    & $entry '0123456789abcdef0123456789abcdef/asset' ([Text.Encoding]::ASCII.GetBytes('outside'))
    $ms.Write((New-Object byte[] 1024), 0, 1024)
    $fs = [IO.File]::Create($evil); $z = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress); $raw = $ms.ToArray(); $z.Write($raw, 0, $raw.Length); $z.Close(); $fs.Close()
    $before = @(Get-ChildItem $scratch -Recurse -Force | ForEach-Object FullName) -join ';'
    $cases = @(
        @(@('import', '--path', (Join-Path $work 'none.unitypackage')), "--path: no file at '.*none\.unitypackage'\. Give the path of a \.unitypackage.*ExportPackage", 13, 'a missing file'),
        @(@('import', '--path', $work), "is a folder: give the \.unitypackage file in it", 2, 'a folder'),
        @(@('import', '--path', (Join-Path $projectPath 'Assets\ThirdParty\VFXTexturePack\Tx_Burst_radial_01.png')), "isn't a \.unitypackage \(it ends in '\.png'\): give a \.unitypackage file\. For loose files use asset import", 2, 'a .png'),
        @(@('import', '--path', $text), "isn't a \.unitypackage: it isn't a gzip archive\. Export one with AssetDatabase\.ExportPackage", 2, 'a text file named .unitypackage'),
        @(@('import', '--path', $gz), "isn't a \.unitypackage: it is a gzip file but not a tar archive", 2, 'a gzip file that is not a tar'),
        @(@('import', '--path', $evil), "has an asset at '\.\./Outside\.txt', outside Assets/: it can't be imported here\. Nothing was imported", 2, 'a package with a path outside Assets/')
    )
    $seen = @()
    foreach ($c in $cases) {
        $x = Invoke-Cli (@('cliext.package') + $c[0] + @('--format', 'json')) 60
        Expect-P3Failure $x $c[1] $c[3] $c[2]
        $seen += "$($c[3]) $($x.exit)"
    }
    Expect (-not (Test-Path (Join-Path $projectPath 'Outside.txt'))) 'the outside path was written'
    Expect ((@(Get-ChildItem $scratch -Recurse -Force | ForEach-Object FullName) -join ';') -eq $before) 'a refused import wrote into the scratch folder'
    Expect ((Get-SignatureDialogs) -eq $dialogs0) 'a Missing signature dialog opened'
    Expect-PackClean 'package errors'
    "$($seen.Count) refusals with the fix; nothing written, no dialog"
}

# The asset-packs skill's converter template and extractor, on made-up vendor assets in the scratch folder.
Add-Check 'pack.converter' $pkgRow 'the asset-packs PackMaterialConverter template, compiled in the project, converts a vendor material whose shader is missing (alpha clip and cutoff saved on, an opaque base texture, a metallic map, HDR emission 4.6) to an opaque Lit material with only base, normal and emission maps, no stale keywords and emission 4.6; one on a texture with alpha keeps its cut-out; ConvertAll takes the folder and refuses a missing one' {
    $dir = "$scratchRel/PackConv"
    $conv = Join-Path $scratch 'PackConv\Editor'
    New-Item -ItemType Directory -Force $conv | Out-Null
    Copy-Item (Join-Path $packageRoot 'Skills~\unity-cliext-asset-packs\templates\Editor\PackMaterialConverter.cs') $conv
    try {
        [void](Compile-AndWait)
        $errs = @((Get-Diagnostics) | Where-Object { "$($_.file)" -match 'PackMaterialConverter' })
        Expect ($errs.Count -eq 0) "the template doesn't compile: $(Short (($errs | ForEach-Object { "$($_.code) $($_.message)" }) -join '; '))"
        $setup = @'
string dir = "{{dir}}/Mats";
if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) UnityEditor.AssetDatabase.CreateFolder("{{dir}}", "Mats");
string Png(string name, bool alpha) {
    var t = new UnityEngine.Texture2D(8, 8, alpha ? UnityEngine.TextureFormat.RGBA32 : UnityEngine.TextureFormat.RGB24, false);
    var px = new UnityEngine.Color[64];
    for (int i = 0; i < 64; i++) px[i] = new UnityEngine.Color(0.6f, 0.4f, 0.2f, alpha && i % 2 == 0 ? 0f : 1f);
    t.SetPixels(px); t.Apply();
    string p = dir + "/" + name + ".png";
    System.IO.File.WriteAllBytes(p, UnityEngine.ImageConversion.EncodeToPNG(t));
    UnityEditor.AssetDatabase.ImportAsset(p);
    return UnityEditor.AssetDatabase.AssetPathToGUID(p);
}
string opaque = Png("Atlas", false), cut = Png("Leaves", true), emit = Png("Glow", false), metal = Png("Metal", false);
string Tex(string slot, string guid) => "    - " + slot + ":\n        m_Texture: {fileID: 2800000, guid: " + guid + ", type: 3}\n        m_Scale: {x: 1, y: 1}\n        m_Offset: {x: 0, y: 0}\n";
string Mat(string name, string baseGuid) =>
    "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n  serializedVersion: 8\n  m_ObjectHideFlags: 0\n" +
    "  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_Name: " + name + "\n" +
    "  m_Shader: {fileID: -6465566751694194690, guid: 0c11e570000000000000000000000c1e, type: 3}\n  m_Parent: {fileID: 0}\n" +
    "  m_ModifiedSerializedProperties: 0\n  m_ValidKeywords:\n  - _ALPHATEST_ON\n  - _METALLICGLOSSMAP\n  m_InvalidKeywords: []\n" +
    "  m_LightmapFlags: 4\n  m_EnableInstancingVariants: 0\n  m_DoubleSidedGI: 0\n  m_CustomRenderQueue: 2450\n  stringTagMap: {}\n" +
    "  disabledShaderPasses: []\n  m_LockedProperties: \n  m_SavedProperties:\n    serializedVersion: 3\n    m_TexEnvs:\n" +
    Tex("_Albedo_Map", baseGuid) + Tex("_Emission_Map", emit) + Tex("_MetallicGlossMap", metal) + Tex("_OcclusionMap", metal) +
    "    m_Ints: []\n    m_Floats:\n    - _AlphaClip: 1\n    - _AlphaToMask: 1\n    - _Alpha_Clip_Threshold: 0.4\n    - _BUILTIN_AlphaClip: 1\n" +
    "    - _Cutoff: 0.5\n    - _Enable_Emission: 1\n    - _Metallic: 0\n    m_Colors:\n    - _EmissionColor: {r: 1, g: 1, b: 1, a: 1}\n" +
    "    - _Emission_Color: {r: 4.6, g: 4.6, b: 4.6, a: 1}\n  m_BuildTextureStacks: []\n  m_AllowLocking: 1\n";
System.IO.File.WriteAllText(dir + "/Vendor_Opaque.mat", Mat("Vendor_Opaque", opaque));
System.IO.File.WriteAllText(dir + "/Vendor_Cutout.mat", Mat("Vendor_Cutout", cut));
UnityEditor.AssetDatabase.ImportAsset(dir + "/Vendor_Opaque.mat");
UnityEditor.AssetDatabase.ImportAsset(dir + "/Vendor_Cutout.mat");
var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(dir + "/Vendor_Opaque.mat");
return m == null ? "no material" : "ok";
'@
        $s = Invoke-Eval 'pack-conv-setup.cs' $setup.Replace('{{dir}}', $dir)
        Expect ($s -eq 'ok') "vendor assets: $s"
        $read = @'
var bad = AssetPackTools.Editor.PackMaterialConverter.ConvertAll("{{dir}}/NoSuchFolder");
var summary = AssetPackTools.Editor.PackMaterialConverter.ConvertAll("{{dir}}/Mats");
string Read(string name) {
    var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>("{{dir}}/Mats/" + name + ".mat");
    var maps = new System.Collections.Generic.List<string>();
    foreach (var p in m.GetTexturePropertyNames()) if (m.HasProperty(p) && m.GetTexture(p) != null) maps.Add(p);
    var e = m.GetColor("_EmissionColor");
    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|kw={1}|clip={2}|queue={3}|tag={4}|cutoff={5:0.00}|maps={6}|emission={7:0.00}",
        m.shader.name, string.Join(",", m.shaderKeywords.OrderBy(k => k)), m.GetFloat("_AlphaClip"), m.renderQueue, m.GetTag("RenderType", false),
        m.GetFloat("_Cutoff"), string.Join(",", maps.OrderBy(k => k)), e.r);
}
return bad + "\n" + summary + "\n" + Read("Vendor_Opaque") + "\n" + Read("Vendor_Cutout");
'@
        $out = (Invoke-Eval 'pack-conv-run.cs' $read.Replace('{{dir}}', $dir)) -replace '\\n', "`n" -replace '\\"', '"'
        $lines = @($out -split "`n")
        Expect ($lines.Count -ge 4) "convert: $(Short $out 400)"
        Expect ($lines[0] -match "No folder '.*NoSuchFolder'") "a missing folder: $($lines[0])"
        Expect ($lines[1] -match 'converted under .*/Mats: 2 \(1 alpha-clipped, 2 emissive') "summary: $($lines[1])"
        $want = 'Universal Render Pipeline/Lit|kw=_EMISSION|clip=0|queue=2000|tag=Opaque|cutoff=0.40|maps=_BaseMap,_EmissionMap,_MainTex|emission=4.60'
        Expect ($lines[2] -eq $want) "opaque base texture: $($lines[2]), expected $want"
        $wantCut = 'Universal Render Pipeline/Lit|kw=_ALPHATEST_ON,_EMISSION|clip=1|queue=2450|tag=TransparentCutout|cutoff=0.40|maps=_BaseMap,_EmissionMap,_MainTex|emission=4.60'
        Expect ($lines[3] -eq $wantCut) "base texture with alpha: $($lines[3]), expected $wantCut"
        "opaque: $($lines[2]); cut-out: $($lines[3])"
    }
    finally {
        [void](Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'pack-conv-clean.cs' "UnityEditor.AssetDatabase.DeleteAsset(`"$dir`"); return `"ok`";"), '--format', 'json'))
        [void](Compile-AndWait)
    }
}

Add-Check 'pack.extract-remove' $pkgRow 'the asset-packs extractor: --dry-run on two matching prefabs lists each with its own dependencies; extracting both writes them with the package''s GUIDs; --remove of one (dry run, then for real) deletes it and the material and texture only it uses, keeps the material and texture the other prefab shares, and removes nothing else' {
    $src = "$scratchRel/PackSrc"
    $pkg = Join-Path $work 'trial.unitypackage'
    $script = Join-Path $packageRoot 'Skills~\unity-cliext-asset-packs\scripts\extract_unitypackage.py'
    $make = @'
string dir = "{{dir}}";
if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) UnityEditor.AssetDatabase.CreateFolder("{{scratch}}", "PackSrc");
UnityEngine.Material Mat(string name) {
    var t = new UnityEngine.Texture2D(4, 4); string p = dir + "/" + name + ".png";
    System.IO.File.WriteAllBytes(p, UnityEngine.ImageConversion.EncodeToPNG(t)); UnityEditor.AssetDatabase.ImportAsset(p);
    var m = new UnityEngine.Material(UnityEngine.Shader.Find("Universal Render Pipeline/Lit"));
    m.SetTexture("_BaseMap", UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(p));
    UnityEditor.AssetDatabase.CreateAsset(m, dir + "/" + name + ".mat"); return m;
}
var shared = Mat("Shared"); var onlyB = Mat("OnlyB");
var a = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube); a.GetComponent<UnityEngine.MeshRenderer>().sharedMaterial = shared;
UnityEditor.PrefabUtility.SaveAsPrefabAsset(a, dir + "/Prop_01.prefab"); UnityEngine.Object.DestroyImmediate(a);
var b = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube); b.GetComponent<UnityEngine.MeshRenderer>().sharedMaterials = new[] { onlyB, shared };
UnityEditor.PrefabUtility.SaveAsPrefabAsset(b, dir + "/Prop_02.prefab"); UnityEngine.Object.DestroyImmediate(b);
UnityEditor.AssetDatabase.SaveAssets();
UnityEditor.AssetDatabase.ExportPackage(dir, @"{{out}}", UnityEditor.ExportPackageOptions.Recurse);
var guids = string.Join(",", new[] { "Prop_01.prefab", "Prop_02.prefab", "Shared.mat", "Shared.png", "OnlyB.mat", "OnlyB.png" }.Select(f => UnityEditor.AssetDatabase.AssetPathToGUID(dir + "/" + f)));
UnityEditor.AssetDatabase.DeleteAsset(dir);
return guids;
'@
    $present = "string d = `"$src`"; return string.Join(`",`", new[] { `"Prop_01.prefab`", `"Prop_02.prefab`", `"Shared.mat`", `"Shared.png`", `"OnlyB.mat`", `"OnlyB.png`" }.Select(f => UnityEditor.AssetDatabase.AssetPathToGUID(d + `"/`" + f, UnityEditor.AssetPathToGUIDOptions.OnlyExistingAssets)));"
    function Py([string[]]$a) {
        $ErrorActionPreference = 'Continue'
        $o = & python -I $script $projectPath $pkg @a 2>&1 | Out-String
        $ErrorActionPreference = 'Stop'
        [pscustomobject]@{ exit = $LASTEXITCODE; out = $o }
    }
    try {
        New-ScratchFolder
        $guids = Invoke-Eval 'pack-src.cs' $make.Replace('{{dir}}', $src).Replace('{{scratch}}', $scratchRel).Replace('{{out}}', $pkg)
        Expect ((Test-Path $pkg) -and $guids -match '^([0-9a-f]{32},){5}[0-9a-f]{32}$') "test package: $guids"
        $g = $guids -split ','
        $d = Py @('Prop_0[12]\.prefab', '--dry-run')
        Expect ($d.exit -eq 0 -and $d.out -match '2 matched, 6 with dependencies' -and $d.out -match 'Prop_01\.prefab: 3 assets' -and $d.out -match 'Prop_02\.prefab: 5 assets') "dry run: $(Short $d.out 400)"
        $x = Py @('Prop_0[12]\.prefab')
        Expect ($x.exit -eq 0 -and $x.out -match '6 written') "extract: $(Short $x.out 300)"
        [void](Invoke-Cli @('cliext.wait', 'idle', '--refresh', 'true', '--format', 'json'))
        Expect ((Invoke-Eval 'pack-present.cs' $present) -eq $guids) "after extracting, the GUIDs differ from the package's: $(Invoke-Eval 'pack-present.cs' $present)"
        $rd = Py @('Prop_02\.prefab', '--remove', '--dry-run')
        Expect ($rd.exit -eq 0 -and $rd.out -match 'would remove 3' -and $rd.out -match '- .*/OnlyB\.png' -and $rd.out -match '= .*/Shared\.mat\s+\(used by .*Prop_01\.prefab\)') "remove dry run: $(Short $rd.out 500)"
        Expect ((Invoke-Eval 'pack-present.cs' $present) -eq $guids) 'the remove dry run deleted something'
        $r = Py @('Prop_02\.prefab', '--remove')
        Expect ($r.exit -eq 0 -and $r.out -match 'removed 3 assets') "remove: $(Short $r.out 400)"
        [void](Invoke-Cli @('cliext.wait', 'idle', '--refresh', 'true', '--format', 'json'))
        $after = Invoke-Eval 'pack-present.cs' $present
        $wantAfter = "$($g[0]),,$($g[2]),$($g[3]),,"
        Expect ($after -eq $wantAfter) "after --remove: $after, expected Prop_01 and Shared kept, Prop_02 and OnlyB gone"
        "dry run listed 2 matches with their own dependencies; extracted 6 with the package's GUIDs; --remove took out Prop_02, OnlyB.mat and OnlyB.png and kept Shared.mat and Shared.png (used by Prop_01)"
    }
    finally {
        [void](Invoke-Cli @('script', 'eval', '--file', (Write-WorkFile 'pack-src-clean.cs' "UnityEditor.AssetDatabase.DeleteAsset(`"$src`"); return `"ok`";"), '--format', 'json'))
        Remove-Item $pkg -Force -ErrorAction SilentlyContinue
    }
}

# --- cliext.asset, cliext.diff ----------------------------------------------------------------------------------------

# Dependencies are checked on the committed TestLevel: its Player wears PlayerSkin.mat, which uses skaterMaleA.png. Each
# answer is compared with AssetDatabase.GetDependencies (and, for used_by, a scan of every asset under Assets/). Diffs
# run on TestLevel opened from disk and changed without saving (reopened from disk after), and on a scratch prefab.
# Snapshots are taken under cliext-test-* names and deleted in a finally; the snapshot folder is compared before and after.
$depRow = 'cliext.asset, cliext.diff'
$depScene = 'Assets/Scenes/TestLevel.unity'
$depMat = 'Assets/TestLevel/Characters/PlayerSkin.mat'
$depTex = 'Assets/TestLevel/Characters/skaterMaleA.png'
$snapDir = Join-Path $projectPath 'Library\CLIExtensions\snapshots'

function Get-DepRecords($r) { @(@($r.json) | Where-Object { $_ -ne $null } | ForEach-Object { $_.path }) | Sort-Object }
function Get-Deps([string]$path, [bool]$recursive) {
    # AssetDatabase.GetDependencies without the asset itself, sorted, ';'-joined.
    $code = "var d = UnityEditor.AssetDatabase.GetDependencies(`"$path`", $(if ($recursive) { 'true' } else { 'false' })).Where(p => p != `"$path`").OrderBy(p => p, System.StringComparer.Ordinal); return string.Join(`";`", d);"
    (@((Invoke-Eval 'dep-deps.cs' $code) -split ';' | Where-Object { $_ } | Sort-Object) -join ';')
}
function Get-Users([string]$path, [bool]$recursive) {
    # Every asset under Assets/ whose GetDependencies (direct, or recursive) holds $path, sorted, ';'-joined.
    $code = "var all = UnityEditor.AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(`"Assets/`") && p != `"$path`" && !UnityEditor.AssetDatabase.IsValidFolder(p)); return string.Join(`";`", all.Where(p => UnityEditor.AssetDatabase.GetDependencies(p, $(if ($recursive) { 'true' } else { 'false' })).Contains(`"$path`")).OrderBy(p => p, System.StringComparer.Ordinal));"
    (@((Invoke-Eval 'dep-users.cs' $code) -split ';' | Where-Object { $_ } | Sort-Object) -join ';')
}
function Get-SnapFiles { if (Test-Path $snapDir) { (@(Get-ChildItem $snapDir -File | Sort-Object Name | ForEach-Object { "$($_.Name)=$((Get-FileHash $_.FullName).Hash)" }) -join ';') } else { '' } }
function Open-DepScene {
    # TestLevel from disk, as the active scene; returns the scene that was open.
    $scenes = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($scenes.json) | Where-Object { $_.isDirty })) "an open scene has unsaved changes: $(Short $scenes.stdout)"
    $code = "string before = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path; UnityEditor.SceneManagement.EditorSceneManager.OpenScene(`"$depScene`", UnityEditor.SceneManagement.OpenSceneMode.Single); return before;"
    Invoke-Eval 'dep-open.cs' $code
}
function Get-DiffLines($r) {
    @(@($r.json) | Where-Object { $_ -ne $null } | ForEach-Object { ("$($_.change) $($_.object) $($_.component) $($_.property) $($_.before)->$($_.after)" -replace '\s+', ' ').Trim() }) | Sort-Object
}

Add-Check 'dep.uses' $depRow 'cliext.asset uses lists exactly what AssetDatabase.GetDependencies gives for PlayerSkin.mat (skaterMaleA.png and the Lit shader; URP''s Editor-only AssetVersion.cs only with --include-editor-scripts), directly and --recursive, by path and by GUID, and for TestLevel' {
    $u = Invoke-Cli @('cliext.asset', 'uses', '--path', $depMat, '--format', 'json')
    Expect ($u.exit -eq 0) "uses: exit $($u.exit): $(Short ($u.stdout + $u.stderr))"
    $got = (Get-DepRecords $u) -join ';'
    $all = Get-Deps $depMat $false
    # Editor-only scripts (URP's AssetVersion.cs on every material) are left out unless --include-editor-scripts.
    $want = (@($all -split ';' | Where-Object { $_ -and $_ -notmatch '/Editor/.*\.cs$' }) -join ';')
    Expect ($got -eq $want -and $got -like "*$depTex*") "uses $depMat`: $got; GetDependencies without Editor scripts: $want"
    $ue = Invoke-Cli @('cliext.asset', 'uses', '--path', $depMat, '--include-editor-scripts', 'true', '--format', 'json')
    Expect ($ue.exit -eq 0 -and ((Get-DepRecords $ue) -join ';') -eq $all) "uses --include-editor-scripts: $((Get-DepRecords $ue) -join ';'); GetDependencies: $all"
    Expect ((@($u.json) | Where-Object { $_.path -eq $depTex }).type -eq 'Texture2D') "the texture record: $(Short $u.stdout)"
    $guid = Invoke-Eval 'dep-guid.cs' "return UnityEditor.AssetDatabase.AssetPathToGUID(`"$depMat`");"
    $g = Invoke-Cli @('cliext.asset', 'uses', '--path', $guid, '--recursive', 'true', '--include-editor-scripts', 'true', '--format', 'json')
    $gotR = (Get-DepRecords $g) -join ';'; $wantR = Get-Deps $depMat $true
    Expect ($g.exit -eq 0 -and $gotR -eq $wantR) "uses --recursive by GUID: exit $($g.exit): $gotR; GetDependencies: $wantR"
    $s = Invoke-Cli @('cliext.asset', 'uses', '--path', $depScene, '--format', 'json')
    $gotS = Get-DepRecords $s; $wantS = @((Get-Deps $depScene $false) -split ';' | Where-Object { $_ })
    Expect ($s.exit -eq 0 -and ($gotS -join ';') -eq ($wantS -join ';') -and $gotS -contains $depMat) "uses $depScene`: $($gotS -join ';'); GetDependencies: $($wantS -join ';')"
    "$depMat uses $($got -replace '[^;]+/', '') (recursive $(@($g.json).Count)); TestLevel uses $($gotS.Count)"
}

Add-Check 'dep.used-by' $depRow 'cliext.asset used_by skaterMaleA.png is PlayerSkin.mat, and --recursive adds TestLevel: the same as scanning every asset''s GetDependencies; --include-packages on a URP shader lists package users, and the next call without it lists none' {
    $d = Invoke-Cli @('cliext.asset', 'used_by', '--path', $depTex, '--format', 'json')
    $got = (Get-DepRecords $d) -join ';'; $want = Get-Users $depTex $false
    Expect ($d.exit -eq 0 -and $got -eq $want -and $got -like "*$depMat*") "used_by: exit $($d.exit): $got; scan: $want"
    $r = Invoke-Cli @('cliext.asset', 'used_by', '--path', $depTex, '--recursive', 'true', '--format', 'json')
    $gotR = (Get-DepRecords $r) -join ';'; $wantR = Get-Users $depTex $true
    Expect ($r.exit -eq 0 -and $gotR -eq $wantR -and $gotR -like "*$depScene*") "used_by --recursive: exit $($r.exit): $gotR; scan: $wantR"
    $lit = 'Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader'
    $p = Invoke-Cli @('cliext.asset', 'used_by', '--path', $lit, '--include-packages', 'true', '--format', 'json') 120
    $pkgUsers = @(Get-DepRecords $p | Where-Object { $_ -like 'Packages/*' })
    Expect ($p.exit -eq 0 -and $pkgUsers.Count -gt 0) "used_by --include-packages: exit $($p.exit), $($pkgUsers.Count) package users"
    $a = Invoke-Cli @('cliext.asset', 'used_by', '--path', $lit, '--format', 'json')
    $leak = @(Get-DepRecords $a | Where-Object { $_ -notlike 'Assets/*' })
    Expect ($a.exit -eq 0 -and $leak.Count -eq 0) "used_by without --include-packages after a call with it listed $($leak.Count) package assets: $(Short ($leak -join ';'))"
    Expect ((Get-DepRecords $a) -contains $depMat) "used_by Lit.shader leaves out PlayerSkin.mat: $(Short $a.stdout)"
    "$depTex used by $($got -replace '[^;]+/', ''); recursive: $($gotR -replace '[^;]+/', ''); Lit.shader: $($pkgUsers.Count) package users only with --include-packages"
}

Add-Check 'dep.asset-errors' $depRow 'cliext.asset uses and used_by with a missing asset or GUID exit 13 naming asset find, and a folder or a path outside the project exit 2' {
    $seen = @()
    foreach ($cmd in 'uses', 'used_by') {
        foreach ($c in @(
                @('Assets/TestLevel/Characters/NoSuch.mat', "--path: no asset at 'Assets/TestLevel/Characters/NoSuch\.mat'\. Check the path \(asset find", 13, 'a missing asset'),
                @('0123456789abcdef0123456789abcdef', 'no asset has the GUID 0123456789abcdef0123456789abcdef.*asset find', 13, 'an unknown GUID'),
                @('Assets/TestLevel/Characters', "is a folder: give an asset in it \(asset find --search-in", 2, 'a folder'),
                @('C:/Windows/notepad.exe', "isn't under Assets/ or Packages/", 2, 'a path outside the project'))) {
            $x = Invoke-Cli @('cliext.asset', $cmd, '--path', $c[0], '--format', 'json')
            Expect-P3Failure $x $c[1] "$cmd $($c[3])" $c[2]
            Expect ("$($x.stdout)".Trim() -eq '') "$cmd $($c[3]): records on stdout: $(Short $x.stdout)"
            $seen += "$cmd $($c[3]) $($x.exit)"
        }
    }
    "$($seen.Count) refusals with the fix"
}

Add-Check 'dep.diff-scene' $depRow 'cliext.diff snapshot of TestLevel opened from disk, then unsaved changes (light moved, Player renamed, a collider added, CamB deleted, an object added): compare lists exactly those 5, list shows the snapshot, --update then compare is empty, reopened from disk compare lists them reversed; deleted after' {
    $snap0 = Get-SnapFiles
    $was = Open-DepScene
    $name = 'cliext-test-scene'
    try {
        $s = Invoke-Cli @('cliext.diff', 'snapshot', '--path', $depScene, '--name', $name, '--format', 'json')
        Expect ($s.exit -eq 0 -and $s.json.name -eq $name -and $s.json.kind -eq 'scene' -and $s.json.objects -gt 10) "snapshot: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $c0 = Invoke-Cli @('cliext.diff', 'compare', '--name', $name, '--format', 'json')
        Expect ($c0.exit -eq 0 -and "$($c0.stdout)".Trim() -eq '') "compare with nothing changed: exit $($c0.exit): $(Short $c0.stdout 300)"
        $code = @'
var light = UnityEngine.GameObject.Find("Directional Light");
light.transform.localPosition += new UnityEngine.Vector3(1.5f, 0, 0);
UnityEngine.GameObject.Find("Player").name = "CliPlayer";
UnityEngine.GameObject.Find("Floor").AddComponent<UnityEngine.BoxCollider>();
UnityEngine.Object.DestroyImmediate(UnityEngine.GameObject.Find("CamB"));
new UnityEngine.GameObject("CliDiffAdded");
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(light.scene);
return light.transform.localPosition.x.ToString(System.Globalization.CultureInfo.InvariantCulture);
'@
        $lx = Invoke-Eval 'dep-change.cs' $code
        $l = Invoke-Cli @('cliext.diff', 'list', '--format', 'json')
        Expect ($l.exit -eq 0 -and @(@($l.json) | Where-Object { $_.name -eq $name -and $_.assetPath -eq $depScene }).Count -eq 1) "list: $(Short $l.stdout)"
        $c = Invoke-Cli @('cliext.diff', 'compare', '--name', $name, '--format', 'json')
        $lines = Get-DiffLines $c
        $x0 = [double]$lx - 1.5
        $fmt = { param($v) ([double]$v).ToString('0.#####', [Globalization.CultureInfo]::InvariantCulture) }
        $want = @(
            'component-added Floor BoxCollider ->',
            "object-removed CamB ->",
            'object-added CliDiffAdded ->',
            'object-renamed CliPlayer Player->CliPlayer',
            "property-changed Directional Light Transform m_LocalPosition.x $(& $fmt $x0)->$(& $fmt $lx)") | Sort-Object
        Expect ($c.exit -eq 0 -and ($lines -join "`n") -eq ($want -join "`n")) "compare: exit $($c.exit): got $($lines.Count): $(Short ($lines -join ' | ') 600); want: $($want -join ' | ')"
        $u = Invoke-Cli @('cliext.diff', 'compare', '--name', $name, '--update', 'true', '--format', 'json')
        Expect ($u.exit -eq 0 -and @(Get-DiffLines $u).Count -eq 5) "compare --update: exit $($u.exit): $(Short $u.stdout)"
        $e = Invoke-Cli @('cliext.diff', 'compare', '--name', $name, '--format', 'json')
        Expect ($e.exit -eq 0 -and "$($e.stdout)".Trim() -eq '') "compare after --update: $(Short $e.stdout 300)"
        $back = Close-P3Scene $depScene
        Expect ($back -match '\|False\|') "reopen from disk: $back"
        $r = Invoke-Cli @('cliext.diff', 'compare', '--name', $name, '--format', 'json')
        $rl = Get-DiffLines $r
        Expect ($r.exit -eq 0 -and $rl.Count -eq 5 -and ($rl -join '|') -match 'component-removed Floor BoxCollider' -and ($rl -join '|') -match 'object-added CamB' -and ($rl -join '|') -match 'object-renamed Player CliPlayer->Player') "compare after reopening: $(Short ($rl -join ' | ') 500)"
    }
    finally {
        $dl = Invoke-Cli @('cliext.diff', 'delete', '--name', $name, '--format', 'json')
        [void](Close-P3Scene $was)
    }
    Expect ($dl.exit -eq 0 -and $dl.json.deleted -eq 1) "delete: exit $($dl.exit): $(Short ($dl.stdout + $dl.stderr))"
    Expect ((Get-SnapFiles) -eq $snap0) 'the snapshot folder is not as it was'
    $open = Invoke-Cli @('scene', 'list_open', '--format', 'json')
    Expect (-not (@($open.json) | Where-Object { $_.isDirty })) "a scene was left dirty: $(Short $open.stdout)"
    "5 changes, exactly; --update then none; reversed after reopening; snapshot deleted"
}

Add-Check 'dep.diff-prefab' $depRow 'cliext.diff snapshot of a scratch prefab, then the prefab saved with a child added, a property changed and a component removed: compare lists exactly those 3; delete removes the snapshot' {
    New-ScratchFolder
    $prefab = "$scratchRel/CliDiff.prefab"
    $code = @'
var go = new UnityEngine.GameObject("CliDiff");
go.AddComponent<UnityEngine.BoxCollider>();
go.AddComponent<UnityEngine.SphereCollider>().radius = 0.5f;
new UnityEngine.GameObject("Child").transform.SetParent(go.transform, false);
UnityEditor.PrefabUtility.SaveAsPrefabAsset(go, "{{p}}", out bool ok);
UnityEngine.Object.DestroyImmediate(go);
return ok ? "ok" : "failed";
'@
    Expect ((Invoke-Eval 'dep-prefab.cs' $code.Replace('{{p}}', $prefab)) -eq 'ok') 'scratch prefab'
    $name = 'cliext-test-prefab'
    $snap0 = Get-SnapFiles
    try {
        $s = Invoke-Cli @('cliext.diff', 'snapshot', '--path', $prefab, '--name', $name, '--format', 'json')
        Expect ($s.exit -eq 0 -and $s.json.kind -eq 'prefab' -and $s.json.objects -eq 2 -and $s.json.components -eq 4) "snapshot: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
        $edit = @'
var root = UnityEditor.PrefabUtility.LoadPrefabContents("{{p}}");
new UnityEngine.GameObject("Added").transform.SetParent(root.transform, false);
root.GetComponent<UnityEngine.SphereCollider>().radius = 2f;
UnityEngine.Object.DestroyImmediate(root.GetComponent<UnityEngine.BoxCollider>());
UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, "{{p}}");
UnityEditor.PrefabUtility.UnloadPrefabContents(root);
return "ok";
'@
        [void](Invoke-Eval 'dep-prefab-edit.cs' $edit.Replace('{{p}}', $prefab))
        $c = Invoke-Cli @('cliext.diff', 'compare', '--name', $name, '--format', 'json')
        $lines = Get-DiffLines $c
        $want = @('component-removed CliDiff BoxCollider ->', 'object-added CliDiff/Added ->', 'property-changed CliDiff SphereCollider m_Radius 0.5->2') | Sort-Object
        Expect ($c.exit -eq 0 -and ($lines -join "`n") -eq ($want -join "`n")) "compare: exit $($c.exit): $(Short ($lines -join ' | ') 500)"
    }
    finally { $dl = Invoke-Cli @('cliext.diff', 'delete', '--name', $name, '--format', 'json') }
    Expect ($dl.exit -eq 0) "delete: exit $($dl.exit): $(Short $dl.stderr)"
    $again = Invoke-Cli @('cliext.diff', 'delete', '--name', $name, '--format', 'json')
    Expect-P3Failure $again "No snapshot named 'cliext-test-prefab'.*cliext.diff (list|snapshot)" 'delete twice' 13
    Expect ((Get-SnapFiles) -eq $snap0) 'the snapshot folder is not as it was'
    "3 changes, exactly; deleted; delete again exit 13"
}

Add-Check 'dep.diff-errors' $depRow 'cliext.diff snapshot of a missing scene or prefab exits 13 naming asset find; a material, a folder or a path outside the project exits 2; compare of an unknown snapshot exits 13 with Did you mean, and of one whose prefab is gone 13; delete with neither or both of --name and --all exits 2; nothing is written' {
    New-ScratchFolder
    $prefab = "$scratchRel/CliGone.prefab"
    $code = "var go = new UnityEngine.GameObject(`"CliGone`"); UnityEditor.PrefabUtility.SaveAsPrefabAsset(go, `"$prefab`"); UnityEngine.Object.DestroyImmediate(go); return `"ok`";"
    [void](Invoke-Eval 'dep-gone.cs' $code)
    $snap0 = Get-SnapFiles
    $name = 'cliext-test-gone'
    $s = Invoke-Cli @('cliext.diff', 'snapshot', '--path', $prefab, '--name', $name, '--format', 'json')
    Expect ($s.exit -eq 0) "snapshot: exit $($s.exit): $(Short $s.stderr)"
    try {
        [void](Invoke-Eval 'dep-gone-rm.cs' "UnityEditor.AssetDatabase.DeleteAsset(`"$prefab`"); return `"ok`";")
        $snap1 = Get-SnapFiles
        $seen = @()
        foreach ($c in @(
                @(@('snapshot', '--path', 'Assets/Scenes/NoSuch.unity'), "--path: no scene at 'Assets/Scenes/NoSuch\.unity'\. Check the path \(asset find --type Scene", 13, 'snapshot of a missing scene'),
                @(@('snapshot', '--path', 'Assets/NoSuch.prefab'), "--path: no prefab at 'Assets/NoSuch\.prefab'\. Check the path \(asset find --type Prefab", 13, 'snapshot of a missing prefab'),
                @(@('snapshot', '--path', $depMat), "is a Material, not a scene \(\.unity\) or prefab \(\.prefab\)", 2, 'snapshot of a material'),
                @(@('snapshot', '--path', 'Assets/Scenes'), "is a folder, not a scene", 2, 'snapshot of a folder'),
                @(@('snapshot', '--path', 'C:/Temp/Level.unity'), "isn't under Assets/ or Packages/", 2, 'snapshot outside the project'),
                @(@('compare', '--name', 'cliext-test-gnoe'), "No snapshot named 'cliext-test-gnoe'\. Did you mean: cliext-test-gone\?", 13, 'compare of a misspelled snapshot'),
                @(@('compare', '--name', $name), "is of '.*CliGone\.prefab', which is gone.*cliext\.diff delete", 13, 'compare after the prefab was deleted'),
                @(@('compare', '--name', $name, '--update', 'true'), 'which is gone', 13, 'compare --update after the prefab was deleted'),
                @(@('delete'), 'Pass --name \(cliext\.diff list lists the snapshots\), or --all true', 2, 'delete with neither'),
                @(@('delete', '--name', $name, '--all', 'true'), 'not both', 2, 'delete with both'),
                @(@('delete', '--name', 'cliext-test-nope'), "No snapshot named 'cliext-test-nope'", 13, 'delete of an unknown snapshot'))) {
            $x = Invoke-Cli (@('cliext.diff') + $c[0] + @('--format', 'json'))
            Expect-P3Failure $x $c[1] $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        Expect ((Get-SnapFiles) -eq $snap1) 'a failed cliext.diff call wrote or deleted a snapshot'
    }
    finally { $dl = Invoke-Cli @('cliext.diff', 'delete', '--name', $name, '--format', 'json') }
    Expect ($dl.exit -eq 0 -and (Get-SnapFiles) -eq $snap0) "delete: exit $($dl.exit); the snapshot folder is not as it was"
    "$($seen.Count) refusals with the fix; nothing written"
}

# --- cliext.addressables ----------------------------------------------------------------------------------------------

# The committed project has no Addressables data. Each check that needs it runs inside Use-Addr: it saves every file of
# ProjectSettings, runs cliext.addressables init, and in a finally unregisters the settings
# (EditorBuildSettings.RemoveConfigObject), deletes Assets/AddressableAssetsData with AssetDatabase.DeleteAsset, removes
# ServerData if the check made it, writes back the ProjectSettings bytes and checks git status of those paths. Entries
# are copies of committed textures in the scratch folder (Addr/A.png, B.png, C.png). Get-AddrSig reads the settings as
# the Editor holds them (labels, groups with their packing, entries with address and labels).
$addrRow = 'cliext.addressables'
$addrData = 'Assets/AddressableAssetsData'
$addrRel = "$scratchRel/Addr"
$addrPaths = @('Assets/AddressableAssetsData', 'Assets/AddressableAssetsData.meta', 'ProjectSettings', 'ServerData', 'Assets/StreamingAssets')

function Get-AddrGit {
    if (-not (Test-Path (Join-Path $projectPath '.git'))) { return '(no git)' }
    $ErrorActionPreference = 'Continue'
    "$(& git -C $projectPath status --porcelain -- $addrPaths 2>$null)"
}
function Save-ProjectSettingsAll {
    $h = @{}
    foreach ($f in Get-ChildItem (Join-Path $projectPath 'ProjectSettings') -File) { $h[$f.Name] = [IO.File]::ReadAllBytes($f.FullName) }
    $h
}
function Restore-ProjectSettingsAll($saved) {
    # Writes back changed files, deletes new ones; returns what it did.
    $put = @()
    $dir = Join-Path $projectPath 'ProjectSettings'
    foreach ($f in Get-ChildItem $dir -File) {
        if (-not $saved.ContainsKey($f.Name)) { Remove-Item $f.FullName -Force; $put += "-$($f.Name)"; continue }
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName)) -ne [Convert]::ToBase64String($saved[$f.Name])) { [IO.File]::WriteAllBytes($f.FullName, $saved[$f.Name]); $put += $f.Name }
    }
    foreach ($k in $saved.Keys) { $p = Join-Path $dir $k; if (-not (Test-Path $p)) { [IO.File]::WriteAllBytes($p, $saved[$k]); $put += "+$k" } }
    , $put
}
function Get-AddrSig {
    $code = @'
var s = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.GetSettings(false);
if (s == null) return "none";
var sb = new System.Text.StringBuilder();
sb.Append("labels=").Append(string.Join(",", s.GetLabels().OrderBy(x => x, System.StringComparer.Ordinal))).Append(" |");
foreach (var g in s.groups.Where(g => g != null).OrderBy(g => g.Name, System.StringComparer.Ordinal))
{
    var b = g.GetSchema<UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema>();
    sb.Append(' ').Append(g.Name).Append(g.Default ? "*" : "").Append('[').Append(b == null ? "-" : b.BundleMode + "," + b.Compression + "," + g.IncludeInBuild).Append("]{");
    foreach (var e in g.entries.OrderBy(e => e.AssetPath, System.StringComparer.Ordinal))
        sb.Append(System.IO.Path.GetFileName(e.AssetPath)).Append('=').Append(e.address).Append(':').Append(string.Join(",", e.labels.OrderBy(x => x, System.StringComparer.Ordinal))).Append(';');
    sb.Append('}');
}
return sb.ToString().Replace('"', '\'');
'@
    Invoke-Eval 'addr-sig.cs' $code
}
function Get-AddrFiles {
    $d = Join-Path $projectPath 'Assets\AddressableAssetsData'
    if (-not (Test-Path $d)) { return '' }
    (@(Get-ChildItem $d -Recurse -File | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($d.Length))=$((Get-FileHash $_.FullName).Hash)" }) -join ';')
}
function Remove-Addr {
    $code = @'
UnityEditor.EditorBuildSettings.RemoveConfigObject("com.unity.addressableassets");
bool had = UnityEditor.AssetDatabase.IsValidFolder("Assets/AddressableAssetsData");
if (had) UnityEditor.AssetDatabase.DeleteAsset("Assets/AddressableAssetsData");
UnityEditor.AssetDatabase.SaveAssets();
return had + "|" + (UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.GetSettings(false) == null);
'@
    Invoke-Eval 'addr-remove.cs' $code
}
function New-AddrTextures {
    New-ScratchFolder
    $code = @'
string root = "Assets/_CliextTests/CliTest";
UnityEditor.AssetDatabase.DeleteAsset(root + "/Addr");
UnityEditor.AssetDatabase.CreateFolder(root, "Addr");
string src = "{{pack}}/";
bool ok = UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_directional_01.png", root + "/Addr/A.png")
    & UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_directional_02.png", root + "/Addr/B.png")
    & UnityEditor.AssetDatabase.CopyAsset(src + "Tx_Burst_radial_01.png", root + "/Addr/C.png");
return ok ? "ok" : "copy failed";
'@
    $r = Invoke-Eval 'addr-tex.cs' $code.Replace('{{pack}}', $impPack)
    Expect ($r -eq 'ok') "scratch textures: $r"
}
function Invoke-AddrEdit([string]$name, [string]$ops) { Invoke-Cli @('cliext.addressables', 'edit', '--ops-file', (Write-WorkFile $name $ops), '--timeout', '120', '--format', 'json') 150 }
function Use-Addr([scriptblock]$body) {
    # Runs $body with fresh Addressables settings, then removes every trace of them; returns the body's detail.
    $st = Invoke-Cli @('cliext.addressables', 'status', '--format', 'json')
    Expect ($st.exit -eq 0 -and $st.json.initialized -eq $false) "the project has Addressables settings before the check (a run that didn't clean up?): $(Short $st.stdout)"
    $git0 = Get-AddrGit
    $serverData = Test-Path (Join-Path $projectPath 'ServerData')
    $saved = Save-ProjectSettingsAll
    # A content build runs URP's build preprocessing, which rewrites the shader prefiltering of the pipeline assets.
    $rp = @{}; foreach ($f in Get-ChildItem (Join-Path $projectPath 'Assets\Settings') -Filter '*.asset' -ErrorAction SilentlyContinue) { $rp[$f.FullName] = [IO.File]::ReadAllBytes($f.FullName) }
    New-AddrTextures
    try {
        $i = Invoke-Cli @('cliext.addressables', 'init', '--format', 'json') 120
        Expect ($i.exit -eq 0 -and $i.json.created -eq $true -and $i.json.settings -eq "$addrData/AddressableAssetSettings.asset") "init: exit $($i.exit): $(Short ($i.stdout + $i.stderr))"
        $detail = & $body
    }
    finally {
        $rm = Remove-Addr
        if (-not $serverData -and (Test-Path (Join-Path $projectPath 'ServerData'))) { Remove-Item (Join-Path $projectPath 'ServerData') -Recurse -Force }
        $put = Restore-ProjectSettingsAll $saved
        foreach ($k in @($rp.Keys)) {
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($k)) -eq [Convert]::ToBase64String($rp[$k])) { continue }
            [IO.File]::WriteAllBytes($k, $rp[$k])
            $rel = 'Assets/Settings/' + (Split-Path $k -Leaf)
            [void](Invoke-Eval 'addr-rp.cs' "UnityEditor.AssetDatabase.ImportAsset(`"$rel`", UnityEditor.ImportAssetOptions.ForceUpdate); return `"ok`";")
            $put += $rel
        }
    }
    Expect ($rm -eq 'True|True') "removing the Addressables data: $rm"
    Expect (-not (Test-Path (Join-Path $projectPath 'Assets\AddressableAssetsData')) -and -not (Test-Path (Join-Path $projectPath 'Assets\AddressableAssetsData.meta'))) 'Assets/AddressableAssetsData is still there'
    $git1 = Get-AddrGit
    Expect ($git1 -eq $git0) "git status of the Addressables paths changed: '$git0' -> '$git1'"
    "$detail; cleaned up$(if ($put.Count) { " (written back: $($put -join ', '))" })"
}

Add-Check 'addr.before-init' $addrRow 'before cliext.addressables init: status says initialized false (exit 0); entries, edit, build and analyze exit 6 naming init; nothing is created (no AddressableAssetsData, ProjectSettings unchanged)' {
    $git0 = Get-AddrGit; $saved = Save-ProjectSettingsAll
    $st = Invoke-Cli @('cliext.addressables', 'status', '--format', 'json')
    Expect ($st.exit -eq 0 -and $st.json.initialized -eq $false -and $st.json.next -eq 'cliext.addressables init') "status: exit $($st.exit): $(Short ($st.stdout + $st.stderr))"
    $ops = Write-WorkFile 'addr-pre.json' '[{"op":"add_label","name":"cli"}]'
    $seen = @()
    foreach ($c in @(@('entries'), @('edit', '--ops-file', $ops), @('build', '--timeout', '60'), @('analyze', '--timeout', '60'))) {
        $x = Invoke-Cli (@('cliext.addressables') + $c + @('--format', 'json')) 90
        Expect-P3Failure $x 'no Addressables settings yet: run cliext\.addressables init first' "$($c[0]) before init" 6
        $seen += "$($c[0]) $($x.exit)"
    }
    Expect (-not (Test-Path (Join-Path $projectPath 'Assets\AddressableAssetsData'))) 'a command before init created Assets/AddressableAssetsData'
    $put = Restore-ProjectSettingsAll $saved
    Expect ($put.Count -eq 0) "a command before init wrote $($put -join ', ')"
    Expect ((Get-AddrGit) -eq $git0) 'git status changed'
    "status initialized false; $($seen -join ', '); nothing created"
}

Add-Check 'addr.init-status' $addrRow 'cliext.addressables init creates the settings (AddressableAssetSettingsDefaultObject and the asset on disk), status lists its groups, profile and labels, init again exits 6; everything it made is removed after' {
    Use-Addr {
        $e = Invoke-Eval 'addr-init.cs' 'var s = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.GetSettings(false); return s == null ? "none" : UnityEditor.AssetDatabase.GetAssetPath(s) + "|" + s.DefaultGroup.Name + "|" + s.groups.Count(g => g != null);'
        Expect ($e -match "^$addrData/AddressableAssetSettings\.asset\|([^|]+)\|(\d+)$") "AddressableAssetSettings after init: $e"
        $default = $Matches[1]; $count = [int]$Matches[2]
        Expect (Test-Path (Join-Path $projectPath 'Assets\AddressableAssetsData\AddressableAssetSettings.asset')) 'the settings asset is not on disk'
        $st = Invoke-Cli @('cliext.addressables', 'status', '--format', 'json')
        Expect ($st.exit -eq 0 -and $st.json.initialized -eq $true -and @($st.json.groups).Count -eq $count -and @(@($st.json.groups) | Where-Object { $_.name -eq $default -and $_.isDefault }).Count -eq 1 -and $st.json.profile) "status: exit $($st.exit): $(Short $st.stdout 300)"
        $again = Invoke-Cli @('cliext.addressables', 'init', '--format', 'json')
        Expect-P3Failure $again "settings exist already at 'Assets/AddressableAssetsData/AddressableAssetSettings\.asset': nothing would change" 'init twice' 6
        Expect ((Get-AddrSig) -match [regex]::Escape("$default*")) "init twice changed the settings: $(Get-AddrSig)"
        "settings with $count group(s), default '$default', profile $($st.json.profile); init again exit 6"
    }
}

Add-Check 'addr.edit' $addrRow 'cliext.addressables edit: a batch adds a group (PackSeparately, LZ4), two entries with addresses and labels and a label; a second moves, relabels, renames, sets includeInBuild, removes an entry and a label; entries (--group, --label, --filter) and the settings in memory and on disk show each change' {
    Use-Addr {
        $a = "$addrRel/A.png"; $b = "$addrRel/B.png"
        $default = (Invoke-Cli @('cliext.addressables', 'status', '--format', 'json')).json.groups | Where-Object { $_.isDefault } | ForEach-Object { $_.name }
        $ops1 = @"
[{"op":"add_group","name":"CliTest Group","set":"bundleMode=PackSeparately;compression=LZ4"},
 {"op":"add_entry","path":"$a","group":"CliTest Group","address":"cli/a","labels":"cliA;shared"},
 {"op":"add_entry","path":"$b","labels":"shared"},
 {"op":"set_address","path":"$b","address":"cli/b"},
 {"op":"add_label","name":"extra"}]
"@
        $r1 = Invoke-AddrEdit 'addr-edit1.json' $ops1
        Expect ($r1.exit -eq 0 -and $r1.json.applied -eq 5 -and $r1.json.saved -eq $true) "batch 1: exit $($r1.exit): $(Short ($r1.stdout + $r1.stderr))"
        $sig1 = Get-AddrSig
        Expect ($sig1 -like 'labels=cliA,default,extra,shared |*' -or $sig1 -like 'labels=cliA,extra,shared |*') "labels after batch 1: $sig1"
        Expect ($sig1 -like '* CliTest Group`[PackSeparately,LZ4,True`]{A.png=cli/a:cliA,shared;}*') "the new group after batch 1: $sig1"
        Expect ($sig1 -like "* $default*{B.png=cli/b:shared;}*") "the default group after batch 1: $sig1"
        Expect (@(Get-ChildItem (Join-Path $projectPath 'Assets\AddressableAssetsData\AssetGroups') -Filter 'CliTest Group.asset').Count -eq 1) 'the new group asset is not on disk'
        Expect ((Get-Content (Join-Path $projectPath 'Assets\AddressableAssetsData\AssetGroups\CliTest Group.asset') -Raw) -match 'm_Address: cli/a') 'the saved group file lacks the entry'
        $eg = Invoke-Cli @('cliext.addressables', 'entries', '--group', 'clitest group', '--format', 'json')
        Expect ($eg.exit -eq 0 -and @($eg.json).Count -eq 1 -and @($eg.json)[0].address -eq 'cli/a' -and @($eg.json)[0].path -eq $a -and (@(@($eg.json)[0].labels) -join ',') -eq 'cliA,shared') "entries --group: $(Short $eg.stdout)"
        $el = Invoke-Cli @('cliext.addressables', 'entries', '--label', 'shared', '--format', 'json')
        Expect ($el.exit -eq 0 -and (@(@($el.json) | ForEach-Object { $_.address } | Sort-Object) -join ',') -eq 'cli/a,cli/b') "entries --label: $(Short $el.stdout)"
        $ef = Invoke-Cli @('cliext.addressables', 'entries', '--filter', 'B.png', '--format', 'json')
        Expect ($ef.exit -eq 0 -and @($ef.json).Count -eq 1 -and @($ef.json)[0].group -eq $default) "entries --filter: $(Short $ef.stdout)"
        $none = Invoke-Cli @('cliext.addressables', 'entries', '--filter', 'no-such-entry', '--format', 'json')
        Expect ($none.exit -eq 0 -and "$($none.stdout)".Trim() -eq '') "entries --filter with no match: exit $($none.exit): $(Short $none.stdout)"
        $ops2 = @"
[{"op":"move_entry","path":"$b","group":"CliTest Group"},
 {"op":"set_labels","path":"$a","labels":"cliA"},
 {"op":"remove_label","name":"extra"},
 {"op":"rename_group","group":"CliTest Group","name":"CliTest Renamed"},
 {"op":"set_group","group":"CliTest Renamed","set":"includeInBuild=false;compression=LZMA"},
 {"op":"remove_entry","path":"$a"}]
"@
        $r2 = Invoke-AddrEdit 'addr-edit2.json' $ops2
        Expect ($r2.exit -eq 0 -and $r2.json.applied -eq 6) "batch 2: exit $($r2.exit): $(Short ($r2.stdout + $r2.stderr))"
        $sig2 = Get-AddrSig
        Expect ($sig2 -like '* CliTest Renamed`[PackSeparately,LZMA,False`]{B.png=cli/b:shared;}*' -and $sig2 -notlike '*CliTest Group*' -and $sig2 -notlike '*A.png*' -and $sig2 -notlike '*extra*') "after batch 2: $sig2"
        Expect ((Test-Path (Join-Path $projectPath 'Assets\AddressableAssetsData\AssetGroups\CliTest Renamed.asset')) -and -not (Test-Path (Join-Path $projectPath 'Assets\AddressableAssetsData\AssetGroups\CliTest Group.asset'))) 'the renamed group file is not on disk under its new name'
        $r3 = Invoke-AddrEdit 'addr-edit3.json' '[{"op":"remove_group","group":"CliTest Renamed"}]'
        Expect ($r3.exit -eq 0 -and (Get-AddrSig) -notlike '*CliTest*') "remove_group: exit $($r3.exit): $(Short ($r3.stdout + $r3.stderr)) $(Get-AddrSig)"
        "batch 1 (5 ops) and 2 (6 ops) applied as asked; $(Short $sig2 120)"
    }
}

Add-Check 'addr.edit-rollback' $addrRow 'cliext.addressables edit batches whose last op fails after others changed things (a group added with an entry, a group removed, a group renamed, labels changed) exit 6, save nothing, and leave the settings in memory and every AddressableAssetsData file as before; the next good edit saves only its own change' {
    Use-Addr {
        $a = "$addrRel/A.png"; $b = "$addrRel/B.png"; $c = "$addrRel/C.png"
        $setup = Invoke-AddrEdit 'addr-rb0.json' "[{`"op`":`"add_group`",`"name`":`"Keep`"},{`"op`":`"add_entry`",`"path`":`"$a`",`"group`":`"Keep`",`"address`":`"keep/a`",`"labels`":`"one`"},{`"op`":`"add_entry`",`"path`":`"$b`",`"address`":`"keep/b`"}]"
        Expect ($setup.exit -eq 0) "setup: exit $($setup.exit): $(Short ($setup.stdout + $setup.stderr))"
        $default = (Invoke-Cli @('cliext.addressables', 'status', '--format', 'json')).json.groups | Where-Object { $_.isDefault } | ForEach-Object { $_.name }
        $sig0 = Get-AddrSig; $files0 = Get-AddrFiles
        $cases = @(
            @("[{`"op`":`"add_group`",`"name`":`"Temp`",`"set`":`"compression=LZMA`"},{`"op`":`"add_entry`",`"path`":`"$c`",`"group`":`"Temp`"},{`"op`":`"rename_group`",`"group`":`"Temp`",`"name`":`"Keep`"}]", 2, "Group 'Keep' exists already", 'a new group with an entry, then a rename onto an existing name'),
            @("[{`"op`":`"remove_group`",`"group`":`"Keep`"},{`"op`":`"add_label`",`"name`":`"fresh`"},{`"op`":`"set_address`",`"path`":`"$c`",`"address`":`"x`"}]", 2, "isn't Addressable: use add_entry", 'a removed group and a new label, then an address for an asset that is not an entry'),
            @("[{`"op`":`"rename_group`",`"group`":`"Keep`",`"name`":`"Kept`"},{`"op`":`"set_labels`",`"path`":`"$a`",`"labels`":`"two;three`"},{`"op`":`"move_entry`",`"path`":`"$b`",`"group`":`"Kept`"},{`"op`":`"remove_label`",`"name`":`"none-such`"}]", 3, "No label 'none-such'", 'a rename, new labels and a move, then removing a label that is not there'),
            @("[{`"op`":`"set_group`",`"group`":`"$default`",`"set`":`"bundleMode=PackSeparately`"},{`"op`":`"remove_entry`",`"path`":`"$b`"},{`"op`":`"add_entry`",`"path`":`"$a`"}]", 2, 'is Addressable already \(in group Keep', 'a group set and an entry removed, then adding an asset that is an entry')
        )
        $seen = @()
        foreach ($k in $cases) {
            $r = Invoke-AddrEdit 'addr-rb.json' $k[0]
            Expect-P3Failure $r ("Op $($k[1]) \(.*\) failed: .*$($k[2]).*Nothing was changed") $k[3] 6
            Expect ($r.json.saved -eq $false -and $r.json.failedAt -eq $k[1] -and $r.json.applied -eq 0) "$($k[3]): the reply: $(Short $r.stdout)"
            $sig = Get-AddrSig
            Expect ($sig -eq $sig0) "$($k[3]): the settings in memory changed: $sig0 -> $sig"
            Expect ((Get-AddrFiles) -eq $files0) "$($k[3]): a file in AssetDatabase data changed or was left: $(Short (Get-AddrFiles) 300)"
            $seen += $k[3]
        }
        $ok = Invoke-AddrEdit 'addr-rb-ok.json' '[{"op":"add_label","name":"after"}]'
        Expect ($ok.exit -eq 0) "the good edit after: exit $($ok.exit): $(Short ($ok.stdout + $ok.stderr))"
        $sig1 = Get-AddrSig
        Expect ($sig1 -eq ($sig0 -replace '^labels=', 'labels=after,') -or $sig1 -eq (($sig0 -replace '^labels=([^ ]*) \|', 'labels=$1,after |'))) "the good edit saved more than its label: $sig0 -> $sig1"
        "$($seen.Count) failed batches put back in memory and on disk; the next edit added only its label"
    }
}

Add-Check 'addr.edit-errors' $addrRow 'cliext.addressables edit with an unknown op, a missing or outside asset, an unknown group or label (Did you mean), bad set values, a bad label or group name, and ops that would change nothing exits 6 with the fix and failedAt; bad JSON or no ops exits 2; nothing changes' {
    Use-Addr {
        $a = "$addrRel/A.png"
        $setup = Invoke-AddrEdit 'addr-err0.json' "[{`"op`":`"add_group`",`"name`":`"Enemies`"},{`"op`":`"add_entry`",`"path`":`"$a`",`"group`":`"Enemies`",`"address`":`"enemy/a`",`"labels`":`"enemies`"}]"
        Expect ($setup.exit -eq 0) "setup: exit $($setup.exit): $(Short ($setup.stdout + $setup.stderr))"
        $sig0 = Get-AddrSig; $files0 = Get-AddrFiles
        $cases = @(
            @('[{"op":"add_entyr","path":"x"}]', 'Unknown op .add_entyr.\. Did you mean: add_entry\? Ops: ', 'a misspelled op'),
            @("[{`"op`":`"add_label`",`"name`":`"ok`"},{`"op`":`"add_entry`",`"path`":`"$addrRel/Nope.png`"}]", "Op 1 \(add_entry\) failed: No asset at '.*Nope\.png'\. Check the path \(asset find", 'a missing asset (after a good op)'),
            @('[{"op":"add_entry","path":"C:/Temp/a.png"}]', "isn't under Assets/ or Packages/", 'an asset outside the project'),
            @('[{"op":"add_entry"}]', 'add_entry needs path', 'add_entry without a path'),
            @("[{`"op`":`"move_entry`",`"path`":`"$a`",`"group`":`"Enemeis`"}]", "No group 'Enemeis'\. Did you mean: Enemies\?", 'a misspelled group'),
            @("[{`"op`":`"move_entry`",`"path`":`"$a`",`"group`":`"Zzzzzzzzzz`"}]", "No group 'Zzzzzzzzzz'\. Groups: .*Enemies.*cliext\.addressables status lists them", 'an unknown group'),
            @('[{"op":"remove_label","name":"enemeis"}]', "No label 'enemeis'\. Did you mean: enemies\?", 'a misspelled label'),
            @('[{"op":"set_group","group":"Enemies","set":"compression=zip"}]', "compression: 'zip' isn't a bundle compression mode\. Use one of: Uncompressed, LZ4, LZMA", 'a bad compression'),
            @('[{"op":"set_group","group":"Enemies","set":"bundleMode=PackTogether;includeInBuild=maybe"}]', 'includeInBuild: .*true or false', 'a bad bool'),
            @('[{"op":"set_group","group":"Enemies","set":"bundelMode=PackTogether"}]', "No group setting 'bundelMode'\. Did you mean: bundleMode\?", 'a misspelled setting'),
            @('[{"op":"set_group","group":"Enemies","set":"compression"}]', "'compression' isn't name=value", 'a setting without a value'),
            @('[{"op":"set_group","group":"Enemies"}]', 'set_group needs set', 'set_group without set'),
            @('[{"op":"add_group","name":"A/B"}]', "has '/', which a group's file name can't", 'a group name with a slash'),
            @('[{"op":"add_label","name":"[x]"}]', "has '\[' or '\]'", 'a label with brackets'),
            @('[{"op":"add_group","name":"enemies"}]', "Group 'Enemies' exists already", 'a group that exists'),
            @('[{"op":"rename_group","group":"Enemies","name":"Enemies"}]', 'has that name already: nothing would change', 'a rename to the same name'),
            @('[{"op":"set_group","group":"Enemies","set":"includeInBuild=true"}]', 'nothing would change', 'a set to the value it has'),
            @("[{`"op`":`"add_entry`",`"path`":`"$a`"}]", 'is Addressable already \(in group Enemies, address .enemy/a.\): use move_entry', 'an entry added twice'),
            @("[{`"op`":`"move_entry`",`"path`":`"$a`",`"group`":`"Enemies`"}]", 'is in group Enemies already: nothing would change', 'a move to its own group'),
            @("[{`"op`":`"set_address`",`"path`":`"$a`",`"address`":`"enemy/a`"}]", "has the address 'enemy/a' already", 'an address it has'),
            @("[{`"op`":`"set_labels`",`"path`":`"$a`",`"labels`":`"enemies`"}]", 'has the labels \[enemies\] already', 'labels it has'),
            @('[{"op":"add_label","name":"enemies"}]', "has the label 'enemies' already", 'a label that exists'),
            @("[{`"op`":`"remove_entry`",`"path`":`"$addrRel/B.png`"}]", "isn't Addressable: nothing to remove\. cliext\.addressables entries lists", 'removing an asset that is not an entry')
        )
        $seen = @()
        foreach ($k in $cases) {
            $r = Invoke-AddrEdit 'addr-err.json' $k[0]
            Expect-P3Failure $r $k[1] $k[2] 6
            Expect ($r.json.saved -eq $false -and $r.json.failedAt -ne $null) "$($k[2]): the reply has no failedAt: $(Short $r.stdout)"
            $seen += $k[2]
        }
        foreach ($k in @(@('[{"op":', 'aren''t valid JSON', 'bad JSON'), @('[]', 'No ops given', 'no ops'))) {
            $r = Invoke-AddrEdit 'addr-err.json' $k[0]
            Expect-P3Failure $r $k[1] $k[2] 2
            $seen += $k[2]
        }
        Expect ((Get-AddrSig) -eq $sig0) "a failed edit changed the settings in memory: $sig0 -> $(Get-AddrSig)"
        Expect ((Get-AddrFiles) -eq $files0) 'a failed edit changed a file in AddressableAssetsData'
        $eg = Invoke-Cli @('cliext.addressables', 'entries', '--group', 'Enemeis', '--format', 'json')
        Expect-P3Failure $eg "No group 'Enemeis'\. Did you mean: Enemies\?" 'entries --group misspelled' 13
        $el = Invoke-Cli @('cliext.addressables', 'entries', '--label', 'nolabel', '--format', 'json')
        Expect-P3Failure $el "No label 'nolabel'\. Labels: .*enemies.*cliext\.addressables status lists them" 'entries --label unknown' 13
        "$($seen.Count) refused edits and 2 refused entries filters; nothing changed"
    }
}

Add-Check 'addr.build' $addrRow 'cliext.addressables build --clean true of two scratch textures in a PackSeparately group writes its bundles (the reply''s bundles and output are on disk); with an unsaved scene open it exits 6 at once, naming the scene, instead of stopping at Unity''s save dialog' {
    Use-Addr {
        $a = "$addrRel/A.png"; $b = "$addrRel/B.png"
        $e = Invoke-AddrEdit 'addr-build.json' "[{`"op`":`"add_group`",`"name`":`"CliBuild`",`"set`":`"bundleMode=PackSeparately`"},{`"op`":`"add_entry`",`"path`":`"$a`",`"group`":`"CliBuild`"},{`"op`":`"add_entry`",`"path`":`"$b`",`"group`":`"CliBuild`"}]"
        Expect ($e.exit -eq 0) "edit: exit $($e.exit): $(Short ($e.stdout + $e.stderr))"
        $buildPath = (Invoke-Cli @('cliext.addressables', 'status', '--format', 'json')).json.groups | Where-Object { $_.name -eq 'CliBuild' } | ForEach-Object { $_.buildPath }
        $r = Invoke-Cli @('cliext.addressables', 'build', '--clean', 'true', '--timeout', '900', '--format', 'json') 960
        Expect ($r.exit -ne 124) 'build timed out (a dialog?)'
        Expect ($r.exit -eq 0 -and $r.json.success -eq $true -and $r.json.bundles -ge 2) "build: exit $($r.exit): $(Short ($r.stdout + $r.stderr) 300)"
        $out = Join-Path $projectPath ($buildPath -replace '/', '\')
        $bundles = @(Get-ChildItem $out -Filter '*.bundle' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*clibuild*' })
        Expect ($bundles.Count -eq 2) "bundles of CliBuild in ${buildPath}: $(@(Get-ChildItem $out -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')"
        $settingsJson = if ($r.json.output) { $(if ([IO.Path]::IsPathRooted($r.json.output)) { $r.json.output } else { Join-Path $projectPath $r.json.output }) } else { '' }
        Expect ($settingsJson -and (Test-Path $settingsJson)) "the build's output '$($r.json.output)' is not on disk"
        $was = Open-P3Scene
        [void](Invoke-Eval 'addr-dirty.cs' 'UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene()); return UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty.ToString();')
        try { $d = Invoke-Cli @('cliext.addressables', 'build', '--timeout', '60', '--format', 'json') 90 }
        finally { $back = Close-P3Scene $was }
        Expect-P3Failure $d '\(untitled\) has unsaved changes, and the build would stop at Unity.s "Unsaved Scenes" dialog\. Save them \(cliext\.scene save_open' 'build with an unsaved scene' 6
        Expect ($d.ms -lt 30000) "the refused build took $($d.ms) ms"
        Expect ($back -match '\|False\|') "reopening $was`: $back"
        "$($r.json.bundles) bundles in $($r.json.seconds)s ($($bundles.Count) CliBuild bundles in $buildPath); unsaved scene: exit 6 in $($d.ms) ms"
    }
}

Add-Check 'addr.analyze' $addrRow 'cliext.addressables analyze runs every registered rule (one record per rule, no base classes, exit 0 with no errors) and --rule narrows to one; an unknown --rule exits 13 with Did you mean or the rule names, --limit 0 exits 2, an unsaved scene exits 6; the settings and the render pipeline assets are left as they were' {
    Use-Addr {
        $a = "$addrRel/A.png"
        $e = Invoke-AddrEdit 'addr-an.json' "[{`"op`":`"add_entry`",`"path`":`"$a`",`"address`":`"an/a`"}]"
        Expect ($e.exit -eq 0) "edit: exit $($e.exit): $(Short ($e.stdout + $e.stderr))"
        $sig0 = Get-AddrSig; $files0 = Get-AddrFiles
        $rpHash = { (@(Get-ChildItem (Join-Path $projectPath 'Assets\Settings') -Filter '*.asset' | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ',') }
        $rp0 = & $rpHash
        $all = Invoke-Cli @('cliext.addressables', 'analyze', '--timeout', '600', '--format', 'json') 660
        $rules = @(@($all.json) | Where-Object { $_.rule })
        Expect (@($rules | Where-Object { $_.rule -match 'RuleBase' }).Count -eq 0) "analyze ran a base class as a rule: $(@($rules.rule) -join ', ')"
        Expect ($all.exit -eq 0 -and $rules.Count -ge 3 -and @($rules | Where-Object { $_.error }).Count -eq 0) "analyze: exit $($all.exit), $($rules.Count) rules: $(Short ($all.stdout + $all.stderr) 400)"
        # A rule whose only result is "No issues found" (severity None) counts 0 issues and lists no results.
        $noIssue = @($rules | Where-Object { @($_.results | Where-Object { $_.severity -eq 'None' -and $_.name -match 'No issues found' }).Count -gt 0 })
        Expect ($noIssue.Count -eq 0) "analyze counted a 'No issues found' result as an issue: $(@($noIssue | ForEach-Object { "$($_.rule) issues $($_.issues)" }) -join '; ')"
        Expect (@($rules | Where-Object { $_.rule -match 'Duplicate' -and $_.issues -ne 0 }).Count -eq 0) "a duplicate rule reports issues on one scratch entry: $(Short $all.stdout 300)"
        $layout = @($rules | Where-Object { $_.rule -match 'Layout' })[0].rule
        $one = Invoke-Cli @('cliext.addressables', 'analyze', '--rule', 'layout', '--limit', '5', '--timeout', '600', '--format', 'json') 660
        Expect ($one.exit -eq 0 -and @($one.json).Count -eq 1 -and @($one.json)[0].rule -eq $layout -and @(@($one.json)[0].results).Count -le 5) "analyze --rule layout: exit $($one.exit): $(Short $one.stdout 300)"
        $seen = @()
        foreach ($c in @(
                @(@('--rule', 'Bundle Layuot Preview'), "No Analyze rule's name contains 'Bundle Layuot Preview'\. Did you mean: Bundle Layout Preview\?", 13, 'a misspelled rule'),
                @(@('--rule', 'zzzzzz'), "No Analyze rule's name contains 'zzzzzz'\. Rules: .*Layout", 13, 'an unknown rule'),
                @(@('--limit', '0'), '--limit 0: give 1 or more', 2, '--limit 0'))) {
            $x = Invoke-Cli (@('cliext.addressables', 'analyze') + $c[0] + @('--format', 'json')) 60
            Expect-P3Failure $x $c[1] $c[3] $c[2]
            $seen += "$($c[3]) $($x.exit)"
        }
        $was = Open-P3Scene
        [void](Invoke-Eval 'addr-dirty.cs' 'UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene()); return UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty.ToString();')
        try { $d = Invoke-Cli @('cliext.addressables', 'analyze', '--timeout', '60', '--format', 'json') 90 }
        finally { $back = Close-P3Scene $was }
        Expect-P3Failure $d 'has unsaved changes, and the Analyze rules would stop at Unity.s "Unsaved Scenes" dialog' 'analyze with an unsaved scene' 6
        Expect ($back -match '\|False\|') "reopening $was`: $back"
        Expect ((Get-AddrSig) -eq $sig0) "analyze changed the settings: $sig0 -> $(Get-AddrSig)"
        Expect ((& $rpHash) -eq $rp0) 'analyze left a render pipeline asset in Assets/Settings changed (the bundle rules'' build step rewrites its shader prefiltering)'
        "$($rules.Count) rules ($(@($rules | ForEach-Object { "$($_.rule): $($_.issues)" }) -join '; ')); $($seen -join ', '); unsaved scene exit 6"
    }
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
            $norm = { param($p) ((Get-Content $p -Raw) -replace "`r", '' -split "`n" | Where-Object { $_ -notmatch '^<!-- [\w-]+ \d+\.\d+\.\d+: installed by Unity CLI Extensions' }) -join "`n" }
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

Add-Check 'env.play-capture-run' 'Environment' 'cliext-play-capture.ps1 -Run "a","b" through powershell -File (which joins them into "a,b") and through & (a real array), and "a;;b", each give two steps; commas inside an argument stay; a bad quote exits 2' {
    # What an agent gets typing the same thing; -DryRun prints the parsed steps without touching the Editor.
    $ErrorActionPreference = 'Continue'
    $capture = Join-Path $tools 'cliext-play-capture.ps1'
    function Steps($out) { $j = ($out | Out-String) | ConvertFrom-Json; @($j.run | ForEach-Object { (@($_) -join ' ') }) }
    $viaFile = & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -Project $projectPath -DryRun -Run "cliext.playmode time_scale --value 0.01","debugmenu do --section 'A, B' --position 1,2,3" a@0
    $s1 = Steps $viaFile
    Expect ($s1.Count -eq 2 -and $s1[0] -eq 'cliext.playmode time_scale --value 0.01' -and $s1[1] -eq 'debugmenu do --section A, B --position 1,2,3') "via -File: $($s1 -join ' | ')"
    $viaAmp = & $capture -Project $projectPath -DryRun -Run "debugmenu do --label One","debugmenu do --label Two" a@0
    $s2 = Steps $viaAmp
    Expect ($s2.Count -eq 2 -and $s2[1] -eq 'debugmenu do --label Two') "via &: $($s2 -join ' | ')"
    $viaSep = & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -Project $projectPath -DryRun -Run "unity command scene save;;debugmenu do --tags Red,Blue" a@0
    $s3 = Steps $viaSep
    Expect ($s3.Count -eq 2 -and $s3[0] -eq 'scene save' -and $s3[1] -eq 'debugmenu do --tags Red,Blue') "with ;;: $($s3 -join ' | ')"
    $one = @(Steps (& powershell -NoProfile -ExecutionPolicy Bypass -File $capture -Project $projectPath -DryRun -Run "debugmenu do --label Fire,Ice" a@0))
    Expect ($one.Count -eq 1 -and $one[0] -eq 'debugmenu do --label Fire,Ice') "one step: $($one -join ' | ')"
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -Project $projectPath -DryRun -Run "debugmenu do --label 'One" a@0 2>&1
    Expect ($LASTEXITCODE -eq 2) "an unclosed quote: exit $LASTEXITCODE"
    "-File joined: 2 steps; & array: 2; ';;': 2; commas in arguments kept; unclosed quote exit 2"
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
        # P3 rows
        'cliext.cinemachine' = '^cliext\.cinemachine '; 'cliext.splines' = '^cliext\.splines '
        'cliext.navmesh' = '^cliext\.navmesh '; 'cliext.physics' = '^cliext\.physics '; 'cliext.quality' = '^cliext\.quality '
        'cliext.import, cliext.presets' = '^cliext\.(import|presets) '; 'cliext.package' = '^cliext\.package '
        'cliext.asset, cliext.diff' = '^cliext\.(asset|diff) '; 'cliext.addressables' = '^cliext\.addressables '
    }
    $q = Invoke-Cli @('--query', 'cliext', '--format', 'json') 60
    $all = @()
    if ($q.json -ne $null) { $all = @($q.json.data.commands | ForEach-Object { $_.command }) }
    Write-Output 'T1 coverage (P1, P2 and P3 rows; commands called at least once):'
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

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

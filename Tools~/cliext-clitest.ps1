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

Exit codes: 0 when every check passed, 1 when one failed, 2 when no Editor answered.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-clitest.ps1 -Project E:/Games/MyGame
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-clitest.ps1 -Project E:/Games/MyGame -Filter '^(xcut|env)\.' -Out results.json
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Filter = '.',
    [string]$Out = '',
    [switch]$Keep
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

function Invoke-Cli([string[]]$CliArgs, [double]$Timeout = 120) {
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

Add-Check 'dialogs.log-and-scan' 'cliext.dialogs, cliext.watchdog' 'the dialog journal and the blocking-call scan answer' {
    $a = Invoke-Cli @('cliext.dialogs', 'log', '--tail', '5', '--format', 'json')
    $b = Invoke-Cli @('cliext.dialogs', 'scan', '--format', 'json')
    Expect ($a.exit -eq 0) "log: exit $($a.exit): $(Short $a.stderr)"
    Expect ($b.exit -eq 0) "scan: exit $($b.exit): $(Short $b.stderr)"
    "journal entries: $(@($a.json).Count); blocking calls found: $(@($b.json).Count)"
}

Add-Check 'dialogs.watchdog-roundtrip' 'cliext.dialogs, cliext.watchdog' 'the watchdog starts, reports the Editor ready, and stops' {
    $s = Invoke-Cli @('cliext.watchdog', 'start', '--format', 'json')
    Expect ($s.exit -eq 0) "start: exit $($s.exit): $(Short ($s.stdout + $s.stderr))"
    $state = ''
    for ($i = 0; $i -lt 10 -and $state -eq ''; $i++) {
        $rep = Invoke-Cli @('cliext.watchdog', 'report', '--format', 'json')
        if ("$($rep.stdout)" -match '"state"\s*:\s*"([^"]+)"') { $state = $Matches[1] } else { Start-Sleep -Milliseconds 500 }
    }
    $t = Invoke-Cli @('cliext.watchdog', 'stop', '--format', 'json')
    Expect ($state -eq 'ready') "report state '$state'"
    Expect ($t.exit -eq 0) "stop: exit $($t.exit): $(Short ($t.stdout + $t.stderr))"
    "state $state"
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
            if ((& $norm $file.FullName).Trim() -ne (& $norm $other).Trim()) { $stale += "$($skill.Name)$rel differs" }
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

# ---------------------------------------------------------------------------------------------------------------------
# Run

$probe = Invoke-Cli @('cliext', 'status', '--format', 'json') 30
if ($probe.exit -ne 0) { Write-Output "No Editor answered for $projectPath (exit $($probe.exit)): $(Short ($probe.stdout + $probe.stderr))"; exit 2 }

$results = New-Object Collections.Generic.List[object]
foreach ($c in $checks | Where-Object { $_.id -match $Filter }) {
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
if ($Out) { $results | ConvertTo-Json -Depth 4 | Set-Content -Path $Out -Encoding UTF8 }
if ($failed -gt 0) { exit 1 } else { exit 0 }

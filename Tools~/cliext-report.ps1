<#
.SYNOPSIS
One-step bug report for Unity CLI Extensions: zips everything the package author needs to act on a problem.

.DESCRIPTION
Builds <project>\CLIExtensionsReports\cliext-report-<yyyyMMdd-HHmmss>.zip, prints its full path and opens Explorer
with it selected. Nothing is uploaded: send the zip yourself (attach it to an issue at
https://github.com/qhenshaw/UnityCLIExtensions/issues) after looking through it.

It reads files directly, so it works when the Editor is hung or closed. It asks the Editor (cliext status, cliext.setup
check, cliext.compile diagnostics) only if it answers within a few seconds, and records whether it did.

The zip holds:
- the field report (field-report.md and .json, from cliext-field-report.ps1): failed tool calls, workarounds, field
  notes and failures, each with excerpts of the agent transcript around it;
- failures.jsonl, field-notes.jsonl, dialogs.jsonl (the dialog and watchdog journal), the watchdog status and policy,
  heartbeat.json and compile.json from Library/CLIExtensions;
- the Unity, Pipeline and package versions, Packages/manifest.json and packages-lock.json;
- each installed skill's name, version stamp and whether it was edited; the CLAUDE.md cliext block;
- the last 2000 lines of Editor.log (and Editor-prev.log); the OS and PowerShell versions;
- the Editor's answers, when it answered;
- with -FullTranscripts, the whole transcripts of the sessions that hold a note or a failure (without it, excerpts only).

Exit codes: 0 when the zip was written, 2 for a bad argument (no such project).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-report.ps1 -Project E:/Games/MyGame
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-report.ps1 -Project E:/Games/MyGame -Days 3 -FullTranscripts
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [int]$Days = 14,
    # Add the full transcripts of the sessions that hold a note or a failure (they can be large and hold prompts).
    [switch]$FullTranscripts,
    # Don't ask the Editor anything; read files only.
    [switch]$NoEditor,
    # Don't open Explorer at the zip.
    [switch]$NoExplorer,
    # The folder for the zip (default <project>\CLIExtensionsReports).
    [string]$Out = '',
    # The exact zip path (the Editor passes this so it can say where the zip will be).
    [string]$Zip = '',
    # The Claude Code projects folder, for the field report (default $env:USERPROFILE\.claude\projects).
    [string]$TranscriptRoot = ''
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Project -PathType Container)) { Write-Error "No project folder at '$Project'."; exit 2 }
if ($Days -lt 1) { Write-Error "-Days $Days`: give 1 or more."; exit 2 }
$projectPath = (Resolve-Path -LiteralPath $Project).Path.TrimEnd('\', '/')
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPackage = Split-Path -Parent $tools
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $Zip) {
    if (-not $Out) { $Out = Join-Path $projectPath 'CLIExtensionsReports' }
    $Zip = Join-Path $Out "cliext-report-$stamp.zip"
}
$Zip = [IO.Path]::GetFullPath($Zip)
$stage = Join-Path ([IO.Path]::GetTempPath()) ("cliext-report-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$notes = New-Object Collections.Generic.List[string]   # what couldn't be collected, for README.txt
$state = Join-Path $projectPath 'Library\CLIExtensions'

function Save-Text([string]$rel, [string]$text) {
    $p = Join-Path $stage $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $p) | Out-Null
    [IO.File]::WriteAllText($p, $text, $utf8)
}
function Save-Json([string]$rel, $obj) { Save-Text $rel ((ConvertTo-Json -InputObject $obj -Depth 8) + "`n") }

# Reads a file another process may hold open (the Editor keeps its log and journals open).
function Read-Shared([string]$path) {
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $sr = New-Object IO.StreamReader($fs, $true); try { $sr.ReadToEnd() } finally { $sr.Dispose() } } finally { $fs.Dispose() }
}

# The last $lines lines of a file, reading only its end.
function Read-Tail([string]$path, [int]$lines, [long]$maxBytes = 4MB) {
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $start = [Math]::Max(0, $fs.Length - $maxBytes)
        [void]$fs.Seek($start, [IO.SeekOrigin]::Begin)
        $sr = New-Object IO.StreamReader($fs, $utf8)
        $text = $sr.ReadToEnd()
        $all = $text -split "`r?`n"
        if ($start -gt 0 -and $all.Count -gt 0) { $all = $all[1..($all.Count - 1)] }   # the first line is cut
        if ($all.Count -gt $lines) { $all = $all[($all.Count - $lines)..($all.Count - 1)] }
        return ($all -join "`n")
    }
    finally { $fs.Dispose() }
}

function Copy-State([string]$name, [string]$rel, [int]$maxLines = 20000) {
    $src = Join-Path $state $name
    if (-not (Test-Path -LiteralPath $src)) { $notes.Add("$name was not there (nothing recorded yet)."); return }
    try {
        if ($name -like '*.jsonl' -and (Get-Item -LiteralPath $src).Length -gt 8MB) {
            Save-Text $rel ((Read-Tail $src $maxLines 64MB) + "`n")
            $notes.Add("$name is large: only its last $maxLines lines are included.")
        }
        else { Save-Text $rel (Read-Shared $src) }
    }
    catch { $notes.Add("Couldn't read ${name}: $($_.Exception.Message)") }
}

# ---------------------------------------------------------------------------------------------------------------------
# The Editor, if it answers

function Invoke-Unity([string[]]$cliArgs, [double]$timeout) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $script:unity
    $info.Arguments = ((@('command', '--project-path', $projectPath) + $cliArgs) | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($info)
    $o = $p.StandardOutput.ReadToEndAsync()
    $e = $p.StandardError.ReadToEndAsync()
    $done = $p.WaitForExit([int]($timeout * 1000))
    if (-not $done) { try { $p.Kill() } catch { } }
    [ordered]@{
        command = 'unity ' + $info.Arguments; answered = $done; exit = $(if ($done) { $p.ExitCode } else { $null })
        ms = $sw.ElapsedMilliseconds; stdout = $(if ($done) { $o.Result } else { '' }); stderr = $(if ($done) { $e.Result } else { '' })
    }
}

$editor = [ordered]@{ queried = $false; answered = $false; reason = ''; time = (Get-Date).ToUniversalTime().ToString('o'); results = [ordered]@{} }
$heartbeat = Join-Path $state 'heartbeat.json'
if (Test-Path -LiteralPath $heartbeat) {
    $editor.heartbeatAgeSeconds = [Math]::Round(((Get-Date).ToUniversalTime() - (Get-Item -LiteralPath $heartbeat).LastWriteTimeUtc).TotalSeconds, 1)
}
if ($NoEditor) { $editor.reason = 'not asked (-NoEditor)' }
else {
    $cmd = Get-Command unity -ErrorAction SilentlyContinue
    if (-not $cmd) { $editor.reason = 'the unity CLI is not on PATH' }
    else {
        $script:unity = $cmd.Source
        $editor.queried = $true
        try {
            $status = Invoke-Unity @('cliext', 'status', '--format', 'json') 6
            $editor.results.status = $status
            if ($status.answered -and $status.exit -eq 0) {
                $editor.answered = $true
                $editor.results.setupCheck = Invoke-Unity @('cliext.setup', 'check', '--format', 'json') 10
                $editor.results.compileDiagnostics = Invoke-Unity @('cliext.compile', 'diagnostics', '--format', 'json') 10
            }
            else { $editor.reason = $(if ($status.answered) { "cliext status exited $($status.exit)" } else { 'no answer within 6 s (hung, busy, closed or blocked by a dialog)' }) }
        }
        catch { $editor.reason = "couldn't run unity: $($_.Exception.Message)" }
    }
}
Save-Json 'editor\editor-queries.json' $editor

# ---------------------------------------------------------------------------------------------------------------------
# The field report

$fieldReport = Join-Path $tools 'cliext-field-report.ps1'
$frOut = Join-Path $stage 'field-report'
$frArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fieldReport, '-Project', $projectPath, '-Days', "$Days", '-Out', $frOut, '-Quiet')
if ($TranscriptRoot) { $frArgs += @('-TranscriptRoot', $TranscriptRoot) }
# Windows PowerShell turns a native command's stderr into errors under -ErrorAction Stop; collect it as text instead.
$ErrorActionPreference = 'Continue'
$frLog = & powershell @frArgs 2>&1 | Out-String
$frExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($frExit -ne 0 -or -not (Test-Path (Join-Path $frOut 'field-report.json'))) {
    $notes.Add("The field report failed (exit $frExit): $($frLog.Trim())")
}

# ---------------------------------------------------------------------------------------------------------------------
# State files, versions, skills, logs

Copy-State 'failures.jsonl' 'state\failures.jsonl'
Copy-State 'field-notes.jsonl' 'state\field-notes.jsonl'
Copy-State 'dialogs.jsonl' 'state\dialogs.jsonl'
Copy-State 'watchdog-status.json' 'state\watchdog-status.json'
Copy-State 'watchdog-policy.json' 'state\watchdog-policy.json'
Copy-State 'heartbeat.json' 'state\heartbeat.json'
Copy-State 'compile.json' 'state\compile.json'

foreach ($f in 'manifest.json', 'packages-lock.json') {
    $p = Join-Path $projectPath "Packages\$f"
    if (Test-Path -LiteralPath $p) { Save-Text "project\$f" (Read-Shared $p) } else { $notes.Add("Packages/$f was not there.") }
}

# The installed package: the manifest's file: path, else the PackageCache copy, else this script's package.
$installed = $null
$manifestPath = Join-Path $projectPath 'Packages\manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $m = [regex]::Match([IO.File]::ReadAllText($manifestPath), '"com\.qhenshaw\.unitycliextensions"\s*:\s*"([^"]+)"')
    if ($m.Success) {
        $ref = $m.Groups[1].Value
        if ($ref -like 'file:*') {
            $p = $ref.Substring(5)
            if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path (Join-Path $projectPath 'Packages') $p }
            if (Test-Path -LiteralPath $p) { $installed = (Resolve-Path -LiteralPath $p).Path }
        }
    }
}
if (-not $installed) {
    $cache = Get-ChildItem -LiteralPath (Join-Path $projectPath 'Library\PackageCache') -Directory -Filter 'com.qhenshaw.unitycliextensions*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cache) { $installed = $cache.FullName }
}
$embedded = Join-Path $projectPath 'Packages\com.qhenshaw.unitycliextensions'
if (-not $installed -and (Test-Path -LiteralPath $embedded)) { $installed = $embedded }
if (-not $installed) { $installed = $scriptPackage; $notes.Add("Couldn't find the installed package; versions and skills are compared with $scriptPackage.") }

function Get-PackageVersion([string]$root) {
    $pj = Join-Path $root 'package.json'
    if (Test-Path -LiteralPath $pj) { return [regex]::Match([IO.File]::ReadAllText($pj), '"version"\s*:\s*"([^"]+)"').Groups[1].Value }
    return ''
}
function Get-LockVersion([string]$name) {
    $lock = Join-Path $projectPath 'Packages\packages-lock.json'
    if (-not (Test-Path -LiteralPath $lock)) { return '' }
    $m = [regex]::Match([IO.File]::ReadAllText($lock), '"' + [regex]::Escape($name) + '"\s*:\s*\{[^}]*?"version"\s*:\s*"([^"]+)"')
    return $m.Groups[1].Value
}
$unityVersion = ''
$pv = Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt'
if (Test-Path -LiteralPath $pv) { $unityVersion = [regex]::Match([IO.File]::ReadAllText($pv), 'm_EditorVersionWithRevision:\s*(.+)').Groups[1].Value.Trim() }
$packageVersion = Get-PackageVersion $installed
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
Save-Json 'versions.json' ([ordered]@{
    unity = $unityVersion
    pipeline = (Get-LockVersion 'com.unity.pipeline')
    package = $packageVersion
    packageSource = (Get-LockVersion 'com.qhenshaw.unitycliextensions')
    packageFolder = $installed
    reportScriptPackage = $scriptPackage
    reportScriptPackageVersion = (Get-PackageVersion $scriptPackage)
    os = $(if ($os) { "$($os.Caption) $($os.Version) ($($os.OSArchitecture))" } else { [Environment]::OSVersion.VersionString })
    powershell = $PSVersionTable.PSVersion.ToString()
    powershellEdition = "$($PSVersionTable.PSEdition)"
    clr = $PSVersionTable.CLRVersion.ToString()
    culture = (Get-Culture).Name
    generated = (Get-Date).ToUniversalTime().ToString('o')
    days = $Days
    fullTranscripts = [bool]$FullTranscripts
})

# Skills: name, stamp, and whether the installed copy differs from what the package would install.
$stampRx = '<!-- (?<skill>[A-Za-z0-9_-]+) (?<version>[^\s:]+): installed by Unity CLI Extensions'
$skills = @()
$skillsDir = Join-Path $projectPath '.claude\skills'
$sourceSkills = Join-Path $installed 'Skills~'
if (Test-Path -LiteralPath $skillsDir) {
    foreach ($dir in Get-ChildItem -LiteralPath $skillsDir -Directory) {
        $md = Join-Path $dir.FullName 'SKILL.md'
        $entry = [ordered]@{ name = $dir.Name; stamp = ''; packageVersion = $packageVersion; status = ''; editedFiles = @() }
        if (-not (Test-Path -LiteralPath $md)) { $entry.status = 'no SKILL.md'; $skills += $entry; continue }
        $text = [IO.File]::ReadAllText($md)
        $sm = [regex]::Match($text, $stampRx)
        $src = Join-Path $sourceSkills $dir.Name
        if (-not $sm.Success) { $entry.status = 'not installed by the package (no stamp)' }
        elseif (-not (Test-Path -LiteralPath $src)) { $entry.stamp = $sm.Groups['version'].Value; $entry.status = 'not in the installed package' }
        else {
            $entry.stamp = $sm.Groups['version'].Value
            $stampLine = [regex]::Match($text, '<!-- ' + [regex]::Escape($dir.Name) + ' [^\n]*?-->').Value
            $fills = @((($installed -replace '\\', '/') + '/Tools~'), (($scriptPackage -replace '\\', '/') + '/Tools~'))
            $edited = @()
            foreach ($file in Get-ChildItem -LiteralPath $src -Recurse -File) {
                $rel = $file.FullName.Substring($src.Length + 1)
                $target = Join-Path $dir.FullName $rel
                if (-not (Test-Path -LiteralPath $target)) { $edited += "$rel (missing)"; continue }
                $have = [IO.File]::ReadAllText($target) -replace "`r`n", "`n"
                $want = [IO.File]::ReadAllText($file.FullName) -replace "`r`n", "`n"
                $same = $false
                foreach ($fill in $fills) {
                    $w = $want
                    if ($rel -ieq 'SKILL.md') { $w = $want.Replace('{{cliext-tools}}', $fill).TrimEnd("`n") + "`n`n" + $stampLine + "`n" }
                    if ($have -eq $w) { $same = $true; break }
                }
                if (-not $same) { $edited += $rel }
            }
            $entry.editedFiles = $edited
            $entry.status = $(if ($entry.stamp -ne $packageVersion) { "stamp $($entry.stamp) is not the package's $packageVersion (not updated yet)" } elseif ($edited.Count -gt 0) { 'edited' } else { 'current' })
        }
        $skills += $entry
    }
}
else { $notes.Add('The project has no .claude/skills folder.') }
Save-Json 'project\skills.json' $skills

$claudeMd = Join-Path $projectPath 'CLAUDE.md'
if (Test-Path -LiteralPath $claudeMd) {
    $block = [regex]::Match([IO.File]::ReadAllText($claudeMd), '<!-- BEGIN unity-cliext.*?<!-- END unity-cliext -->', 'Singleline')
    if ($block.Success) { Save-Text 'project\claude-md-block.md' ($block.Value + "`n") } else { $notes.Add('CLAUDE.md has no unity-cliext block.') }
}
else { $notes.Add('The project has no CLAUDE.md.') }

$logDir = Join-Path $env:LOCALAPPDATA 'Unity\Editor'
foreach ($log in 'Editor.log', 'Editor-prev.log') {
    $p = Join-Path $logDir $log
    if (Test-Path -LiteralPath $p) {
        try { Save-Text "logs\$log" ((Read-Tail $p 2000) + "`n") } catch { $notes.Add("Couldn't read ${log}: $($_.Exception.Message)") }
    }
    elseif ($log -eq 'Editor.log') { $notes.Add("No Editor.log at $p.") }
}

# Full transcripts of the sessions that hold a note or a failure.
$frJson = Join-Path $frOut 'field-report.json'
if ($FullTranscripts -and (Test-Path -LiteralPath $frJson)) {
    $report = [IO.File]::ReadAllText($frJson) | ConvertFrom-Json
    $n = 0
    foreach ($t in @($report.sessionsWithContext)) {
        if (-not (Test-Path -LiteralPath $t)) { continue }
        $parent = Split-Path -Leaf (Split-Path -Parent $t)
        $session = if ($parent -eq 'subagents') { Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $t)) } else { 'sessions' }
        $dest = Join-Path $stage "transcripts\$session\$(Split-Path -Leaf $t)"
        New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $t $dest
        $n++
    }
    if ($n -eq 0) { $notes.Add('-FullTranscripts: no session in the window holds a matched note or failure, so no transcript is included.') }
}

# ---------------------------------------------------------------------------------------------------------------------
# README.txt and the zip

$describe = [ordered]@{
    'README.txt' = 'This file.'
    'versions.json' = 'Unity, Pipeline and package versions, where the package is installed from, OS and PowerShell versions.'
    'editor\editor-queries.json' = 'Whether the Editor answered within a few seconds, and its answers to cliext status, cliext.setup check and cliext.compile diagnostics.'
    'field-report\field-report.md' = 'The field report: failed tool calls by cause, workarounds, field notes and failures, with transcript excerpts around each.'
    'field-report\field-report.json' = 'The same as data.'
    'state\failures.jsonl' = 'Failed cliext commands (parameter names only, no values).'
    'state\field-notes.jsonl' = 'Notes agents recorded with cliext.failures note.'
    'state\dialogs.jsonl' = 'The dialog journal: dialogs the Editor answered and what the watchdog saw, pressed or escalated.'
    'state\watchdog-status.json' = "The watchdog's last snapshot of the Editor."
    'state\watchdog-policy.json' = 'The rules the watchdog answers dialogs with.'
    'state\heartbeat.json' = "The Editor's last heartbeat (a stale one means its main thread was blocked)."
    'state\compile.json' = 'Each assembly''s errors and warnings from its last compile.'
    'project\manifest.json' = 'Packages/manifest.json.'
    'project\packages-lock.json' = 'Packages/packages-lock.json.'
    'project\skills.json' = 'Each installed skill: its version stamp and whether it was edited.'
    'project\claude-md-block.md' = 'The unity-cliext block of CLAUDE.md.'
    'logs\Editor.log' = 'The last 2000 lines of the Unity Editor log on this machine (it can be from another project).'
    'logs\Editor-prev.log' = 'The last 2000 lines of the previous Editor log.'
}
$files = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1) } | Sort-Object
$lines = New-Object Collections.Generic.List[string]
$lines.Add("Unity CLI Extensions bug report")
$lines.Add("Project: $projectPath")
$lines.Add("Made: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')), covering the last $Days days.")
$lines.Add("Editor: $(if ($editor.answered) { 'answered' } elseif ($editor.queried) { "did not answer ($($editor.reason))" } else { $editor.reason })")
$lines.Add('')
$lines.Add('HOW TO SEND IT')
$lines.Add('Nothing has been uploaded. Look through this zip first, then attach it to a new issue at')
$lines.Add('https://github.com/qhenshaw/UnityCLIExtensions/issues and say in a sentence or two what went wrong.')
$lines.Add('')
$lines.Add('LOOK THROUGH IT FIRST')
$lines.Add('The transcript excerpts and logs can contain your project''s code, file and folder paths, and the prompts you')
$lines.Add('gave the agent. Open field-report\field-report.md and the logs, and remove anything you don''t want to share')
$lines.Add('(delete the file from the zip, or rerun with a smaller -Days). transcripts\ is only there with -FullTranscripts.')
$lines.Add('')
$lines.Add('FILES')
foreach ($f in @($files) + @('README.txt')) {
    $d = $describe[$f]
    if (-not $d -and $f -like 'transcripts\*') { $d = 'A full agent transcript (-FullTranscripts).' }
    $lines.Add("  $f" + $(if ($d) { ": $d" } else { '' }))
}
if ($notes.Count -gt 0) { $lines.Add(''); $lines.Add('NOT INCLUDED OR CUT'); foreach ($n in $notes) { $lines.Add("  - $n") } }
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), (($lines -join "`r`n") + "`r`n"), $utf8)

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force (Split-Path -Parent $Zip) | Out-Null
$partial = "$Zip.partial"
if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $partial, [IO.Compression.CompressionLevel]::Optimal, $false)
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
Move-Item -LiteralPath $partial $Zip    # the zip appears only when complete
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

$size = (Get-Item -LiteralPath $Zip).Length
Write-Output $Zip
Write-Output ("{0:N1} MB. Look through it, then attach it to an issue at https://github.com/qhenshaw/UnityCLIExtensions/issues." -f ($size / 1MB))
if (-not $NoExplorer) { Start-Process explorer.exe -ArgumentList "/select,`"$Zip`"" }
exit 0

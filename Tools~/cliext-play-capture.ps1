<#
.SYNOPSIS
Puts the game in a state and screenshots it, in one call: compile, enter Play, optionally slow time down, wait until
a snippet says "ready", run the project's own commands, then capture the game view at timed moments and after setup
snippets. Optionally crops and tiles the shots into one contact sheet. Leaves Play mode afterwards (unless -Stay).
Nothing is written into the project.

.DESCRIPTION
-TimeScale Set Time.timeScale once Play mode has started, before -Until and -Run (cliext.playmode time_scale), so the
           effect you trigger plays in slow motion: 0.005 makes a 0.1 s muzzle flash last 20 s. The previous time
           scale is put back at the end, also with -Stay and when a step fails.
-Until     A C# snippet run every second (cliext.playmode wait_until) until it returns "ready". It may also push the
           game forward each time (start the run, end the wave, pick level-ups) and return the state it is in
           otherwise, which keeps "how to get there" in one file.
-Run       unity command lines run in order after -Until, such as the project's own debug menu commands:
           -Run "debugmenu do --section Effects --label 'Preview gun looks'". Quote arguments with spaces in single or
           double quotes. A leading "unity command" is optional. A step that exits non-zero fails the capture.
Shots      One argument each after the named parameters, in either form:
           NAME@SECONDS   captured that many real seconds after the last -Run step (or after -Until, or once Play
                          mode started). Taken first, in time order; the JSON reports when each was really taken.
           NAME=FILE or NAME=FILE|key=value;key=value   FILE is run with cliext.eval run, its {{key}} placeholders
                          filled from the pairs, then the game view is saved. Taken after the timed shots, in order.
           Each shot is saved as <Out>/NAME.png.
-Crop      x,y,w,h in capture pixels, applied to every shot before tiling (e.g. one UI panel).
-Sheet     Tile the (cropped) shots left to right into one PNG with their names underneath.
-Settle    Seconds to wait after a setup snippet before capturing (layout, animations). Default 0.5.
-Scene     Open this scene first. Its current scene must have no unsaved changes.

Exit codes: 0 every shot captured, 1 a step failed (compile errors, Play mode didn't start, not ready in time, a
-Run step, setup snippet or capture failed), 2 bad arguments. The JSON on stdout lists the shots and the sheet.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-play-capture.ps1 -Project E:/Games/MyGame `
  -Scene Assets/Scenes/Game.unity -Until Snippets/to-shop.cs `
  -Crop 455,30,450,1030 -Sheet C:/Temp/tooltips.png `
  'wand=Snippets/show-item.cs|id=Pistol' 'totem=Snippets/show-item.cs|id=BloodiedTotem'

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-play-capture.ps1 -Project E:/Games/MyGame `
  -Scene Assets/Scenes/Arena.unity -TimeScale 0.005 `
  -Run "debugmenu do --section Effects --label 'Preview gun looks'" -Sheet C:/Temp/guns.png a@0 b@1.5 c@3
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Scene,
    [string]$Until,
    [double]$Timeout = 90,
    [string]$Out,
    [string]$Crop,
    [string]$Sheet,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [double]$Settle = 0.5,
    [switch]$NoRecompile,
    [switch]$Stay,
    [string[]]$Run,
    [double]$TimeScale = -1,
    # The shots: NAME@SECONDS, NAME=FILE or NAME=FILE|key=value;key=value, one argument each, after the named parameters.
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Shot
)

if (-not $Shot -or $Shot.Count -eq 0) { Write-Error 'Give at least one shot after the named parameters: NAME@SECONDS or NAME=FILE[|key=value;...].'; exit 2 }
$timedPattern = '^(?<name>[^=@|]+)@(?<at>\d+(\.\d+)?)$'
foreach ($spec in $Shot) {
    if ($spec -notmatch $timedPattern -and $spec.IndexOf('=') -le 0) { Write-Error "Shot '$spec' is neither NAME@SECONDS nor NAME=FILE."; exit 2 }
}

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path $Project).Path.TrimEnd('\')
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) ("play-capture-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$waitScript = Join-Path $PSScriptRoot 'cliext-wait.ps1'
$transient = 'closed the connection|No Pipeline instance|Bad Request|isn.t reachable|not reachable|ECONNREFUSED|ECONNRESET'

function Invoke-Unity([string[]]$arguments, [int]$tries = 6) {
    $ErrorActionPreference = 'Continue'
    for ($i = 1; $i -le $tries; $i++) {
        $output = (& unity command --project-path $projectPath @arguments --format json 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $code = $LASTEXITCODE
        if ($output -notmatch $transient -or $i -eq $tries) { break }
        Start-Sleep -Seconds 3
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $output }
}

function Wait-Idle([double]$seconds = 180) {
    $w = (& powershell -NoProfile -ExecutionPolicy Bypass -File $waitScript -Project $projectPath -Timeout $seconds 2>&1 | Out-String)
    try { return ($w | ConvertFrom-Json) } catch { return $null }
}

# NAME=FILE|k=v;k=v  ->  name, file, vars
function Split-Spec([string]$spec, [bool]$named) {
    $name = $null
    if ($named) {
        $eq = $spec.IndexOf('=')
        if ($eq -le 0) { throw "-Shot '$spec' isn't NAME=FILE." }
        $name = $spec.Substring(0, $eq); $spec = $spec.Substring($eq + 1)
    }
    $bar = $spec.IndexOf('|')
    $file = $(if ($bar -ge 0) { $spec.Substring(0, $bar) } else { $spec })
    $vars = $(if ($bar -ge 0) { $spec.Substring($bar + 1) } else { '' })
    if (-not [IO.Path]::IsPathRooted($file)) { $file = Join-Path (Get-Location).Path $file }
    return [pscustomobject]@{ Name = $name; File = $file; Vars = $vars }
}

# --vars only when there are some: Windows PowerShell 5.1 drops an empty argument on the way to a native program,
# which would leave --vars without a value.
function VarsArgs([string]$vars) {
    if ([string]::IsNullOrWhiteSpace($vars)) { return @() }
    return @('--vars', $vars)
}

# A command line as typed in a shell -> its arguments. Single and double quotes group words; a leading
# "unity command" is dropped.
function Split-CommandLine([string]$line) {
    $parts = New-Object System.Collections.Generic.List[string]
    $current = New-Object System.Text.StringBuilder
    $quote = [char]0
    $quoted = $false
    foreach ($ch in $line.ToCharArray()) {
        if ($quote -ne [char]0) {
            if ($ch -eq $quote) { $quote = [char]0 } else { [void]$current.Append($ch) }
            continue
        }
        if ($ch -eq [char]'"' -or $ch -eq [char]"'") { $quote = $ch; $quoted = $true; continue }
        if ([char]::IsWhiteSpace($ch)) {
            if ($current.Length -gt 0 -or $quoted) { $parts.Add($current.ToString()); [void]$current.Clear(); $quoted = $false }
            continue
        }
        [void]$current.Append($ch)
    }
    if ($quote -ne [char]0) { throw "Unclosed quote in -Run '$line'." }
    if ($current.Length -gt 0 -or $quoted) { $parts.Add($current.ToString()) }
    if ($parts.Count -ge 2 -and $parts[0] -eq 'unity' -and $parts[1] -eq 'command') { $parts.RemoveRange(0, 2) }
    return , $parts.ToArray()
}

$script:timeScaleBefore = $null
function Restore-TimeScale {
    if ($null -eq $script:timeScaleBefore) { return }
    Invoke-Unity @('cliext.playmode', 'time_scale', '--value', ([string]$script:timeScaleBefore)) 2 | Out-Null
    $script:timeScaleBefore = $null
}

function Fail([string]$step, [string]$detail) {
    [ordered]@{ result = 'failed'; step = $step; detail = $detail; out = $Out } | ConvertTo-Json -Depth 4
    Restore-TimeScale
    if (-not $Stay) { Invoke-Unity @('editor.playmode', 'stop') | Out-Null }
    exit 1
}

# 1. Fresh scripts. compile run --stop-play leaves Play mode first (nothing compiles while playing).
if (-not $NoRecompile) {
    Invoke-Unity @('cliext.compile', 'run', '--stop-play', 'true', '--async', 'true') | Out-Null
    Start-Sleep -Seconds 1
    Wait-Idle | Out-Null
    $diag = Invoke-Unity @('cliext.compile', 'diagnostics', '--warnings', 'false')
    $errors = @($diag.Output -split "`n" | Where-Object { $_ -match '"severity":"error"' })
    if ($errors.Count -gt 0) { Fail 'compile' ($errors -join "`n") }
}

# 2. Scene and Play mode (Run In Background on, so an unfocused Editor keeps ticking).
if ($Scene) {
    $r = Invoke-Unity @('scene', 'open', '--path', $Scene)
    if ($r.ExitCode -ne 0) { Fail 'scene' $r.Output }
}
$r = Invoke-Unity @('editor.playmode', 'start', '--run-in-background', 'true')
if ($r.ExitCode -ne 0 -and $r.Output -notmatch $transient) { Fail 'play' $r.Output }
$playing = $false
for ($i = 0; $i -lt 30 -and -not $playing; $i++) {
    $state = Wait-Idle 60
    if ($state -and $state.result -eq 'blocked-by-dialog') { Fail 'play' "A dialog blocks the Editor: $(@($state.dialogs | ForEach-Object { $_.title }) -join ', ')" }
    $playing = $state -and $state.playMode -eq 'playing'
    if (-not $playing) { Start-Sleep -Seconds 1 }
}
if (-not $playing) { Fail 'play' 'Play mode did not start.' }

# 3. Slow motion, before anything is triggered.
if ($TimeScale -ge 0) {
    $r = Invoke-Unity @('cliext.playmode', 'time_scale', '--value', ([string]$TimeScale)) 2
    if ($r.ExitCode -ne 0) { Fail 'time scale' $r.Output }
    try { $script:timeScaleBefore = ($r.Output.Substring($r.Output.IndexOf('{')) | ConvertFrom-Json).previous } catch { $script:timeScaleBefore = 1 }
}

# 4. Wait for the state the shots need.
$readyResult = $null
if ($Until) {
    $u = Split-Spec $Until $false
    $r = Invoke-Unity (@('cliext.playmode', 'wait_until', '--file', $u.File, '--timeout', [string]$Timeout) + (VarsArgs $u.Vars)) 1
    if ($r.ExitCode -ne 0) { Fail 'until' $r.Output }
    $readyResult = $r.Output.Trim()
}

# 5. The project's own commands, in order. Run once each: a retry could trigger an effect twice.
$runs = @()
foreach ($line in @($Run | Where-Object { $_ -and $_.Trim() })) {
    try { $commandArgs = Split-CommandLine $line } catch { Fail 'run' $_.Exception.Message }
    $r = Invoke-Unity $commandArgs 1
    if ($r.ExitCode -ne 0) { Fail "run '$line'" $r.Output }
    $runs += [ordered]@{ command = $line; output = $r.Output.Trim() }
}
$clock = [Diagnostics.Stopwatch]::StartNew()

# 6. Timed shots, in time order, measured from the end of the -Run steps.
$shots = @()
$timed = @($Shot | Where-Object { $_ -match $timedPattern } | ForEach-Object {
    $null = $_ -match $timedPattern
    [pscustomobject]@{ Name = $Matches['name']; At = [double]::Parse($Matches['at'], [Globalization.CultureInfo]::InvariantCulture) }
} | Sort-Object At)
foreach ($t in $timed) {
    $waitMs = [int](($t.At - $clock.Elapsed.TotalSeconds) * 1000)
    if ($waitMs -gt 0) { Start-Sleep -Milliseconds $waitMs }
    $takenAt = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
    $png = Join-Path $Out "$($t.Name).png"
    $cap = Invoke-Unity @('capture', 'game_view', '--output-path', $png, '--width', [string]$Width, '--height', [string]$Height)
    if (-not (Test-Path $png)) { Fail "capture $($t.Name)" $cap.Output }
    $shots += [ordered]@{ name = $t.Name; png = $png; at = $takenAt; late = [Math]::Round($takenAt - $t.At, 2) }
}

# 7. Snippet shots, in the order given.
foreach ($spec in @($Shot | Where-Object { $_ -notmatch $timedPattern })) {
    $s = Split-Spec $spec $true
    $setup = Invoke-Unity (@('cliext.eval', 'run', '--file', $s.File) + (VarsArgs $s.Vars))
    if ($setup.ExitCode -ne 0) { Fail "shot $($s.Name)" $setup.Output }
    Start-Sleep -Milliseconds ([int]($Settle * 1000))
    $png = Join-Path $Out "$($s.Name).png"
    $cap = Invoke-Unity @('capture', 'game_view', '--output-path', $png, '--width', [string]$Width, '--height', [string]$Height)
    if (-not (Test-Path $png)) { Fail "capture $($s.Name)" $cap.Output }
    $shots += [ordered]@{ name = $s.Name; png = $png; setup = $setup.Output.Trim() }
}

# 8. Normal speed again, then leave Play mode.
Restore-TimeScale
if (-not $Stay) {
    Invoke-Unity @('editor.playmode', 'stop') | Out-Null
    Wait-Idle 60 | Out-Null
}

# 9. Contact sheet (System.Drawing, no Python needed).
$sheetPath = $null
if ($Sheet -and $shots.Count -gt 0) {
    Add-Type -AssemblyName System.Drawing
    $rect = $null
    if ($Crop) { $c = @($Crop.Split(',') | ForEach-Object { [int]$_.Trim() }); $rect = New-Object Drawing.Rectangle $c[0], $c[1], $c[2], $c[3] }
    $images = @()
    foreach ($s in $shots) {
        $img = [Drawing.Image]::FromFile($s.png)
        if ($rect) {
            $r2 = [Drawing.Rectangle]::Intersect($rect, (New-Object Drawing.Rectangle 0, 0, $img.Width, $img.Height))
            $cropped = New-Object Drawing.Bitmap $r2.Width, $r2.Height
            $g = [Drawing.Graphics]::FromImage($cropped); $g.DrawImage($img, (New-Object Drawing.Rectangle 0, 0, $r2.Width, $r2.Height), $r2, [Drawing.GraphicsUnit]::Pixel); $g.Dispose()
            $img.Dispose(); $img = $cropped
        }
        $images += [pscustomobject]@{ Name = $s.name; Image = $img }
    }
    $gap = 10; $label = 24
    $w = ($images | ForEach-Object { $_.Image.Width } | Measure-Object -Sum).Sum + $gap * ($images.Count - 1)
    $h = ($images | ForEach-Object { $_.Image.Height } | Measure-Object -Maximum).Maximum + $label
    $canvas = New-Object Drawing.Bitmap ([int]$w), ([int]$h)
    $g = [Drawing.Graphics]::FromImage($canvas)
    $g.Clear([Drawing.Color]::FromArgb(20, 20, 24))
    $font = New-Object Drawing.Font 'Segoe UI', 11
    $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(200, 200, 210))
    $x = 0
    foreach ($i in $images) {
        $g.DrawImage($i.Image, $x, 0, $i.Image.Width, $i.Image.Height)
        $g.DrawString($i.Name, $font, $brush, [single]($x + 4), [single]($i.Image.Height + 3))
        $x += $i.Image.Width + $gap
        $i.Image.Dispose()
    }
    $g.Dispose()
    $sheetPath = [IO.Path]::GetFullPath($Sheet)
    New-Item -ItemType Directory -Force (Split-Path $sheetPath) | Out-Null
    $canvas.Save($sheetPath, [Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}

[ordered]@{ result = 'captured'; out = $Out; shots = $shots; sheet = $sheetPath; until = $readyResult; runs = $runs; timeScale = $(if ($TimeScale -ge 0) { $TimeScale } else { $null }) } | ConvertTo-Json -Depth 4
exit 0

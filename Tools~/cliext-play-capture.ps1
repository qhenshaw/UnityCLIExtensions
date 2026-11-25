<#
.SYNOPSIS
Puts the game in a state and screenshots it, in one call: compile, enter Play, wait until a snippet says "ready",
then for each shot run its setup snippet and capture the game view. Optionally crops and tiles the shots into one
contact sheet. Leaves Play mode afterwards (unless -Stay). Nothing is written into the project.

.DESCRIPTION
-Until     A C# snippet run every second (cliext.playmode wait_until) until it returns "ready". It may also push the
           game forward each time (start the run, end the wave, pick level-ups) and return the state it is in
           otherwise, which keeps "how to get there" in one file.
Shots      NAME=FILE or NAME=FILE|key=value;key=value, one argument each after the named parameters. FILE is run
           with cliext.eval run, its {{key}} placeholders filled from the pairs, then the game view is saved as
           <Out>/NAME.png.
-Crop      x,y,w,h in capture pixels, applied to every shot before tiling (e.g. one UI panel).
-Sheet     Tile the (cropped) shots left to right into one PNG with their names underneath.
-Settle    Seconds to wait after a setup snippet before capturing (layout, animations). Default 0.5.
-Scene     Open this scene first. Its current scene must have no unsaved changes.

Exit codes: 0 every shot captured, 1 a step failed (compile errors, Play mode didn't start, not ready in time, a
setup snippet or capture failed). The JSON on stdout lists the shots and the sheet.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-play-capture.ps1 -Project E:/Games/MyGame `
  -Scene Assets/Scenes/Game.unity -Until Snippets/to-shop.cs `
  -Crop 455,30,450,1030 -Sheet C:/Temp/tooltips.png `
  'wand=Snippets/show-item.cs|id=Pistol' 'totem=Snippets/show-item.cs|id=BloodiedTotem'
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
    # The shots: NAME=FILE or NAME=FILE|key=value;key=value, one argument each, after the named parameters.
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Shot
)

if (-not $Shot -or $Shot.Count -eq 0) { Write-Error 'Give at least one shot: NAME=FILE[|key=value;...] after the named parameters.'; exit 2 }

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

function Fail([string]$step, [string]$detail) {
    [ordered]@{ result = 'failed'; step = $step; detail = $detail; out = $Out } | ConvertTo-Json -Depth 4
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

# 3. Wait for the state the shots need.
$readyResult = $null
if ($Until) {
    $u = Split-Spec $Until $false
    $r = Invoke-Unity @('cliext.playmode', 'wait_until', '--file', $u.File, '--vars', $u.Vars, '--timeout', [string]$Timeout) 1
    if ($r.ExitCode -ne 0) { Fail 'until' $r.Output }
    $readyResult = $r.Output.Trim()
}

# 4. Shots.
$shots = @()
foreach ($spec in $Shot) {
    $s = Split-Spec $spec $true
    $setup = Invoke-Unity @('cliext.eval', 'run', '--file', $s.File, '--vars', $s.Vars)
    if ($setup.ExitCode -ne 0) { Fail "shot $($s.Name)" $setup.Output }
    Start-Sleep -Milliseconds ([int]($Settle * 1000))
    $png = Join-Path $Out "$($s.Name).png"
    $cap = Invoke-Unity @('capture', 'game_view', '--output-path', $png, '--width', [string]$Width, '--height', [string]$Height)
    if (-not (Test-Path $png)) { Fail "capture $($s.Name)" $cap.Output }
    $shots += [ordered]@{ name = $s.Name; png = $png; setup = $setup.Output.Trim() }
}

# 5. Leave Play mode.
if (-not $Stay) {
    Invoke-Unity @('editor.playmode', 'stop') | Out-Null
    Wait-Idle 60 | Out-Null
}

# 6. Contact sheet (System.Drawing, no Python needed).
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

[ordered]@{ result = 'captured'; out = $Out; shots = $shots; sheet = $sheetPath; until = $readyResult } | ConvertTo-Json -Depth 4
exit 0

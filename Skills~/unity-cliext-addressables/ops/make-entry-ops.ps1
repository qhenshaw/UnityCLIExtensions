# Writes a cliext.addressables ops file: an optional add_group, then one add_entry per asset file under -Folder
# (subfolders included) whose extension is in -Extensions, each in -Group with -Labels.
# Run from the project folder:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/unity-cliext-addressables/ops/make-entry-ops.ps1 -Folder Assets/<folder> -Group <Group> `
#     -Labels <label> -Extensions png,tga,jpg -NewGroup -Out Temp/addr-ops.json
# Then: unity command cliext.addressables edit --ops-file Temp/addr-ops.json --format json
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][string]$Group,
    [string]$Labels = '',
    [Parameter(Mandatory = $true)][string[]]$Extensions,
    [switch]$NewGroup,
    [string]$GroupSet = '',
    [string]$Out = 'Temp/addr-ops.json'
)
$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
if (-not (Test-Path (Join-Path $root $Folder) -PathType Container)) { throw "No folder '$Folder' under $root (run from the project folder)." }
$exts = $Extensions | ForEach-Object { $_ -split ',' } | ForEach-Object { '.' + $_.Trim().TrimStart('.').ToLowerInvariant() } | Where-Object { $_ -ne '.' }
$files = Get-ChildItem -Path (Join-Path $root $Folder) -Recurse -File |
    Where-Object { $exts -contains $_.Extension.ToLowerInvariant() } |
    Sort-Object FullName
$ops = @()
if ($NewGroup) {
    $g = [ordered]@{ op = 'add_group'; name = $Group }
    if ($GroupSet) { $g.set = $GroupSet }
    $ops += $g
}
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
    $e = [ordered]@{ op = 'add_entry'; path = $rel; group = $Group }
    if ($Labels) { $e.labels = $Labels }
    $ops += $e
}
$dir = Split-Path -Parent (Join-Path $root $Out)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
# -InputObject keeps a one-element list an array in Windows PowerShell 5.1.
ConvertTo-Json -InputObject $ops -Depth 3 | Out-File -Encoding utf8 (Join-Path $root $Out)
"$($files.Count) asset(s), $($ops.Count) op(s) -> $Out"

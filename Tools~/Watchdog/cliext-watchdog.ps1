<#
.SYNOPSIS
Out-of-process watchdog for a Unity Editor driven from the command line (Windows).

.DESCRIPTION
A modal dialog blocks the Unity Editor's main thread, so every `unity command` waits behind it and nothing inside
the Editor can answer it. This script runs outside the Editor. It finds the Editor's dialogs with Win32, reports their
title, message and buttons, presses the safe default for the dialogs its policy knows, and leaves the rest for the
agent (escalates). It also notices a hung window and a crash, and can reopen the project.

Modes:
  -Status            Print one snapshot as JSON and exit (default when -Watch, -Click and -Close are absent).
  -Click <label>     Press the button with that label on the Editor's dialog, then print a snapshot.
  -Close             Close (cancel) the Editor's dialog, then print a snapshot.
  -Watch             Keep checking every few seconds, writing Library/CLIExtensions/watchdog-status.json and
                     appending to Library/CLIExtensions/dialogs.jsonl. The Editor starts this in agent mode.
  -Detach            With -Watch: start the watchdog as a separate, hidden process (outside the Editor's process
                     tree, so it survives an Editor crash) and return at once.

-Project takes the project folder's path, or its folder name when the Editor is already open. It is required with
-Watch, and with -Click or -Close when several Editors are open.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-watchdog.ps1 -Project E:/Games/MyGame
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-watchdog.ps1 -Project MyGame -Click "Reload"
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-watchdog.ps1 -Project E:/Games/MyGame -Watch -Detach
#>
param(
    [string]$Project,
    [int]$EditorPid = 0,
    [switch]$Status,
    [string]$Click,
    [string]$Title,
    [switch]$Close,
    [switch]$Watch,
    [switch]$Detach,
    [string]$PolicyPath,
    [double]$IntervalSeconds = 0,
    [switch]$NoAutoClick
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class CliExtWin
{
    public IntPtr Handle;
    public string Title;
    public string ClassName;
    public bool Enabled;
    public IntPtr Owner;
}

public class CliExtControl
{
    public IntPtr Handle;
    public string Text;
    public string Kind;   // button, checkbox, text
    public int Id;
}

public static class CliExtNative
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    const uint GW_OWNER = 4;
    const int GWL_STYLE = -16;
    const uint WM_NULL = 0, WM_CLOSE = 0x10, WM_COMMAND = 0x111;
    const uint SMTO_ABORTIFHUNG = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeoutText(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    const uint WM_GETTEXT = 0x0D;

    static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, 1024); return s.ToString(); }

    // GetWindowText only reads captions across processes; a control's own text (the message in an Edit) needs WM_GETTEXT.
    static string ControlText(IntPtr h)
    {
        var s = new StringBuilder(8192);
        IntPtr r;
        if (SendMessageTimeoutText(h, WM_GETTEXT, (IntPtr)s.Capacity, s, SMTO_ABORTIFHUNG, 1000, out r) == IntPtr.Zero) return Text(h);
        return s.ToString();
    }
    static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }

    public static List<CliExtWin> TopLevelWindows(uint pid)
    {
        var list = new List<CliExtWin>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h))
                list.Add(new CliExtWin { Handle = h, Title = Text(h), ClassName = Class(h), Enabled = IsWindowEnabled(h), Owner = GetWindow(h, GW_OWNER) });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Direct children only: a dialog's buttons and labels, not the controls of an embedded file browser.
    public static List<CliExtControl> Controls(IntPtr dialog)
    {
        var list = new List<CliExtControl>();
        EnumChildWindows(dialog, delegate(IntPtr h, IntPtr l)
        {
            if (GetParent(h) != dialog || !IsWindowVisible(h)) return true;
            string cls = Class(h);
            string text = ControlText(h);
            if (cls == "Button")
            {
                int type = GetWindowLong(h, GWL_STYLE) & 0xF;
                string kind = (type == 0 || type == 1) ? "button" : (type == 2 || type == 3 || type == 5 || type == 6) ? "checkbox" : (type == 7 ? "group" : "button");
                list.Add(new CliExtControl { Handle = h, Text = text, Kind = kind, Id = GetDlgCtrlID(h) });
            }
            else if ((cls == "Static" || cls == "Edit" || cls.StartsWith("RichEdit")) && text.Trim().Length > 0)
            {
                list.Add(new CliExtControl { Handle = h, Text = text, Kind = "text", Id = GetDlgCtrlID(h) });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Presses a button the way the dialog's own message loop expects (WM_COMMAND/BN_CLICKED to the dialog). Posted,
    // so the watchdog never waits on the Editor, and it works whether or not the dialog has focus.
    public static bool PressButton(IntPtr dialog, IntPtr button)
    {
        if (!IsWindow(dialog) || !IsWindow(button)) return false;
        int id = GetDlgCtrlID(button);
        return PostMessage(dialog, WM_COMMAND, (IntPtr)(id & 0xFFFF), button);
    }

    public static bool CloseWindow(IntPtr h)
    {
        return IsWindow(h) && PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // True when the window's thread pumps messages within the timeout. A modal dialog pumps them, so the Editor answers
    // even while blocked; a busy main thread (long import, infinite loop) does not.
    public static bool Responds(IntPtr h, uint timeoutMs)
    {
        if (IsHungAppWindow(h)) return false;
        IntPtr result;
        return SendMessageTimeout(h, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out result) != IntPtr.Zero;
    }

    // Appends a line, sharing the file with the Editor, retrying briefly when the Editor is writing.
    public static void AppendLine(string path, string line)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    w.Write(line + "\n");
                return;
            }
            catch (IOException) { Thread.Sleep(20); }
        }
    }

    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return;
            }
            catch (IOException) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) { Thread.Sleep(20); }
        }
    }
}
"@

# ---------------------------------------------------------------------------------------------------------------
# Finding the Editor

function Normalize-Path([string]$p) { if (-not $p) { return $null }; return $p.Replace('/', '\').TrimEnd('\') }

# The Editor's main window title starts with the project path: "E:\Games\MyGame - Scene - Windows, Mac, Linux - Unity 6.5 (...)".
# In Safe Mode it is "E:\Games\MyGame - SAFE MODE - 6000.5.8f1 <DX12>".
function Get-ProjectFromTitle([string]$t) {
    if ($t -match '^(.+?) - .* - Unity \d') { return $Matches[1] }
    if ($t -match '^(.+?) - SAFE MODE - ') { return $Matches[1] }
    return $null
}

# The -projectPath argument of an Editor's command line. Before the main window exists (a startup dialog such as
# Safe Mode or an upgrade prompt) this is the only way to tell which project an Editor has open.
function Get-ProjectFromCommandLine([string]$cmd) {
    if (-not $cmd) { return $null }
    if ($cmd -match '(?i)-projectpath\s+"([^"]+)"') { return $Matches[1] }
    if ($cmd -match '(?i)-projectpath\s+(\S+)') { return $Matches[1] }
    return $null
}

function Get-Editors {
    $result = @()
    $commandLines = @{}
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)) { $commandLines[[int]$p.ProcessId] = $p.CommandLine }
    } catch { }
    foreach ($proc in @(Get-Process Unity -ErrorAction SilentlyContinue)) {
        $cmd = $commandLines[[int]$proc.Id]
        if ($cmd -match '(?i)-batchmode') { continue }   # asset import workers and batch Editors have no dialogs to answer
        $windows = [CliExtNative]::TopLevelWindows([uint32]$proc.Id)
        $main = $null
        foreach ($w in $windows) { if (Get-ProjectFromTitle $w.Title) { $main = $w; break } }
        $project = $(if ($main) { Get-ProjectFromTitle $main.Title } else { Get-ProjectFromCommandLine $cmd })
        if (-not $project -and $windows.Count -eq 0) { continue }
        $result += [pscustomobject]@{ Pid = $proc.Id; Process = $proc; Main = $main; Project = $project; Windows = $windows }
    }
    return $result
}

function Test-ProjectMatch($editorProject, [string]$wanted) {
    if (-not $wanted) { return $true }
    $want = Normalize-Path $wanted
    $have = Normalize-Path $editorProject
    if (-not $have) { return $false }
    return ($have -ieq $want) -or ((Split-Path $have -Leaf) -ieq (Split-Path $want -Leaf))
}

function Find-Editor {
    $editors = @(Get-Editors)
    if ($EditorPid -gt 0) { return @($editors | Where-Object { $_.Pid -eq $EditorPid }) | Select-Object -First 1 }
    $matching = @($editors | Where-Object { Test-ProjectMatch $_.Project $Project })
    if ($matching.Count -gt 1 -and -not $Project) {
        throw ("Several Unity Editors are open; pass -Project <path or name>: " + (($matching | ForEach-Object { "$($_.Pid) $($_.Project)" }) -join '; '))
    }
    return $matching | Select-Object -First 1
}

# ---------------------------------------------------------------------------------------------------------------
# Dialogs

# Button text with its mnemonic markers removed ("&&" is a literal "&"), runs of spaces collapsed.
function Get-Label([string]$text) { return ((($text -replace '&&', '<amp>') -replace '&', '' -replace '<amp>', '&') -replace '\s+', ' ').Trim() }

function Get-Dialogs($editor) {
    $dialogs = @()
    $windows = [CliExtNative]::TopLevelWindows([uint32]$editor.Pid)
    foreach ($w in $windows) {
        if ($w.Handle -eq $editor.Main.Handle) { continue }
        $controls = [CliExtNative]::Controls($w.Handle)
        $buttons = @($controls | Where-Object { $_.Kind -eq 'button' })
        if ($w.ClassName -eq '#32770' -or $buttons.Count -gt 0) {
            $dialogs += [pscustomobject]@{
                kind      = 'native'
                hwnd      = [int64]$w.Handle
                title     = $w.Title
                message   = (@($controls | Where-Object { $_.Kind -eq 'text' } | ForEach-Object { $_.Text }) -join "`n")
                buttons   = @($buttons | ForEach-Object { Get-Label $_.Text })
                checkboxes = @($controls | Where-Object { $_.Kind -eq 'checkbox' } | ForEach-Object { Get-Label $_.Text })
                controls  = $controls
            }
        }
        elseif ($editor.Main -and -not $editor.Main.Enabled -and $w.Owner -ne [IntPtr]::Zero) {
            # An EditorWindow.ShowModal window: drawn by Unity, so it has no Win32 buttons to press, only a close.
            $dialogs += [pscustomobject]@{ kind = 'unity-modal'; hwnd = [int64]$w.Handle; title = $w.Title; message = ''; buttons = @(); checkboxes = @(); controls = @() }
        }
    }
    return $dialogs
}

function Press-Button($dialog, [string]$label) {
    $want = (Get-Label $label) -replace '\s+', ' '
    foreach ($c in $dialog.controls) {
        if ($c.Kind -ne 'button') { continue }
        $have = (Get-Label $c.Text) -replace '\s+', ' '
        if ($have -ieq $want) { return [CliExtNative]::PressButton([IntPtr]$dialog.hwnd, $c.Handle) }
    }
    return $false
}

function Find-ButtonByPattern($dialog, [string]$pattern) {
    foreach ($b in $dialog.buttons) { if ($b -match $pattern) { return $b } }
    return $null
}

# ---------------------------------------------------------------------------------------------------------------
# Policy

function Get-DefaultPolicy {
    return [pscustomobject]@{
        autoClick = $true; restartOnCrash = $false; killAfterHungSeconds = 0; intervalSeconds = 2; staleHeartbeatSeconds = 20
        relaunchArgs = '-automated -accept-apiupdate -skipUpgradeDialogs'
        rules = @(
            [pscustomobject]@{ name = 'busy'; title = '^(Hold on|Opening project\.\.\.|Loading|Initializing|Compiling|Importing|Reloading|Progress|Unity is busy|Building|Baking)'; action = 'wait' }
            [pscustomobject]@{ name = 'scene-modified-externally'; title = 'modified externally|changed on disk|have been changed externally'; action = 'click'; button = '^Reload' }
            [pscustomobject]@{ name = 'scene-save-prompt'; title = '^Scene\(s\) Have Been Modified|^Save Scene|Unsaved Changes'; action = 'escalate' }
            [pscustomobject]@{ name = 'input-system-backends'; title = 'Input System native platform backend'; action = 'click'; button = "^Don't Enable" }
            [pscustomobject]@{ name = 'safe-mode'; title = 'Enter Safe Mode'; action = 'click'; button = '^Ignore' }
            [pscustomobject]@{ name = 'file-picker'; title = '^(Open|Open File|Save|Save As|Save File|Select Folder|Choose Folder|Browse For Folder)$'; action = 'close' }
        )
        # The Editor writes the full policy (WatchdogPolicy.DefaultRules) to Library/CLIExtensions/watchdog-policy.json;
        # these defaults only cover the startup dialogs that come up before the package's code has run.
    }
}

function Get-Policy([string]$projectPath) {
    $path = $PolicyPath
    if (-not $path -and $projectPath) { $path = Join-Path $projectPath 'Library\CLIExtensions\watchdog-policy.json' }
    $policy = Get-DefaultPolicy
    if ($path -and (Test-Path $path)) {
        try { $policy = Get-Content $path -Raw | ConvertFrom-Json } catch { Write-Warning "Bad policy file $path, using the defaults: $_" }
    }
    if ($NoAutoClick) { $policy.autoClick = $false }
    if ($IntervalSeconds -gt 0) { $policy.intervalSeconds = $IntervalSeconds }
    return $policy
}

function Find-Rule($policy, $dialog) {
    foreach ($rule in $policy.rules) {
        if ($rule.title -and $dialog.title -notmatch $rule.title) { continue }
        if ($rule.message -and $dialog.message -notmatch $rule.message) { continue }
        if ($rule.kind -and $dialog.kind -ne $rule.kind) { continue }
        return $rule
    }
    # A native window with no buttons is a progress window (Unity's titles vary: "Reloading Domain", "Hold on...").
    # There is nothing to press, and it closes by itself.
    if ($dialog.kind -eq 'native' -and @($dialog.buttons).Count -eq 0) {
        return [pscustomobject]@{ name = 'progress-window'; action = 'wait' }
    }
    return $null
}

# ---------------------------------------------------------------------------------------------------------------
# State files

function Get-StateDir([string]$projectPath) {
    $dir = Join-Path $projectPath 'Library\CLIExtensions'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    return $dir
}

function Write-Journal([string]$projectPath, [hashtable]$entry) {
    if (-not $projectPath) { return }
    $entry['time'] = [DateTime]::UtcNow.ToString('o')
    $entry['source'] = 'watchdog'
    [CliExtNative]::AppendLine((Join-Path (Get-StateDir $projectPath) 'dialogs.jsonl'), ($entry | ConvertTo-Json -Compress -Depth 4))
}

function Read-Heartbeat([string]$projectPath) {
    $file = Join-Path $projectPath 'Library\CLIExtensions\heartbeat.json'
    if (-not (Test-Path $file)) { return $null }
    try {
        $hb = Get-Content $file -Raw | ConvertFrom-Json
        $age = ([DateTime]::UtcNow - (Get-Item $file).LastWriteTimeUtc).TotalSeconds
        $hb | Add-Member -NotePropertyName ageSeconds -NotePropertyValue ([math]::Round($age, 1)) -Force
        return $hb
    } catch { return $null }
}

function Get-Snapshot($editor, [string]$projectPath, $policy) {
    if (-not $editor) {
        return [ordered]@{ time = [DateTime]::UtcNow.ToString('o'); state = 'not-running'; project = $projectPath; dialogs = @() }
    }
    $dialogs = @(Get-Dialogs $editor)
    $probe = $(if ($editor.Main) { $editor.Main.Handle } elseif ($editor.Windows.Count -gt 0) { $editor.Windows[0].Handle } else { [IntPtr]::Zero })
    $responds = $(if ($probe -ne [IntPtr]::Zero) { [CliExtNative]::Responds($probe, 3000) } else { $null })
    $heartbeat = $null
    if ($projectPath) { $heartbeat = Read-Heartbeat $projectPath }

    $out = @()
    foreach ($d in $dialogs) {
        $rule = $null; if ($policy) { $rule = Find-Rule $policy $d }
        $out += [ordered]@{
            kind = $d.kind; hwnd = $d.hwnd; title = $d.title; message = $d.message; buttons = $d.buttons; checkboxes = $d.checkboxes
            rule = $(if ($rule) { $rule.name } else { $null }); action = $(if ($rule) { $rule.action } else { 'escalate' })
        }
    }
    # Progress windows ("Reloading Domain", "Compiling...") match a "wait" rule: the Editor is busy, not blocked.
    $blocking = @($out | Where-Object { $_.action -ne 'wait' })

    $state = 'ready'
    if ($blocking.Count -gt 0) { $state = 'blocked-by-dialog' }
    elseif ($out.Count -gt 0) { $state = 'busy' }
    elseif (-not $editor.Main) { $state = 'starting' }
    # Safe Mode loads no package editor code, so no Pipeline server: fix the compile errors in the files, then the
    # Editor normally leaves Safe Mode once they compile.
    elseif ($editor.Main.Title -match ' - SAFE MODE - ') { $state = 'safe-mode' }
    elseif (-not $responds) { $state = 'hung' }
    elseif ($heartbeat -and $policy -and $heartbeat.ageSeconds -gt $policy.staleHeartbeatSeconds) { $state = 'busy' }
    return [ordered]@{
        time = [DateTime]::UtcNow.ToString('o'); state = $state; pid = $editor.Pid; project = $editor.Project
        windowResponds = $responds; mainWindowEnabled = $(if ($editor.Main) { $editor.Main.Enabled } else { $null })
        heartbeatAgeSeconds = $(if ($heartbeat) { $heartbeat.ageSeconds } else { $null })
        editor = $heartbeat; dialogs = $out
    }
}

# ---------------------------------------------------------------------------------------------------------------
# One-shot modes

if (-not $Watch) {
    $editor = Find-Editor
    $projectPath = $(if ($editor) { $editor.Project } else { $Project })
    if ($Click -or $Close) {
        if (-not $editor) { throw "No open Unity Editor for project '$Project'." }
        $dialogs = @(Get-Dialogs $editor)
        if ($Title) { $dialogs = @($dialogs | Where-Object { $_.title -match $Title }) }
        if ($dialogs.Count -eq 0) { throw 'No dialog is open in that Editor.' }
        $d = $dialogs[0]
        if ($Close) {
            $ok = [CliExtNative]::CloseWindow([IntPtr]$d.hwnd)
            Write-Journal $projectPath @{ kind = 'clicked'; title = $d.title; message = $d.message; buttons = $d.buttons; answer = '(close)'; detail = 'watchdog -Close' }
        } else {
            $ok = Press-Button $d $Click
            if (-not $ok) { throw "No button '$Click' on '$($d.title)'. Buttons: $($d.buttons -join ', ')" }
            Write-Journal $projectPath @{ kind = 'clicked'; title = $d.title; message = $d.message; buttons = $d.buttons; answer = $Click; detail = 'watchdog -Click' }
        }
        Start-Sleep -Milliseconds 500
    }
    $editor = Find-Editor
    $policy = $null; if ($projectPath) { $policy = Get-Policy $projectPath }
    Get-Snapshot $editor $projectPath $policy | ConvertTo-Json -Depth 6
    exit 0
}

# ---------------------------------------------------------------------------------------------------------------
# Watch mode

if (-not $Project) { throw '-Watch needs -Project <project folder path>.' }
$projectPath = (Resolve-Path $Project).Path.TrimEnd('\')

if ($Detach) {
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-Watch', '-Project', "`"$projectPath`"")
    if ($EditorPid -gt 0) { $argsList += @('-EditorPid', $EditorPid) }
    if ($PolicyPath) { $argsList += @('-PolicyPath', "`"$PolicyPath`"") }
    if ($NoAutoClick) { $argsList += '-NoAutoClick' }
    if ($IntervalSeconds -gt 0) { $argsList += @('-IntervalSeconds', $IntervalSeconds) }
    $commandLine = 'powershell.exe ' + ($argsList -join ' ')
    # Created by WMI, the process is not a child of the caller (the Unity Editor) and is in none of its job objects,
    # so it survives the Editor crashing. ShowWindow 0 keeps the console hidden.
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine; ProcessStartupInformation = $startup }
    if ($r.ReturnValue -ne 0) { throw "Could not start the watchdog (Win32_Process.Create returned $($r.ReturnValue))." }
    [ordered]@{ started = $true; pid = $r.ProcessId; project = $projectPath } | ConvertTo-Json
    exit 0
}

$stateDir = Get-StateDir $projectPath
$pidFile = Join-Path $stateDir 'watchdog.pid'
$stopFile = Join-Path $stateDir 'watchdog.stop'
$statusFile = Join-Path $stateDir 'watchdog-status.json'

if (Test-Path $pidFile) {
    $other = 0; [int]::TryParse((Get-Content $pidFile -Raw).Trim(), [ref]$other) | Out-Null
    if ($other -and $other -ne $PID -and (Get-Process -Id $other -ErrorAction SilentlyContinue)) {
        Write-Output "A watchdog (pid $other) already watches $projectPath."
        exit 0
    }
}
Set-Content -Path $pidFile -Value $PID -Encoding ASCII
if (Test-Path $stopFile) { Remove-Item $stopFile -Force }

$handled = @{}         # dialog hwnd -> time it was answered or escalated, so each is acted on once
$hungSince = $null
$lastExe = $null
$restarts = New-Object System.Collections.ArrayList
$knownPid = $EditorPid

function Restart-Editor([string]$reason) {
    $now = [DateTime]::UtcNow
    foreach ($t in @($restarts)) { if (($now - $t).TotalMinutes -gt 60) { [void]$restarts.Remove($t) } }
    if ($restarts.Count -ge 3) {
        Write-Journal $projectPath @{ kind = 'escalated'; title = 'Editor restart limit'; message = "Not reopening the project after ${reason}: 3 restarts in the last hour." }
        return $false
    }
    [void]$restarts.Add($now)
    if (-not $lastExe) { return $false }
    Write-Journal $projectPath @{ kind = 'restarted'; title = 'Editor reopened'; message = $reason; detail = $lastExe }
    Start-Process -FilePath $lastExe -ArgumentList "-projectPath `"$projectPath`" $($policy.relaunchArgs)"
    return $true
}

try {
    while ($true) {
        if (Test-Path $stopFile) { Remove-Item $stopFile -Force; break }
        $policy = Get-Policy $projectPath

        $editor = $null
        if ($knownPid -gt 0) { $editor = @(Get-Editors | Where-Object { $_.Pid -eq $knownPid }) | Select-Object -First 1 }
        if (-not $editor) { $editor = @(Get-Editors | Where-Object { Test-ProjectMatch $_.Project $projectPath }) | Select-Object -First 1 }

        if ($editor) {
            $knownPid = $editor.Pid
            try { $lastExe = $editor.Process.MainModule.FileName } catch { }
        }
        elseif ($knownPid -gt 0 -and -not (Get-Process -Id $knownPid -ErrorAction SilentlyContinue)) {
            # The Editor we watched is gone. A normal quit leaves "quitting": true in the heartbeat.
            $hb = Read-Heartbeat $projectPath
            if ($hb -and $hb.quitting) { Write-Journal $projectPath @{ kind = 'exited'; title = 'Editor quit' }; break }
            Write-Journal $projectPath @{ kind = 'crashed'; title = 'Editor process ended without quitting'; detail = "pid $knownPid" }
            $knownPid = 0
            if (-not ($policy.restartOnCrash -and (Restart-Editor 'the Editor crashed'))) { break }
        }

        $snapshot = Get-Snapshot $editor $projectPath $policy
        [CliExtNative]::WriteAllText($statusFile, ($snapshot | ConvertTo-Json -Depth 6))

        if ($editor) {
            foreach ($d in @(Get-Dialogs $editor)) {
                $key = [string]$d.hwnd
                if ($handled.ContainsKey($key)) { continue }
                $rule = Find-Rule $policy $d
                $action = $(if ($rule) { $rule.action } else { 'escalate' })
                if ($action -eq 'wait') { continue }
                $entry = @{ title = $d.title; message = $d.message; buttons = $d.buttons; detail = $(if ($rule) { $rule.name } else { 'no rule' }) }
                if ($action -eq 'click' -and $policy.autoClick) {
                    $button = Find-ButtonByPattern $d $rule.button
                    if ($button -and (Press-Button $d $button)) { $entry.kind = 'clicked'; $entry.answer = $button }
                    else { $entry.kind = 'escalated'; $entry.detail = "$($rule.name): no button matching '$($rule.button)'" }
                }
                elseif ($action -eq 'close' -and $policy.autoClick) {
                    [CliExtNative]::CloseWindow([IntPtr]$d.hwnd) | Out-Null
                    $entry.kind = 'clicked'; $entry.answer = '(close)'
                }
                else { $entry.kind = 'escalated' }
                $handled[$key] = [DateTime]::UtcNow
                Write-Journal $projectPath $entry
            }
            foreach ($k in @($handled.Keys)) { if (-not [CliExtNative]::IsWindow([IntPtr][int64]$k)) { $handled.Remove($k) } }

            if ($snapshot.state -eq 'hung') {
                if (-not $hungSince) { $hungSince = [DateTime]::UtcNow }
                $hungFor = ([DateTime]::UtcNow - $hungSince).TotalSeconds
                if ($policy.killAfterHungSeconds -gt 0 -and $hungFor -ge $policy.killAfterHungSeconds) {
                    Write-Journal $projectPath @{ kind = 'killed'; title = 'Editor not responding'; message = "No response for $([int]$hungFor) s; killing it." }
                    Stop-Process -Id $editor.Pid -Force
                    $knownPid = 0; $hungSince = $null
                    Start-Sleep -Seconds 3
                    if (-not (Restart-Editor 'the Editor stopped responding')) { break }
                }
            } else { $hungSince = $null }
        }

        Start-Sleep -Milliseconds ([int]([double]$policy.intervalSeconds * 1000))
    }
}
finally {
    if ((Test-Path $pidFile) -and ((Get-Content $pidFile -Raw).Trim() -eq [string]$PID)) { Remove-Item $pidFile -Force }
}

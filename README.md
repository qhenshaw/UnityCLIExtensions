# Unity CLI Extensions
[![Unity 6](https://img.shields.io/badge/unity-6-blue.svg)](https://unity.com/releases/editor/archive)
[![License: MIT](https://img.shields.io/badge/License-MIT-brightgreen.svg)](LICENSE.md)

Tools for a Unity Editor that an AI agent (Claude Code and the like) drives from the command line with the
[Pipeline package](https://docs.unity3d.com/Packages/com.unity.pipeline@1.0/manual/index.html)'s `unity command`.

The first focus is keeping the Editor responsive. Pipeline runs commands one at a time on the main thread, so a single
modal dialog ("The open scene(s) have been modified externally", "Enter Safe Mode?", a package's own
`DisplayDialog`) stalls every command until someone clicks it, and on Unity 6.0 to 6.6 the agent only sees a timeout.
This package works in three layers:

1. **Prevent** dialogs up front: launch flags, one-time project setup, and agent-mode behaviour that removes their causes.
2. **Answer** the project's own dialogs in agent mode: `AgentDialog` and `AgentProgress` replace the blocking
   `EditorUtility` calls, return the answer the call site chose, and log it.
3. **Unblock** whatever still comes up with an out-of-process **watchdog** (Windows) that finds the Editor's modal
   windows, reports their title, message and buttons, presses the safe default for known dialogs and escalates the rest.

> [!WARNING]
> This package is maintained with AI-assisted code.

## Contents
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start for agents](#quick-start-for-agents)
- [Agent mode](#agent-mode)
- [Setup checks](#setup-checks)
- [AgentDialog and AgentProgress](#agentdialog-and-agentprogress)
- [Watchdog](#watchdog)
- [Commands](#commands)
- [Files](#files)
- [Blocking dialogs catalog](Documentation~/blocking-dialogs.md)

## Requirements
- Unity 6 (6000.0 or later). Tested on 6000.5.8f1.
- [Pipeline](https://docs.unity3d.com/Packages/com.unity.pipeline@1.0/manual/index.html) 1.0.0 (installed as a dependency) and the `unity` CLI.
- The watchdog runs on Windows (Windows PowerShell 5.1, which ships with Windows 10 and 11). Agent mode and the setup checks work on every platform.

## Installation
Add the package from git: Package Manager > + > Add package from git URL:
```
https://github.com/qhenshaw/UnityCLIExtensions.git
```
To lock a project to one version, add the version tag (see the [changelog](CHANGELOG.md)):
```
https://github.com/qhenshaw/UnityCLIExtensions.git#0.1.0
```

## Quick start for agents
Open the project with the flags that skip startup dialogs, then run the setup once:
```
unity open <project> --accept-upgrades --args "-automated -accept-apiupdate" --until-ready
unity command --project-path <project> cliext.setup check
unity command --project-path <project> cliext.setup apply
```
`-automated` turns agent mode on and makes every `EditorUtility.DisplayDialog`, `DisplayDialogComplex` and file picker
return Cancel at once instead of blocking (verified on 6000.5; see the [catalog](Documentation~/blocking-dialogs.md#what--automated-does)
for the catch). In agent mode the watchdog starts with the Editor.

When a `unity command` times out, the Editor is usually blocked by a dialog. Read the watchdog's report, which is a
plain file and so needs nothing from the blocked Editor:
```
cat <project>/Library/CLIExtensions/watchdog-status.json
```
`state` is `ready`, `blocked-by-dialog`, `busy` (heartbeat stale: compiling or importing), `hung` (the window stopped
responding), `starting` or `not-running`. Each open dialog lists its `title`, `message`, `buttons`, the `rule` that
matched and its `action`. A dialog with `action: escalate` waits for you. Press a button with the watchdog script:
```
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/Watchdog/cliext-watchdog.ps1 -Project <project> -Click "Don't Save"
```
`<package>` is `<project>/Library/PackageCache/com.qhenshaw.unitycliextensions@<hash>` for a git install. Tell the
user which dialogs you answered; `unity command cliext.dialogs log` lists everything that was answered for you.

## Agent mode
Agent mode is on when any of these holds:
- The Editor was started with `-automated` or `-cliext-agent`.
- The `CLIEXT_AGENT` environment variable is `1` or `true`.
- **Agent Mode** is ticked in Project Settings > CLI Extensions, or set with `unity command cliext set_agent_mode --enabled true`.
- The Editor runs in batch mode.

In agent mode:
- `AgentDialog` and `AgentProgress` calls don't open anything (see below).
- The open scenes are saved (those that have a file) when entering Play mode, so a later scene switch, build or quit has nothing to ask.
- `Application.runInBackground` is on for the Play session and restored afterwards, so Play mode keeps running while the Editor is unfocused.
- Dialogs that offer "don't ask again this session" get their safe answer up front (the Input System's restart prompt).
- With `-automated`, open scenes that change on disk are reloaded. Unity would silently keep the stale scene, and the next save would overwrite the file. A scene with unsaved changes is not reloaded; that is logged instead.
- The watchdog starts (Windows; turn off with **Start Watchdog**).

Every setting is in Project Settings > CLI Extensions and is saved in `UserSettings/CLIExtensionsSettings.asset`:
whether an agent drives this Editor is a fact about your machine, not the project.

## Setup checks
One-time fixes for the causes of startup and first-use dialogs. `cliext.setup check` reports, `cliext.setup apply`
fixes; both take `--ids a,b`. Project Settings > CLI Extensions shows them with a Fix button, and Tools > CLI
Extensions > Run Setup applies them all.

| Id | Checks | Fix |
|---|---|---|
| `launch-flags` | The Editor was started with `-automated`, `-accept-apiupdate` and `-skipUpgradeDialogs`. | Manual: reopen with the command above. |
| `tmp-essentials` | TextMesh Pro Essential Resources are imported. | Imports them without the Import dialog. |
| `input-handling` | Active Input Handling enables the Input System package, when it is installed. | Sets it to Both. Needs an Editor restart. |
| `hdrp-wizard` | The HDRP Wizard doesn't open on start. | Turns off its Show on start. |
| `interaction-mode` | Preferences > General > Interaction Mode is No Throttling, so an unfocused Editor keeps updating. | Sets it (an Editor preference shared by all projects on the machine). |
| `enter-play-mode-options` | *Optional.* Enter Play Mode skips the domain and scene reload. | Applied only when named: `--ids enter-play-mode-options`. Static fields then keep their values between Play sessions. |

## AgentDialog and AgentProgress
Drop-in replacements for the blocking `EditorUtility` calls. With a person at the Editor they behave exactly like the
originals. In agent mode they return the answer you give at the call site, write it to the journal and the Console,
and never open a window:
```cs
using UnityCLIExtensions;

if (AgentDialog.Display("Rebuild atlas?", "This takes a minute.", "Rebuild", "Skip", headlessAnswer: true))
    RebuildAtlas();

int choice = AgentDialog.DisplayComplex("Unsaved changes", "Save before closing?", "Save", "Cancel", "Don't Save", headlessAnswer: 0);

for (int i = 0; i < items.Count; i++)
{
    if (AgentProgress.ShowCancelable("Baking", items[i].name, i / (float)items.Count)) break;
    Bake(items[i]);
}
AgentProgress.Clear();
```
In agent mode `AgentProgress` reports through the background `Progress` API instead of the modal progress bar, and
`ShowCancelable` never reports a cancel. Pick headless answers that are safe to take without a person: skip, keep, cancel.

To find the calls to replace, run `unity command cliext.dialogs scan` (or Tools > CLI Extensions > Scan Scripts for
Blocking Dialogs). It lists every `DisplayDialog`, `DisplayDialogComplex`, progress bar, file picker, `ShowModal`,
scene save prompt and interactive `ImportPackage` in Assets/ and in embedded and local packages, with what to use instead.

## Watchdog
`Tools~/Watchdog/cliext-watchdog.ps1` runs outside the Editor, so it keeps working while the Editor's main thread is
blocked and after a crash. In agent mode the Editor starts it (detached, through WMI, so it isn't in the Editor's
process tree); you can also start it with Tools > CLI Extensions > Start Watchdog or `cliext.watchdog start`.

Every couple of seconds it:
- finds the Editor's top-level windows with Win32 and reads each dialog's title, message (the `Edit` control's text),
  buttons and checkboxes;
- checks whether the Editor's window responds (`WM_NULL` with a timeout) and how old the Editor's heartbeat is;
- applies the policy: the first rule whose title regex matches decides between `click` (press a button by regex),
  `close` (cancel the window), `wait` (a progress window) and `escalate` (report it and leave it). Each dialog is acted
  on once, and everything is written to the journal;
- writes `Library/CLIExtensions/watchdog-status.json`;
- notices a normal quit (and exits), a crash (and, with **Reopen After Crash**, reopens the project at most three times
  an hour) and a hung window (with **Kill When Hung** above 0, kills and reopens the Editor after that many seconds).

The default rules answer only dialogs whose safe answer changes nothing the agent didn't intend: Reload for an
externally modified scene, Don't Enable for the Input System prompt, Ignore for Safe Mode, Cancel for file pickers,
and wait on progress windows. Scene save prompts, the API Updater, package errors, version, licensing and file-lock
dialogs are escalated. **Scene Save Prompt** in the settings can answer save prompts with Save or Don't Save. The
Editor writes the policy to `Library/CLIExtensions/watchdog-policy.json` (see `WatchdogPolicy.DefaultRules`), and the
watchdog reads it on every check. The [catalog](Documentation~/blocking-dialogs.md) has the full list.

Run it by hand:

| Command | Does |
|---|---|
| `cliext-watchdog.ps1 -Project <path or name>` | Prints one snapshot as JSON. Without `-Project`, any open Editor. |
| `cliext-watchdog.ps1 -Project <p> -Click "<label>" [-Title <regex>]` | Presses a button on the Editor's dialog, then prints a snapshot. |
| `cliext-watchdog.ps1 -Project <p> -Close` | Closes (cancels) the dialog. |
| `cliext-watchdog.ps1 -Project <p> -Watch [-Detach] [-NoAutoClick]` | Watches until the Editor quits or `Library/CLIExtensions/watchdog.stop` appears. |

Pass `-NoProfile -ExecutionPolicy Bypass` to `powershell` before `-File`.

## Commands
All under `unity command --project-path <project>`:

| Command | Does |
|---|---|
| `cliext status` | Agent mode and why, the settings, the watchdog, and the setup checks that still need fixing. |
| `cliext set_agent_mode --enabled true\|false` | Turns the per-project setting on or off. |
| `cliext.setup check [--ids a,b]` | Runs the setup checks. Changes nothing. |
| `cliext.setup apply [--ids a,b]` | Fixes what they can fix. |
| `cliext.dialogs log [--tail 20] [--since <UTC ISO time>]` | The journal: dialogs answered in agent mode, and what the watchdog saw, pressed or escalated. |
| `cliext.dialogs scan [--local-packages true]` | Finds blocking dialog calls in the project's scripts. |
| `cliext.dialogs clear_log` | Deletes the journal. |
| `cliext.watchdog start` / `stop` | Starts or stops the watchdog. |
| `cliext.watchdog report` | The watchdog's last report. |
| `cliext.package import --path <file.unitypackage>` | Imports a .unitypackage without the Import dialog. |
| `cliext.scene save_open` | Saves every open scene that has unsaved changes and a file. |

`cliext.dialogs log` and `cliext.watchdog report` only read files and run off the main thread, so they still answer
while a dialog is open, as long as no other command is queued ahead of them. When they time out too, read the files.

## Files
Everything the package writes at runtime is under `Library/CLIExtensions/` (git-ignored):

| File | Written by | Holds |
|---|---|---|
| `heartbeat.json` | Editor, about once a second from `EditorApplication.update` | pid, Play mode, compiling, agent mode, `quitting` on a normal quit. A stale file means the main thread is blocked. |
| `dialogs.jsonl` | Editor and watchdog | One JSON object per line: `time`, `source`, `kind` (`answered`, `clicked`, `escalated`, `shown`, `progress`, `crashed`, `exited`, `restarted`, `killed`), `title`, `message`, `buttons`, `answer`, `detail`. |
| `watchdog-status.json` | Watchdog, every check | The last snapshot (see above). |
| `watchdog-policy.json` | Editor | The rules the watchdog applies. |
| `watchdog.pid`, `watchdog.stop` | Watchdog, Editor | The running watchdog; a request to stop. |

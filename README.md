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
   `EditorUtility` calls, return the answer the call site chose, and log it. Unity's "Missing signature" dialog for
   unsigned .unitypackage files is answered with Import Anyway at all times (see [below](#missing-signature-dialog)).
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
- [Missing signature dialog](#missing-signature-dialog)
- [Watchdog](#watchdog)
- [EditorDeferred](#editordeferred)
- [Waiting, compiling and fast Play mode](#waiting-compiling-and-fast-play-mode)
- [Edit batches, affected tests and deadlines](#edit-batches-affected-tests-and-deadlines)
- [Snippets, game states and screenshots](#snippets-game-states-and-screenshots)
- [Asset dependencies and scene diffs](#asset-dependencies-and-scene-diffs)
- [Editing VFX Graphs](#editing-vfx-graphs)
- [Editing Shader Graphs](#editing-shader-graphs)
- [Input actions and simulated input](#input-actions-and-simulated-input)
- [Animator controllers](#animator-controllers)
- [Cinemachine](#cinemachine)
- [Timeline](#timeline)
- [UI Toolkit](#ui-toolkit)
- [Splines](#splines)
- [Addressables](#addressables)
- [Import settings and presets](#import-settings-and-presets)
- [Animation import, clips and avatar masks](#animation-import-clips-and-avatar-masks)
- [Physics, NavMesh and quality settings](#physics-navmesh-and-quality-settings)
- [Agent files: the skills and CLAUDE.md](#agent-files-the-skills-and-claudemd)
- [Testing the package](#testing-the-package)
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
https://github.com/qhenshaw/UnityCLIExtensions.git#0.4.0
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

## Missing signature dialog
Unity 6.3 and later stop every import of a .unitypackage that has no signature (anything not from the Asset Store,
packages you exported yourself included) at a "Missing signature" dialog with Import Anyway (Reimport Anyway when the
assets are already in the project), Cancel and Learn More. It is a Package Manager UI Toolkit modal, so it has no Win32
buttons for the watchdog to press, `-automated` doesn't affect it, and while it is open the Editor's update loop and
the Pipeline server are stopped.

`MissingSignatureAnswer` presses Import Anyway as the dialog opens, from `EditorWindow.windowFocusChanged`, which Unity
still raises inside the modal (also with the Editor in the background). It presses the dialog's own button with a
navigation submit; nothing moves the mouse. It always runs, with or without agent mode, and has no setting. Each answer
goes to the dialog journal (`kind: answered`) and the Console. It answers only the dialog titled exactly "Missing
signature": the trust dialogs for an invalid or untrusted signature have other titles and are left for a person.

## Watchdog
`Tools~/Watchdog/cliext-watchdog.ps1` runs outside the Editor, so it keeps working while the Editor's main thread is
blocked and after a crash. In agent mode the Editor starts it (detached, through WMI, so it isn't in the Editor's
process tree); you can also start it with Tools > CLI Extensions > Start Watchdog or `cliext.watchdog start`.
Turning agent mode off stops a watchdog that agent mode started; one started by hand keeps running.
`cliext-run.ps1` and `cliext-wait.ps1` don't need it running: when its report is missing or stale, they take one
snapshot (`cliext-watchdog.ps1 -Status`) themselves, so a dialog is still found.

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
and wait on progress windows (any native window with no buttons counts as one). Scene save prompts, the API Updater, package errors, version, licensing and file-lock
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

## EditorDeferred
A replacement for `EditorApplication.delayCall` for work that has to happen soon. In an unfocused Editor driven from
the command line, delayCall callbacks have been seen to sit in the queue for minutes after a domain reload while
`EditorApplication.update` kept ticking (6000.5.8f1, even with Interaction Mode set to No Throttling, which makes an
unfocused Editor update faster but doesn't guarantee delayCall runs). `EditorDeferred` runs the work from
`EditorApplication.update`, asks for another update with `EditorApplication.QueuePlayerLoopUpdate()` while it waits,
and gives up with a warning after a timeout, so deferred work either runs or says it didn't:
```cs
using UnityCLIExtensions;

EditorDeferred.Run(() => Debug.Log("next update"));

// Wait for a condition, with a timeout and a fallback.
var handle = EditorDeferred.Run(OpenWindow, when: () => SomethingIsReady(), timeoutSeconds: 60,
                                onTimeout: () => Debug.LogWarning("Never got ready"));
handle.Cancel(); // drops it if it hasn't run yet

// Once the Editor isn't compiling, importing or changing Play mode.
EditorDeferred.RunWhenIdle(RebuildCaches);
```
Exceptions from the work or the condition are logged, never thrown into the update loop. Like delayCall, a pending
call doesn't survive a domain reload: queue it again from `[InitializeOnLoad]` or `[InitializeOnLoadMethod]`. The
package uses it for every deferred call of its own (starting the watchdog, reloading scenes after an import).

## Waiting, compiling and fast Play mode
Commands for the agent's edit, compile, play loop, so it never has to sleep and poll.

**Wait until the Editor is idle.** `unity command cliext.wait idle [--refresh true] [--timeout 120]` returns once the
Editor has spent a few frames in a row not compiling, importing or changing Play mode, with how long it waited and
the standing compile errors. `--refresh true` imports files written outside the Editor first. A compile that succeeds
reloads the domain, which drops any request in flight, this one included. To wait across a reload, wait from outside
the Editor instead:
```
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/cliext-wait.ps1 -Project <project> -Timeout 300
```
It reads the heartbeat file, so it carries on through domain reloads, and returns at once (exit 3, with the dialog)
when a modal dialog blocks the Editor rather than waiting out its timeout. Exit 0 is idle, 1 timed out, 4 no heartbeat.

**Compile and read the errors as data.** Pipeline's `script recompile_status` lists the last compile's errors as
strings, with no warnings, and none when scripts still fail but nothing recompiled. `cliext.compile run` refreshes,
compiles what changed and waits for it. With errors it fails (exit 6) and returns each one with `file`, `line`,
`column`, `code`, `message` and `assembly`, plus the warnings:
```json
{"status":"failed","compiledMs":1818,
 "errors":[{"severity":"error","code":"CS0103","file":"Assets/Broken.cs","line":5,"column":27,"message":"The name 'speed' does not exist in the current context","assembly":"Assembly-CSharp.dll"}],
 "warnings":[{"severity":"warning","code":"CS0168","file":"Assets/Broken.cs","line":5,"column":24,"message":"The variable 'e' is declared but never used","assembly":"Assembly-CSharp.dll"}]}
```
A compile that succeeds reloads the domain, so its reply is usually dropped ("the Editor closed the connection").
That means it compiled: wait with `cliext-wait.ps1`, then read `cliext.compile diagnostics` for any warnings.
The CLI exits 6 for a dropped reply, the same code as a compile error. For an exit code that doesn't depend on the
reload, pass `--async true` (also on `cliext.edits end`): it answers `{"status":"started"}` with exit 0 and compiles
after the reply; then wait with `cliext-wait.ps1` and read `cliext.compile diagnostics`. (Holding the reload with
`LockReloadAssemblies` until the reply has gone doesn't work: in Unity 6.5 the lock defers the compile too.)
`cliext.compile diagnostics` lists the standing errors and warnings at any time, each assembly's from its last
compile. They are kept in `Library/CLIExtensions/compile.json`, so they survive reloads, and they start being
recorded with the first compile after the package is installed.

**Fast Play mode.** `cliext.playmode set_fast --enabled true` turns on Enter Play Mode Options with domain and scene
reload off, so entering Play mode skips reloading every assembly and the scene. The catch: static fields keep their
values between Play sessions, and static events keep their subscribers. `cliext.playmode check_statics` lists the
static fields, auto-properties and events in the project's own runtime code (Assets/ and embedded or local packages)
whose type has no `[RuntimeInitializeOnLoadMethod]` to reset them. `set_fast` warns with the count. Reset each one in
a method like this, or keep domain reload on:
```cs
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
private static void ResetStatics() { Score = 0; Instance = null; OnScore = null; }
```
`--enabled false` puts back the Enter Play Mode settings the project had before `set_fast` turned it on (kept in
`Library/CLIExtensions/playmode-before-fast.json`; `restoredPrevious` says whether it did), or Unity's default when
none were kept. When fast Play mode isn't on, `--enabled false` changes nothing. The `enter-play-mode-options` setup check does the same as `set_fast`
and reports the count too. Readonly fields are not listed; their contents can still change, so check readonly
collections by hand.

## Edit batches, affected tests and deadlines

**One compile for many files.** Writing several scripts one after another can compile and reload the domain after
each. Wrap them in a batch:
```
unity command cliext.edits begin --expire-after 300
# ... write the files ...
unity command cliext.edits end
```
`begin` stops auto-refresh and locks assembly reloads. `end` lifts both, then refreshes and compiles once and answers
like `cliext.compile run`: errors and warnings when the compile fails; when it succeeds, the domain reload usually
drops the reply, so wait with `cliext-wait.ps1`. A refresh asked for explicitly during the batch still compiles, but
the reload (the slow part) waits for `end`. A batch nobody ends ends itself after `--expire-after` seconds, refreshes,
and says so in the journal; it is kept in SessionState, so a domain reload in between puts the lock back, and it ends
with the Editor. `cliext.edits status` says whether one is open. `cliext.compile run` refuses to run inside a batch.

**Only the tests a change affects.** `cliext.test affected` maps each changed script or .asmdef to its assembly and
lists every test assembly that is it or depends on it, directly or through others, with the `cliext.test run` command
that runs each. The changes come from `--files "a;b"` or from git (uncommitted changes against `--since`, default `HEAD`, plus
untracked files); `--all true` lists every test assembly. Test assemblies are the ones whose .asmdef has the
`UNITY_INCLUDE_TESTS` constraint. Changed scenes, prefabs and data are reported as `otherChanges`: nothing maps them to
tests, so run everything when they matter. To run the list in one call:
```
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/cliext-test.ps1 -Project <project> [-Files a,b] [-Since HEAD] [-All]
```
It runs each assembly with `cliext.test run` (below) and prints one summary with passed, failed and skipped counts and
each failure per assembly. Exit 0 when everything passed, 6 when something failed. It refuses to start
(exit 1) while an open scene has unsaved changes, because the Test Runner would stop at a save prompt until someone
answers it; save them with `cliext.scene save_open` or discard them first. When a run leaves the Editor in another
scene (one that timed out or was cancelled can leave it in the Test Runner's Untitled scene), it reopens the scenes
that were open before and lists them in `restoredScenes`.

**Test runs you can trust.** Pipeline's `test status` keeps showing the previous run until a new one starts, and a
filter that matches nothing finishes "green". `cliext.test run` picks the tests first and records the result of the
run it started:
```
unity command cliext.test run --filter UnityTools.Messaging.Tests --filter-type assembly
unity command cliext.test run --filter AddListener                 # part of the full name
unity command cliext.test run --filter Smoke --filter-type category --mode play
unity command cliext.test result --wait true --timeout 600         # the latest run (or --run-id <id>)
```
`--filter-type` is `name` (part of the full name, the default), `exact`, `assembly` or `category`. A filter that
matches nothing fails (exit 13) with the test assemblies that exist and the closest fixture or test names, so
`MesagingServiceTsts` suggests `UnityTools.Messaging.Tests.MessagingServiceTests`. The result has the counts and
each failure with its message and stack trace; exit 0 when everything passed, 6 when a test failed. An Edit Mode run
waits for its result. A Play Mode run reloads the domain, so it returns its `runId` at once: wait with
`cliext-wait.ps1`, then `cliext.test result --wait true`. Like `cliext-test.ps1`, it refuses to start while an open
scene has unsaved changes. Results are kept in `Library/CLIExtensions/test-runs/`. Built when the Test Framework
package is installed.

**A deadline for any command.** A hung `unity command` usually waits on the Editor. `cliext-run.ps1` gives it a
deadline, and when it passes, says what the Editor was doing:
```
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/cliext-run.ps1 -Project <project> -Timeout 60 -Command 'scene open --path Assets/Main.unity'
```
`-Command` is the rest of the `unity command` line, as one string. In time, it prints the command's output and exits
with its code. Past the deadline it stops waiting (the Editor may still finish the work) and prints JSON with a
`reason`: `blocked-by-dialog` (with the dialog and how to press a button), `safe-mode`, `hung`, `compiling`,
`importing`, `busy` (a progress window such as "Hold on"), `main-thread-busy`, or `editor-idle` (the command itself is
slow). Exit 124.

## Snippets, game states and screenshots

**One snippet, many values.** `script eval` runs inside the Editor, so shell arguments never reach the code. Write
`{{key}}` where a value goes and fill it with `cliext.eval run`:
```
unity command cliext.eval run --file Snippets/show-item.cs --vars "item=FrostCandle;mode=fusion"
```
Values are inserted as they are (quote them in the C#: `"{{item}}"`). A placeholder left unfilled is an error, so a
typo can't run with a blank in it. `--print true` returns the filled code without running it; `--internals true` and
`--timeout` work as on `script eval`. Pairs are separated by `;` (or `,` when there is no `;`).

**Wait for a game state.** `cliext.playmode wait_until --file ready.cs --vars ... --timeout 90` runs a snippet every
second until it returns `ready`. The snippet can also push the game forward each time (start the run, skip a wave)
and return what it is waiting for otherwise, which keeps "how to get there" in one file.

**Compile from Play mode.** Nothing compiles while the game is playing. `cliext.compile run --stop-play true` leaves
Play mode first.

**Put the game in a state and screenshot it.** `Tools~/cliext-play-capture.ps1` does the whole loop in one call:
compile (leaving Play mode), open a scene, enter Play mode with Run In Background, wait until a snippet says ready,
then for each shot run its setup snippet and capture the game view, then leave Play mode and tile the shots into a
contact sheet:
```
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/cliext-play-capture.ps1 -Project <project> `
  -Scene Assets/Scenes/Game.unity -Until "Snippets/to-shop.cs|wave=3" -Out C:/Temp/shots `
  -Crop 455,30,450,1030 -Sheet C:/Temp/tooltips.png `
  "wand=Snippets/show-item.cs|id=Pistol" "totem=Snippets/show-item.cs|id=BloodiedTotem"
```
Each shot is `NAME=FILE` or `NAME=FILE|key=value;key=value`, after the named parameters. `-Crop x,y,w,h` crops every
shot before tiling; `-Settle` waits after a setup snippet (default 0.5 s); `-Width`/`-Height` size the capture;
`-NoRecompile` skips the compile and `-Stay` stays in Play mode. Nothing is written into the project; the shots go
to `-Out` (a temp folder by default). The sheet needs no Python. Exit 0 when every shot was captured, 1 with the
step that failed.

**Slow motion, the project's own commands and timed shots.** Effects that last a tenth of a second are hard to catch
with a capture that takes a fraction of a second itself. Three options make that one call too, with no C# snippet:
```
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/cliext-play-capture.ps1 -Project <project> `
  -Scene Assets/Scenes/Arena.unity -TimeScale 0.005 `
  -Run "debugmenu do --section Effects --label 'Preview gun looks'" -Sheet C:/Temp/guns.png a@0 b@1.5 c@3
```
- `-TimeScale` sets `Time.timeScale` (with `cliext.playmode time_scale`) as soon as Play mode has started, before
  `-Until` and `-Run`, so whatever they trigger plays slowly: at 0.005 a 0.1 s flash lasts 20 s. The previous time
  scale is put back at the end, also with `-Stay` and when a step fails.
- `-Run` takes `unity command` lines, run in order after `-Until`: any command the project or Pipeline offers, such
  as a debug menu's. Quote arguments that have spaces (single or double quotes); a leading `unity command` is
  optional. Each runs once (a retry could trigger an effect twice); one that exits non-zero fails the capture.
- A shot written `NAME@SECONDS` is captured that many real seconds after the last `-Run` step. Timed shots are taken
  first, in time order, then the `NAME=FILE` ones. The JSON reports when each was really taken (`at`) and how far
  after its target (`late`), since every capture takes a moment.

## Asset dependencies and scene diffs

**What an asset uses, and what uses it.** `cliext.asset uses --path <asset>` lists what a prefab, scene, material or
ScriptableObject references (`--recursive true` for everything underneath). `cliext.asset used_by --path <asset>`
lists the assets under Assets/ that reference it; for a script, the prefabs and scenes with that component.
`--recursive true` follows it up (a material, the prefab that uses it, the scenes with that prefab). Paths or GUIDs both
work. The first `used_by` builds an index of the project, kept until an asset changes.

**Snapshot and diff a scene or prefab.** Take a snapshot before a change and compare after:
```
unity command cliext.diff snapshot --path Assets/Scenes/Main.unity --name before
# ... change the scene ...
unity command cliext.diff compare --name before --format tsv
```
```
object-renamed    Lights/Sun                                  Directional Light  Lights/Sun
property-changed  Crate  Transform  m_LocalPosition.x         0                  1.5
component-added   Crate  Rigidbody
object-removed    Marker
object-added      Spawn Point
```
A snapshot holds every GameObject, component and visible serialized property, with object references as asset paths
or hierarchy paths. An open scene is read as it is in the Editor, unsaved changes included; a scene that isn't open is
opened additively and closed again; a prefab is read from its asset. Objects are matched by their file ID, then by
hierarchy path, so a rename or reparent shows as `object-renamed` rather than a removal and an addition.
`--update true` replaces the snapshot with the current state; `cliext.diff list` lists the snapshots, kept in
`Library/CLIExtensions/snapshots/`, and `cliext.diff delete --name n` (or `--all true`) deletes them.

`cliext.asset uses` leaves out Editor-only scripts, such as the version-tracking script HDRP attaches to every
material; `--include-editor-scripts true` lists them too.

## Editing VFX Graphs
With the Visual Effect Graph package in the project, `cliext.vfx` reads and edits `.vfx` graphs: the same contexts,
blocks, operators and parameters the graph view offers, without opening it. VFX Graph has no public authoring API,
so these commands use its editor internals from an assembly named `Unity.Testing.VisualEffectGraph.Editor` (one of
the names VFX Graph lets see its internals). It compiles only when `com.unity.visualeffectgraph` is installed.
Proven on VFX Graph 17.5 and 17.6 (17.6 replaced `GetOrCreateGraph` with `GetGraph` and `CreateGraph`; the
`CLIEXT_VFX_GRAPH_17_6_OR_NEWER` version define picks the right call); a later version that renames those internals fails to compile this one assembly only.

**Find what to add.** `cliext.vfx types [--kind context|block|operator|parameter] [--filter text]` lists the
library with a key for each entry, such as `Context/Common/Spawn`, `Set/Set Lifetime Random Uniform` or
`Operator/Math/Arithmetic/Multiply`. `add` takes the key, the name alone when it is unique, or the class name.

**Read a graph.** `cliext.vfx inspect --path Assets/VFX/Spark.vfx` lists the contexts (with their flow links and
blocks), operators and parameters. Each has an `id` (its file ID in the asset, stable across reloads), its settings
and its input slots, with each value or link. `--id <id>` shows one model; `--details false` just the structure.

**Edit it.** Each command saves and recompiles the graph, and returns what the compile logged:
```
unity command cliext.vfx create --path Assets/VFX/Spark.vfx
unity command cliext.vfx add --path Assets/VFX/Spark.vfx --kind context --type Spawn
unity command cliext.vfx add --path Assets/VFX/Spark.vfx --kind block --type "Constant Spawn Rate" --context <spawn id>
unity command cliext.vfx set --path Assets/VFX/Spark.vfx --id <block id> --slot Rate --value 40
unity command cliext.vfx set --path Assets/VFX/Spark.vfx --id <output id> --setting blendMode --value Additive
unity command cliext.vfx link --path Assets/VFX/Spark.vfx --from <spawn id> --to <init id>
unity command cliext.vfx link --path Assets/VFX/Spark.vfx --from <parameter id>:#0 --to <block id>:Rate
unity command cliext.vfx remove --path Assets/VFX/Spark.vfx --id <id>
```
A slot is `<id>:<slot>`: the slot's name (a leading underscore is optional), a path into its fields (`velocity.y`),
or `#index`. `link` and `unlink` join two contexts when both ends are context ids (`--from-index`/`--to-index` pick
a flow slot, such as a spawner's Stop), and an output slot to an input slot otherwise. `set` on a parameter sets its
default value. Values are text: numbers, `true`/`false`, enum names, `[x,y,z]` for vectors (also the VFX wrappers
such as Position and Vector) and colours (or `#RRGGBB`), asset paths for textures and meshes,
`[[time,value],...]` for curves and `[[time,r,g,b(,a)],...]` for gradients, and JSON for other structs.
`--space Local|World` sets a slot's space.

**Many edits, one compile.** `cliext.vfx edit` applies a JSON array of ops in order and saves once. An op that adds
a model can name it with `as`; later ops refer to it as `$name`:
```json
[
  {"op":"add","kind":"context","type":"Spawn","as":"spawn"},
  {"op":"add","kind":"block","type":"Constant Spawn Rate","context":"$spawn","as":"rate"},
  {"op":"add","kind":"context","type":"Initialize Particle","as":"init"},
  {"op":"link","from":"$spawn","to":"$init"},
  {"op":"set","id":"$init","setting":"capacity","value":"256"},
  {"op":"add","kind":"block","type":"Set Velocity","context":"$init","as":"vel"},
  {"op":"set","id":"$vel","slot":"velocity","value":"[0,2,0]"},
  {"op":"add","kind":"context","type":"Update Particle","as":"update"},
  {"op":"link","from":"$init","to":"$update"},
  {"op":"add","kind":"block","type":"Gravity","context":"$update"},
  {"op":"add","kind":"context","type":"Output Particle Unlit Quad","as":"out"},
  {"op":"link","from":"$update","to":"$out"},
  {"op":"add","kind":"block","type":"Set Size Over Life","context":"$out","as":"size"},
  {"op":"set","id":"$size","slot":"Size","value":"[[0,0.2],[1,0]]"},
  {"op":"add","kind":"parameter","type":"Float","as":"rateParam"},
  {"op":"rename","id":"$rateParam","name":"SpawnRate"},
  {"op":"expose","id":"$rateParam","value":"true"},
  {"op":"link","from":"$rateParam:#0","to":"$rate:Rate"}
]
```
Pass it with `--ops '<json>'` or, easier to quote, `--ops-file <path>` (Windows PowerShell 5.1 strips the double
quotes from an inline JSON argument, so use the file there). Ops: `add` (kind, type, context, index, x,
y, as), `remove` (id), `set` (id, setting or slot, value, space), `link`/`unlink` (from, to, fromIndex, toIndex),
`move` (id, x, y), `rename` (id, name: a context's label or a parameter's name), `expose` (id, value). The result
lists each op with the ids of the models it added. An op that fails saves nothing: the graph is put back as it was,
in memory and on disk, and the result gives the op's index and the reason. Contexts land under the lowest one and
nodes to the left unless given x and y.

The VFX Graph window can stay open on the graph while these run; edits made with it open saved and compiled cleanly.

**Check it.** A graph can compile cleanly and still show nothing. Every edit command and `inspect` also return
`warnings` and `eventAttributes`, and `cliext.vfx check --path <vfx> [--strict true]` returns them on their own:
```json
{"warnings":[{"severity":"warning","id":"114350483966675102","where":"Single Burst in 'Tracer streak'",
  "problem":"Single Burst has a Count of 0, so it spawns nothing (0 is the default). Set its Count slot.","source":"cliext"}],
 "eventAttributes":[{"event":"OnPlay","attributes":["color","lifetime","position","scaleX","scaleY","scaleZ","size","velocity"]}]}
```
- `warnings` (severity `error`, `warning` or `info`, with the model's id): a `Single Burst` or `Periodic Burst` whose
  Count is 0 (its default), a spawner with no burst or rate block, an Initialize with capacity 0, a flipbook output
  with no `mainTexture`, a flipbook with a 1x1 grid, plus VFX Graph's own warnings, the badges the graph window would
  show (`source: "vfx"`).
- `eventAttributes`: for each Event context, the attributes its systems read from the event (`Set ... From Source` in
  their Initialize), minus those a spawner on the way sets itself. These are the exact names script must send with
  `VFXEventAttribute`: a variadic attribute is read per channel, so `Set Scale From Source` needs `scaleX`, `scaleY`
  and `scaleZ`, never `scale` (which the event silently ignores).

`check` exits 6 when there is an error, and with `--strict true` also on a warning.

## Editing Shader Graphs
With the Shader Graph package in the project, `cliext.shadergraph` reads and edits `.shadergraph` files without the
Shader Graph window, and returns the shader's compile errors as data after every edit. Like `cliext.vfx`, it uses
Shader Graph's editor internals from an assembly Shader Graph lets see them (`Unity.ShaderGraphTool.Editor`), built
only when `com.unity.shadergraph` is installed. Proven on Shader Graph 17.5 with HDRP.

**Find what to add.** `cliext.shadergraph types [--filter text]` lists the nodes by menu path
(`Math/Basic/Multiply`, `Input/Texture/Sample Texture 2D`, `Utility/Custom Function`); `--targets true` lists the
render pipeline targets and their material types for `create`.

**Read a graph.** `cliext.shadergraph inspect --path Assets/Shaders/Glow.shadergraph` gives the targets and material,
the vertex and fragment blocks, the blocks the target can take (`blocksAvailable`), every node with its id, settings
and slots (values or links), the properties, and `compile`: the shader's errors and warnings and each node's own
errors. `--id <node>` shows one node.

**Create one.** `cliext.shadergraph create --path Assets/Shaders/Glow.shadergraph --target HDRP --material Unlit`
makes a graph with that material's default blocks; `--from <graph>` copies one instead.

**Edit it.** `add` (a node), `add_property`, `remove`, `set`, `link` and `unlink` each make one change, then save
and compile; `edit` applies a JSON list of ops with one save:
```json
[
  {"op":"add_property","type":"color","name":"Tint","reference":"_Tint","value":"[1,0.5,0.2,1]","as":"tint"},
  {"op":"add_property","type":"float","name":"Glow","reference":"_Glow","value":"2","as":"glow"},
  {"op":"add_node","type":"Math/Basic/Multiply","as":"mul"},
  {"op":"link","from":"$tint","to":"$mul:A"},
  {"op":"link","from":"$glow","to":"$mul:B"},
  {"op":"add_block","block":"Emission","as":"emission"},
  {"op":"link","from":"$mul:Out","to":"$emission:Emission"},
  {"op":"add_node","type":"Custom Function","as":"fn"},
  {"op":"set","id":"$fn","setting":"sourceType","value":"String"},
  {"op":"set","id":"$fn","setting":"functionName","value":"Stripe"},
  {"op":"add_port","id":"$fn","name":"x","type":"float","value":"in"},
  {"op":"add_port","id":"$fn","name":"Out","type":"vector3","value":"out"},
  {"op":"set","id":"$fn","setting":"functionBody","value":"Out = x * float3(1, 0, 0);"}
]
```
A slot is `<node id>:<slot>`, by slot id, display name or shader name. Linking a property (its name or id) to a slot
adds a property node for it. `set` takes a node's input slot (`slot`), a node's own setting (`setting`: a Custom
Function's `functionBody`, a texture type, ... as `inspect --id` lists them), or a property's default (no slot).
Ops: `add_node`, `add_property` (type: float, vector2-4, color, texture2d, texture2darray, texture3d, cubemap,
boolean, matrix2-4, gradient, samplerstate), `add_block`, `add_port`/`remove_port` (Custom Function ports),
`remove`, `set`, `link`, `unlink` (every link into `to`, or only the one from `from`), `move`, `rename` (a property's
name and reference), `expose`. Links are type-checked as the window checks them.

Every edit runs on a copy loaded from the file: an op that fails saves nothing. After saving, each pass of the shader
is compiled for the current platform, so HLSL errors (a Custom Function's body) come back at once, each reported once
with the number of passes it appeared in. A command exits 6 when the graph saved but has errors. Edits are refused
while a Shader Graph window has the graph open, since its own copy would overwrite them; close it or pass
`--force true` to `edit`.

## Input actions and simulated input
With the Input System package in the project, `cliext.input` edits `.inputactions` assets and, in Play mode, drives
the game through virtual devices.

**Read and edit actions.** `cliext.input inspect --path Assets/Settings/Player.inputactions` lists the maps, actions
(type, control type, interactions, processors), their bindings by index, and the control schemes; a binding whose
device layout the Input System doesn't know is marked `valid: false`. `cliext.input edit --path ... --ops-file ops.json`
applies a JSON list of ops and saves once (an op that fails saves nothing):
```json
[
  {"op":"add_action","map":"Player","action":"Dash","type":"Button"},
  {"op":"add_binding","map":"Player","action":"Dash","path":"<Gamepad>/buttonEast","groups":"Gamepad"},
  {"op":"add_binding","map":"Player","action":"Dash","path":"<Keyboard>/leftShift","groups":"Keyboard&Mouse","interactions":"press"},
  {"op":"add_action","map":"Player","action":"Strafe","type":"Value","controlType":"Axis"},
  {"op":"add_composite","map":"Player","action":"Strafe","composite":"1DAxis","parts":"negative=<Keyboard>/q;positive=<Keyboard>/e","groups":"Keyboard&Mouse"},
  {"op":"set_binding","map":"Player","action":"Dash","index":1,"path":"<Keyboard>/space"},
  {"op":"set_action","map":"Player","action":"Fire","interactions":"hold(duration=0.3)"},
  {"op":"add_scheme","name":"Steam Deck","devices":"<Gamepad>;?<Mouse>"}
]
```
Ops: `add_map`, `remove_map`, `rename_map`, `add_action`, `remove_action`, `rename_action`, `set_action` (type,
control type, interactions, processors; the action moves to the end of its map), `add_binding`, `add_composite`
(`parts` as `name=path;...`), `set_binding` and `remove_binding` (by `index` or `id`), `add_scheme` (`devices`
separated by `;`, `?` for optional), `remove_scheme`. Unknown device layouts in a path come back as warnings.

**Drive the game.** In Play mode:
```
unity command cliext.input add_device --layout Gamepad
unity command cliext.input set --control leftStick --value "[0,1]" --hold 2
unity command cliext.input press --control buttonSouth --hold 0.1
unity command cliext.input actions --filter Player
```
`set` holds a control at a value (`--hold` releases it after that many seconds); `press` presses and releases a
button or key. Controls are paths on the virtual device (`leftStick`, `buttonSouth`, `space`) or full paths
(`<Keyboard>/space`). Each event carries every control the device is holding, so a stick stays held while a button is
pressed. `actions` lists the enabled actions with their phase and the value each had at its last start, perform or
cancel, as the game saw it (a value read from a command would see the Editor's input state, not the game's).
`devices` lists the devices; `remove_device` removes the virtual ones. Virtual devices and replays are removed when
Play mode ends.

**Record and replay.** `record_start [--device d]`, play, then `record_stop --out run.inputtrace`;
`replay --file run.inputtrace [--timing false]` plays it back on new copies of the recorded devices, and
`replay_status` says when it is done.

## Animator controllers
Pipeline's `animation.animator` commands create controllers and add layers, parameters, states and transitions.
`cliext.animator` covers the rest:

- `inspect --path <controller>`: parameters with defaults; layers with weight, blending, mask, sync and IK; every
  state by path (`Locomotion`, `Combat/Attack`) with its motion, blend tree (type, parameters, children with
  thresholds or positions), speed and speed parameter, tag and behaviours; every transition with its conditions, exit
  time, duration, offset and interruption; Any State transitions; sub-state machines.
- `validate --path <controller>`: states with no motion, unreachable states, transitions that can never fire (no
  conditions and no exit time), conditions on missing or mistyped parameters, blend trees on missing or non-Float
  parameters, unused parameters. Exit 6 on errors.
- `edit --path <controller> --ops-file ops.json`, saved once (Undo in the Editor reverts it; an op that fails saves nothing):
```json
[
  {"op":"set_state","state":"Locomotion","set":"speed=1.25;tag=Move;speedParameterActive=true;speedParameter=Speed"},
  {"op":"add_blendtree","state":"Strafe","type":"SimpleDirectional2D","parameter":"MoveX","parameterY":"MoveY",
   "children":"Assets/Anims/Idle.anim@0,0;Assets/Anims/Walk.fbx@walk@0,1;Assets/Anims/Run.fbx@run@0,2"},
  {"op":"set_transition","from":"Any","to":"Airborne","conditions":"IsGrounded IfNot","set":"duration=0.05;canTransitionToSelf=false"},
  {"op":"remove_parameter","name":"Unused"},
  {"op":"set_layer","layer":"Upper Body","set":"defaultWeight=1;blendingMode=Override;avatarMask=Assets/Masks/Upper.mask"}
]
```
  Ops: `remove_state`, `rename_state`, `set_state` (`motion` and `set` with any settable property), `set_default`,
  `add_substatemachine`, `remove_substatemachine`, `set_transition` (`from` a state or `Any`, `to` a state or `Exit`,
  `index` among several, `conditions` as `Param Mode [Threshold]` separated by `;`, checked against the parameter's
  type), `remove_transition`, `remove_parameter` (also removes the conditions that use it), `set_parameter` (default
  value), `set_layer`, `remove_layer`, `add_blendtree` (on a new or existing state). A motion is a clip path, or a
  model's clip as `Assets/Char.fbx@Run`.
- `live --object <GameObject>` (Play mode): each layer's current state by name, normalized time, clips and their
  weights, the transition in progress, and every parameter's value.
- `drive --object <GameObject> --parameter Speed --value 1` sets a parameter (a Trigger with `true`/`false` sets or
  resets it); `--state "Base Layer.Run"` cross-fades to a state. The game's own scripts may set the same parameter
  again on the next frame.

## Cinemachine
With Cinemachine 3 in the project, `cliext.cinemachine` reads and changes the cameras in the open scenes:

- `list`: every CinemachineCamera (priority, follow and look-at targets, field of view, rig components, enabled, and
  in Play mode whether it is live) and every brain (default blend, Custom Blends asset, the live camera, and the blend
  in progress with its weight, time and duration).
- `create --name Overhead --rig follow|orbital|thirdperson|lookat|fixed [--follow Player] [--lookat Boss]
  [--priority 10] [--fov 50] [--position "[0,15,-10]"]`: a camera with that rig's components; a CinemachineBrain is
  added to the main Camera when there is none. In Play mode the camera lasts until Play mode ends.
- `set --camera Overhead [--priority 20] [--follow Player|none] [--lookat ...] [--fov 50] [--enabled false]`.
- `blend --style EaseInOut --time 1`: the brain's default blend. With `--from A --to B` (either left out for any
  camera), a custom blend in the brain's Custom Blends asset, created at `--asset <path>` when it has none.

Edits in Edit mode are undoable and mark the scene dirty; in Play mode they last until Play mode ends.

## Timeline
Pipeline's `animation.timeline` commands create a timeline and add tracks and clip assets. `cliext.timeline` adds:

- `inspect --path <timeline>` (or `--director <GameObject>`): every track by path with its type, mute and lock, its
  clips (start, duration, end, clip-in, speed, ease and blend) and markers, the timeline's markers, and with a
  director, what each track is bound to.
- `edit --path <timeline> --ops-file ops.json`, saved once:
```json
[
  {"op":"add_clip","track":"Moves","clip":"Assets/Chars/Hero@run.fbx@run","time":0,"set":"duration=2"},
  {"op":"set_clip","track":"Moves","clip":"0","set":"start=0.5;duration=3;easeIn=0.25;easeOut=0.5;timeScale=1.5","name":"RunCycle"},
  {"op":"move_clip","track":"Moves","clip":"1","to":"Moves2"},
  {"op":"add_marker","time":2.0,"signal":"Assets/Signals/Footstep.signal"},
  {"op":"mute_track","track":"Lights","value":"true"},
  {"op":"rename_track","track":"Lights","name":"Lamps"}
]
```
  Ops: `add_clip` (a clip asset or a model's take as `model.fbx@take`; no clip for a track's default clip),
  `set_clip`, `move_clip`, `remove_clip`, `remove_track`, `rename_track`, `mute_track`, `lock_track`, `add_marker`
  (on a track or the timeline's markers, optionally a signal), `remove_marker`. A clip is its index or name on the
  track; a track is its name, its path through groups, or its index.
- `bind --director <GameObject> --track Moves --object Player`: binds a track to a scene object (the component the
  track needs, such as its Animator), or `none`.
- `director --director <GameObject> [--time 1.5] [--action play|pause|resume|stop]`: scrubs (evaluating that frame,
  in Edit mode too), plays and reads a director's time, duration and state.

## UI Toolkit
`cliext.uitk` edits UXML and USS files and works with the live UI in Play mode.

**Files.** `inspect --path Menu.uxml` gives the element tree (type, name, classes, text, attributes), its style
sheets and templates; on a `.uss` file, its rules and properties. `edit --path <file> --ops-file ops.json` applies
ops, saves once, and reimports through Unity's importer, whose errors come back as `import` (exit 6):
```json
[
  {"op":"add","selector":"#menu","type":"Button","name":"quit","text":"Quit","class":"menu-button","attributes":"tooltip=Leave the game"},
  {"op":"set_attributes","selector":"Button#start","text":"Play"},
  {"op":"add_class","selector":"#menu > Button","class":"wide"},
  {"op":"move","selector":"#quit","to":"#footer","index":0},
  {"op":"remove","selector":".debug-only"}
]
```
UXML ops: `add` (no selector adds at the top level), `remove`, `move`, `set_attributes`, `add_class`,
`remove_class`. Selectors are `Type`, `#name`, `.class`, combined (`Button.primary#start`) and nested with spaces or
`>`. New elements go on their own indented lines. USS ops: `set_rule` (`selector` as written,
`properties: "padding: 8px; color: #fff"`, merged into the rule or `replace: true`; a missing rule is added),
`remove_rule`, `remove_property`. An op that fails saves nothing.

**Live UI** (UIDocuments in the open scenes, so mostly Play mode):
- `tree [--document <GameObject>] [--depth 8]`: each element's type, name, classes, text or value, screen rect, and
  whether it is hidden or disabled.
- `query --selector "#menu > Button"`: the matching elements with their paths.
- `click --selector "#start"`: clicks a button the way a navigation submit does, so its `clicked` handlers run once.
- `set_value --selector "#volume" --value 0.5`: sets a field's value (TextField, Toggle, Slider, DropdownField, ...;
  its change event fires) or a label's text.

## Splines
With the Splines package in the project, `cliext.splines` works with the SplineContainers in the open scenes:

- `list`: each container's splines with knot count, closed flag, world length and bounds.
- `inspect --object Road`: every knot's local and world position, tangents, rotation and tangent mode.
- `create --name Path --knots "[0,0,0];[5,0,0];[5,0,5]" [--mode AutoSmooth|Linear|Mirrored|Continuous|Broken]
  [--closed true] [--position "[x,y,z]"]`.
- `edit --object Road --ops-file ops.json`: `add_knot` (position, index, mode), `set_knot` (index, position,
  tangentIn, tangentOut, rotation, mode; `world: true` for world positions), `remove_knot`, `set_closed`,
  `set_tangent_mode` (one knot or all), `add_spline` (knots, mode), `remove_spline`; `spline` picks the spline.
  Undoable; marks the scene dirty.
- `sample --object Road [--spline 0]` with `--t 0.5` (0-1 along it), `--distance 12` (world units), `--near "[x,y,z]"`
  (the nearest point on the spline, its t and distance), or `--count 10` evenly spaced samples: world position,
  direction and up.

## Addressables
With the Addressables package in the project, `cliext.addressables`:

- `status`: whether settings exist, the active profile and play mode script, the labels, and every group with its
  entry count, packing, compression and build and load paths. `init` creates the settings if there are none.
- `entries [--group g] [--label l] [--filter text]`: address, path, group and labels of each entry.
- `edit --ops-file ops.json`, saved once:
```json
[
  {"op":"add_group","name":"Characters","set":"bundleMode=PackSeparately;compression=LZ4"},
  {"op":"add_entry","path":"Assets/Chars/Hero.prefab","group":"Characters","address":"chars/hero","labels":"player;hero"},
  {"op":"set_address","path":"Assets/Chars/Hero.mat","address":"mats/hero"},
  {"op":"set_labels","path":"Assets/Chars/Hero.prefab","labels":"hero"},
  {"op":"move_entry","path":"Assets/Chars/Hero.mat","group":"Characters"}
]
```
  Ops: `add_group`, `remove_group`, `rename_group`, `set_group` (`bundleMode`, `compression`, `includeInBuild`),
  `add_entry` (labels are added to the project when new), `remove_entry`, `move_entry`, `set_address`, `set_labels`
  (the full set), `add_label`, `remove_label`.
- `build [--clean true]`: builds the content for the active build target and returns the duration, output and bundle
  count. The first build's "Enable Build Report" prompt is answered as seen, without changing the setting, so it
  doesn't wait on a dialog.
- `analyze [--rule "Bundle Dependencies"]`: runs the Analyze rules and returns their results with severities
  (nothing is fixed); exit 6 on error results.

## Import settings and presets
Pipeline's `asset get_import_settings` / `set_import_settings` read and change one asset. `cliext.import batch`
changes many, only where they differ, and reimports just those:
```
unity command cliext.import batch --folder Assets/Art/UI --type Texture2D --set "maxTextureSize=1024;mipmapEnabled=false" --dry-run true
unity command cliext.import batch --folder Assets/Art/Props --filter "*.fbx" --set "globalScale=1;meshCompression=Medium;materialImportMode=None"
unity command cliext.import batch --folder Assets/Art --type Texture2D --platform Android --set "maxTextureSize=512;format=ASTC_6x6"
```
`--set` takes the importer's own property names (TextureImporter, ModelImporter, ...); `--platform` writes a texture
platform override. Each changed asset comes back with `name: old -> new`.

`cliext.presets` manages default presets per folder and checks them:
- `list`: the default presets per type, with their filters.
- `create --from Assets/Art/UI/Button.png --out Assets/Presets/UITexture.preset`: a preset from an asset's importer.
- `set_default --preset Assets/Presets/UITexture.preset --filter Assets/Art/UI`: new and reimported assets in that
  folder get it (a folder becomes `glob:"Assets/Art/UI/**"`; any Preset Manager filter works). `--remove true` takes
  it out.
- `apply --preset ... --folder ... [--filter "*.png"] [--dry-run true]`: applies it to existing assets.
- `audit [--folder Assets]`: every asset whose importer breaks the default preset that applies to it, with each
  differing setting (`m_MaxTextureSize: is 8192, preset 512`). Nothing is changed; exit 6 when anything breaks a
  preset.

## Animation import, clips and avatar masks
`cliext.anim` covers what Pipeline's `animation.clip` commands (whole curves on .anim clips) leave out:

- `takes --path Hero@run.fbx`: the model's rig type and avatar, the takes in the file with their frame ranges, and
  the clips it imports with loop, root motion (baked or not, original or not, per rotation, height and XZ), mirror,
  mask and events.
- `edit_clips --path Hero@run.fbx --ops-file ops.json`: `add_clip` (name, take, first, last frames), `set_clip` (`set:
  "loopTime=true;loopPose=true;lockRootRotation=true;keepOriginalOrientation=true;lockRootHeightY=true"`, first,
  last), `remove_clip`, `set_events` (`"0.25:Footstep:Left;0.75:Footstep:Right"`, normalized times), `set_mask`
  (an AvatarMask, or `none`); reimports once. An op that fails saves nothing.
- `clip --path Bounce.anim [--keys true]`: length, frame rate, loop settings, each curve with its key count (or
  keys), sprite curves and events; a model's clip as `Hero@run.fbx@run`.
- `edit_clip --path Bounce.anim --ops-file ops.json`: `set_key` (path, type, property, time, value, tangent
  `auto|linear|constant|free`; replaces a key at that time), `remove_key`, `add_event` (seconds, function, parameter),
  `remove_event`, `set_settings` (`loopTime`, `loopBlend`, `cycleOffset`, `mirror`, ...).
- `mask --path Upper.mask [--humanoid "LeftLeg=false;RightLeg=false"] [--from Hero.fbx] [--transforms
  "Hips/LeftUpLeg/*=false"]`: creates or changes an AvatarMask; `--from` lists a model's transforms in it.
- `check --model Hero.prefab --clips "Hero.controller;Assets/Anims/Extra.fbx"`: whether the clips fit the model:
  humanoid clips on a model without a humanoid avatar, generic clips on a humanoid, an invalid avatar, animated paths
  the model doesn't have, curves for components it doesn't have (a SpriteRenderer clip on a 3D model), legacy clips.
  Exit 6 on a mismatch.

## Physics, NavMesh and quality settings
Pipeline's `settings.*` commands read and set the basics (gravity, tags and layers, the quality level). These add:

- `cliext.physics matrix [--2d true]`: for each named layer, the layers it doesn't collide with.
  `set_matrix --a Player --b Pickup --collide false [--2d true]` changes a pair (both directions), saved.
- `cliext.physics material --path Assets/Physics/Ice.physicMaterial --set "dynamicFriction=0.02;staticFriction=0.05;frictionCombine=Minimum"`
  creates or changes a physics material (`--2d true` for a PhysicsMaterial2D: friction, bounciness).
- `cliext.physics raycast --from "[0,20,0]" [--direction "[0,-1,0]" | --to "[x,y,z]"] [--distance 50]
  [--layers "Default;Ground"] [--all true]`: hits in the open scenes (Edit or Play mode) with object, collider,
  point, normal, distance and layer.
- `cliext.navmesh settings`: areas (index, name, cost) and agent types (radius, height, slope, step).
  `set --index 3 --name Mud --cost 3`, or `set --agent Humanoid --set "agentRadius=0.4;agentHeight=1.8"`.
- `cliext.navmesh query --from "[0,0,0]" --to "[5,0,5]" [--agent Humanoid] [--areas "Walkable;Mud"]`: a path
  (status, corners, length) on the baked NavMesh, both ends snapped within `--distance`; or `--sample "[x,y,z]"` for
  the nearest NavMesh point.
- `cliext.navmesh surfaces` and `bake [--surface TestRoom|all]` (AI Navigation 2): NavMeshSurfaces and baking them
  as the Bake button does, waiting for the bake. A surface's old data asset is cleared first, so no
  `NavMesh-Room 1.asset` is left; surfaces on prefab instances are baked the button's way (a new asset beside the old).
- `cliext.quality levels [--level Low]`: every quality level with all its settings (51 in Unity 6.5, including
  `customRenderPipeline`), the platforms it's excluded from, the current level and each platform's default.
  `set --level Low --set "shadowDistance=40;vSyncCount=0;customRenderPipeline=Assets/Settings/Low.asset" [--exclude "Android;iPhone"]`,
  or `set --platform Android --default Low`.
- `cliext.quality graphics [--platform Android] [--set "Vulkan;OpenGLES3" | --auto true]`: a platform's graphics
  APIs, the default render pipeline, and each quality level's pipeline override.

## Agent files: the skills and CLAUDE.md
The package ships Claude Code skills in `Skills~/`:
- `unity-cliext`: when to reach for the Editor, the compile-wait-test loop, the rules (no sleep-polling, dialogs,
  unsaved scenes, ops files) and a map of every `cliext` group.
- `unity-cliext-vfx`, installed only in projects with VFX Graph: building effects with `cliext.vfx` (block keys and
  slot names for VFX Graph 17.6, the defaults that spawn nothing, the C# side of event attributes, slow-motion
  captures) and ops templates in `ops/` (`burst.json`, `sparks.json`, `streak-from-source.json`) that apply cleanly
  and pass `cliext.vfx check` once their texture path is set.

Agents find skills in the project's `.claude/skills/`, so the package puts them there itself:

- When the package loads (so on install and on every update), it copies the skills that apply to
  `.claude/skills/<name>/` and keeps a marked block in the project's `CLAUDE.md` that tells agents to load them
  (`<!-- BEGIN unity-cliext ... -->` to `<!-- END unity-cliext -->`). `CLAUDE.md` is created if missing; everything
  outside the block is left as it is. A package skill whose package was removed (VFX Graph) is taken out again.
- Only what differs from the installed package version is written, so it does nothing on most loads. The installed
  skill carries a version stamp: a newer package replaces it, while edits made by hand to the current version's copy
  are kept (`--force true` replaces them).
- `unity command cliext.setup agent [--force true]` does the same on demand and says what it changed;
  `--remove true` takes the skill and the block out. Tools > CLI Extensions > Update Agent Files does it from the menu.
- Turn it off per user in Project Settings > CLI Extensions > Keep Skill and CLAUDE.md Current.

`cliext status` also reports the package `version` and `tools`, the folder of the `Tools~` scripts the skill refers to.

## Testing the package
Two layers. EditMode tests in `Tests/` run inside the Editor: list the package under `testables` in the project's
`Packages/manifest.json`, then `unity command cliext.test run --filter UnityCLIExtensions.Tests --filter-type assembly`
(and `UnityCLIExtensions.VFX.Tests` in projects with VFX Graph). `CommandSchemaTests` among them lints every command's
declared schema: descriptions, kebab-case names, ASCII text, effects, list separators, sentinel defaults, types,
enum values, GUID support and the all-or-nothing wording of batch edits.

What only shows from outside the Editor is checked by `Tools~/cliext-clitest.ps1`, which drives the running Editor
through `unity command`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File <package>/Tools~/cliext-clitest.ps1 -Project <project> [-Filter '^xcut\.'] [-Out results.json] [-Keep]
```

It checks setup, compile errors and edit batches, test runs, the dialog journal and the watchdog, snippets with
placeholders, exit codes (a reply dropped by a domain reload is exit 6 with `EDITOR_CONNECTION_LOST`; a real failure
is exit 6 with its reason on stderr), reply shapes, misspelled and either/or parameters on every batch edit, that the
installed skills match `Skills~/`, and how Windows PowerShell 5.1 passes quoted arguments. It prints one line per check,
then T1 coverage (for each core-loop group, the commands this run called and the ones it didn't), and exits 0 when all
pass, 1 when one fails, 2 when no Editor answers. Scratch files go in `Assets/_CliextTests/CliTest` and are removed at
the end (`-Keep` leaves them). `-T3Ready` runs only an environment check for agent scenario runs: setup checks applied,
agent mode on, the watchdog running, and the installed skills current.

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
| `cliext.scene save_open [--scene <path>] [--dry-run true]` | Saves open scenes that have unsaved changes and a file (or only `--scene`) and returns their paths; `--dry-run` lists them and saves nothing. |
| `cliext.wait idle [--refresh true] [--timeout 120] [--settle-frames 3]` | Waits until the Editor is idle. |
| `cliext.compile run [--warnings true] [--timeout 300] [--async true] [--stop-play true]` | Refreshes, compiles, and returns structured errors and warnings. `--async true` answers at once and compiles after; `--stop-play true` leaves Play mode first. |
| `cliext.compile diagnostics [--warnings true]` | The standing errors and warnings, one record each. |
| `cliext.playmode set_fast --enabled true\|false` | Turns fast Play mode (no domain or scene reload) on or off. |
| `cliext.playmode check_statics [--include-reset true]` | Static state that would survive between Play sessions without a domain reload. |
| `cliext.playmode time_scale --value <n>` | Sets `Time.timeScale` (slow motion for captures) and returns the previous value. |
| `cliext.edits begin [--expire-after 300]` / `end` / `status` | An edit batch: no import or reload until `end`, which compiles once. |
| `cliext.test affected [--files "a;b"] [--since HEAD] [--all true]` | The test assemblies a change affects, with the commands that run them. |
| `cliext.test run [--filter f] [--filter-type name\|exact\|assembly\|category] [--mode edit\|play]` | Runs tests and returns that run's own result; an empty match fails. |
| `cliext.test result [--run-id id] [--wait true]` | A run's result (default: the latest). |
| `cliext.eval run --file f.cs [--vars "k=v;k=v"] [--print true]` | Runs a snippet with its `{{placeholders}}` filled. |
| `cliext.playmode wait_until --file f.cs [--vars ...] [--timeout 60]` | Runs a snippet every second until it returns `ready`. |
| `cliext.asset uses --path <asset> [--recursive true] [--include-editor-scripts true]` | What an asset references. |
| `cliext.asset used_by --path <asset> [--recursive true] [--include-packages true]` | What references an asset. |
| `cliext.diff snapshot --path <scene or prefab> [--name n]` | Saves a snapshot. |
| `cliext.diff compare --name n [--update true]` | The changes since the snapshot. |
| `cliext.diff list` | The saved snapshots. |
| `cliext.diff delete --name n` / `--all true` | Deletes a snapshot, or all of them. |
| `cliext.vfx types [--kind k] [--filter text]` | The contexts, blocks, operators and parameter types a VFX Graph can hold. |
| `cliext.vfx inspect --path <vfx> [--id id] [--details false]` | A graph's models with ids, settings, slot values and links. |
| `cliext.vfx create --path <vfx>` | Creates an empty VFX Graph. |
| `cliext.vfx add` / `remove` / `set` / `link` / `unlink` | One edit, then save and compile. |
| `cliext.vfx edit --path <vfx> --ops <json> \| --ops-file <path>` | Many edits, one save and compile. |
| `cliext.vfx check --path <vfx> [--strict true]` | Mistakes that compile but show nothing, VFX Graph's own warnings, and the attributes each event reads from script. |
| `cliext.shadergraph types [--filter text] [--targets true]` | The nodes by menu path, or the targets and materials. |
| `cliext.shadergraph inspect --path <graph> [--id node]` | Blocks, nodes, slots, properties and compile errors. |
| `cliext.shadergraph create --path <graph> --target T [--material M] \| --from <graph>` | Creates a graph. |
| `cliext.shadergraph add` / `add_property` / `remove` / `set` / `link` / `unlink` | One edit, then save and compile. |
| `cliext.shadergraph edit --path <graph> --ops-file <path>` | Many edits, one save and compile. |
| `cliext.input inspect --path <inputactions>` / `edit --ops-file <path>` | Read and edit maps, actions, bindings and schemes. |
| `cliext.input add_device` / `remove_device` / `devices` | Virtual input devices. |
| `cliext.input set` / `press` / `actions` | Drive controls and read the game's actions (Play mode). |
| `cliext.input record_start` / `record_stop` / `replay` / `replay_status` | Record and replay input. |
| `cliext.animator inspect` / `validate` / `edit --path <controller>` | Read, check and edit an AnimatorController. |
| `cliext.animator live` / `drive --object <GameObject>` | An Animator's live state, and driving it (Play mode). |
| `cliext.cinemachine list` / `create` / `set` / `blend` | Cinemachine cameras, the live camera and blends. |
| `cliext.timeline inspect` / `edit` / `bind` / `director` | Timeline clips, tracks, markers, bindings and scrubbing. |
| `cliext.uitk inspect` / `edit --path <uxml or uss>` | Read and edit UXML and USS, checked by the importer. |
| `cliext.uitk tree` / `query` / `click` / `set_value` | The live visual tree, clicking and setting values. |
| `cliext.splines list` / `inspect` / `create` / `edit` / `sample` | SplineContainers, knots, and sampling. |
| `cliext.addressables status` / `init` / `entries` / `edit` / `build` / `analyze` | Addressables groups, entries, builds and Analyze. |
| `cliext.import batch --folder <f> --set "k=v;..."` | Import settings across many assets. |
| `cliext.presets list` / `create` / `set_default` / `apply` / `audit` | Default presets per folder, and the assets that break them. |
| `cliext.anim takes` / `edit_clips --path <model>` | A model's takes and imported clips (loop, root motion, masks, events). |
| `cliext.anim clip` / `edit_clip --path <anim>` | Keys, events and settings on .anim clips. |
| `cliext.anim mask` / `check` | Avatar masks, and whether clips fit a model. |
| `cliext.physics matrix` / `set_matrix` / `material` / `raycast` | Collision matrix, physics materials, raycasts. |
| `cliext.navmesh settings` / `set` / `query` / `surfaces` / `bake` | NavMesh areas, agents, paths, surfaces and baking. |
| `cliext.quality levels` / `set` / `graphics` | Quality levels, platform defaults, graphics APIs. |

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
| `compile.json` | Editor, when an assembly finishes compiling | Each assembly's errors and warnings from its last compile. |
| `snapshots/*.json` | Editor, `cliext.diff snapshot` | Scene and prefab snapshots. |
| `watchdog.pid`, `watchdog.stop` | Watchdog, Editor | The running watchdog; a request to stop. |

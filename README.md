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
- [EditorDeferred](#editordeferred)
- [Waiting, compiling and fast Play mode](#waiting-compiling-and-fast-play-mode)
- [Edit batches, affected tests and deadlines](#edit-batches-affected-tests-and-deadlines)
- [Snippets, game states and screenshots](#snippets-game-states-and-screenshots)
- [Asset dependencies and scene diffs](#asset-dependencies-and-scene-diffs)
- [Editing VFX Graphs](#editing-vfx-graphs)
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
that runs each. The changes come from `--files a,b` or from git (uncommitted changes against `--since`, default `HEAD`, plus
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
Proven on VFX Graph 17.5; a later version that renames those internals fails to compile this one assembly only.

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
lists each op with the ids of the models it added. An op that fails stops the run with its index and the reason; the
ops before it are kept and saved. Contexts land under the lowest one and nodes to the left unless given x and y.

The VFX Graph window can stay open on the graph while these run; edits made with it open saved and compiled cleanly.

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
| `cliext.wait idle [--refresh true] [--timeout 120] [--settle-frames 3]` | Waits until the Editor is idle. |
| `cliext.compile run [--warnings true] [--timeout 300] [--async true] [--stop-play true]` | Refreshes, compiles, and returns structured errors and warnings. `--async true` answers at once and compiles after; `--stop-play true` leaves Play mode first. |
| `cliext.compile diagnostics [--warnings true]` | The standing errors and warnings, one record each. |
| `cliext.playmode set_fast --enabled true\|false` | Turns fast Play mode (no domain or scene reload) on or off. |
| `cliext.playmode check_statics [--include-reset true]` | Static state that would survive between Play sessions without a domain reload. |
| `cliext.edits begin [--expire-after 300]` / `end` / `status` | An edit batch: no import or reload until `end`, which compiles once. |
| `cliext.test affected [--files a,b] [--since HEAD] [--all true]` | The test assemblies a change affects, with the commands that run them. |
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

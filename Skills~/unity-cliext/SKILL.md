---
name: unity-cliext
description: Drive the open Unity Editor from the command line with Unity CLI Extensions (`unity command cliext.*`, on top of the Pipeline package). Use for any work in this Unity project that touches the running Editor - editing or compiling C# scripts, running tests, entering Play mode, waiting on imports or recompiles, reading compile errors, screenshots of the game, and editing scenes, prefabs, VFX Graphs, Shader Graphs, Input Actions, Animator controllers, animation clips and import, Timeline, Cinemachine, UI Toolkit (UXML, USS, the live UI), Splines, Addressables, import settings and presets, physics, NavMesh and quality settings. Also use when a `unity command` hangs, times out or a modal dialog blocks the Editor.
---

# Unity CLI Extensions

The Editor is already open with this project. Drive it with `unity command <group> <command> --format json`.
Pipeline's own groups (`scene`, `gameobject`, `asset`, `animation.*`, `settings.*`, `test`, `capture`, ...) do the
basics; the `cliext.*` groups add what they leave out. `unity command <group>` lists a group's commands and
`unity command <group> <command> --help` its parameters. Read a command's help before its first use.

Start with `unity command cliext status`: agent mode, the setup checks still open, the package `version`, and
`tools`, the folder holding the scripts named below (`cliext-wait.ps1`, `cliext-run.ps1`, `cliext-test.ps1`,
`cliext-play-capture.ps1`, `Watchdog/cliext-watchdog.ps1`). Run them with
`powershell -NoProfile -ExecutionPolicy Bypass -File <tools>/<script> -Project <project> ...`.

## The loop for code changes

1. Several files: `cliext.edits begin`, write them, then `cliext.edits end --async true` (one compile). One file:
   `cliext.compile run --async true` (add `--stop-play true` in Play mode).
2. Wait with `cliext-wait.ps1 -Timeout 300`. It rides out the domain reload, and exits 3 with the dialog when one
   blocks the Editor.
3. `cliext.compile diagnostics --warnings false`: the errors with file, line, column and code. Fix and repeat.
4. `cliext.test affected` (from git) or `cliext-test.ps1`, then `cliext.test run --filter <assembly> --filter-type
   assembly`. A Play Mode run returns a run id: wait, then `cliext.test result --wait true`.

## Rules

- Never sleep-poll a command. Use `cliext-wait.ps1`, `cliext.wait idle`, `cliext.test result --wait true`,
  `cliext.playmode wait_until --file ready.cs`, or a deadline with `cliext-run.ps1 -Timeout 60 -Command '...'`, which
  says what the Editor was doing when time ran out.
- A command that hangs is almost always a modal dialog. `Watchdog/cliext-watchdog.ps1 -Project <p> -Status` names it
  and its buttons; `-Click "<button>"` presses one. Ask the user before pressing anything that saves, deletes or
  rewrites files (save prompts, the API updater).
- Save or discard scene changes before tests, scene switches or builds (`cliext.scene save_open`): an unsaved scene
  stops them at a save prompt.
- "The Editor closed the connection" (exit 6) after a compile or Play mode change means a domain reload, not a
  failure: wait, then read the result. `--async true` avoids it for compiles.
- Batch edits (`edit --ops-file ops.json`) instead of one command per change. On Windows PowerShell 5.1 inline JSON
  loses its quotes, so write the ops to a file.
- Give `script eval` and `cliext.eval run` a `--timeout` when the work may take over 30 seconds.
- After every `cliext.vfx` edit, read `warnings` (a burst left at Count 0 spawns nothing) and `eventAttributes`: from
  script, send exactly those attribute names (`scaleX`/`scaleY`/`scaleZ`, never `scale`). `cliext.vfx check` runs the
  same checks on any graph.
- To see a short effect, capture it in slow motion with real commands rather than snippets:
  `cliext-play-capture.ps1 -TimeScale 0.005 -Run "<the command that triggers it>" a@0 b@1.5 c@3`.
- Leave the project as you found it: revert scratch assets, Play mode objects and setting changes you made to test.

## Command map

| Group | For |
|---|---|
| `cliext` | `status`, `set_agent_mode`; `cliext.setup check/apply/agent` (setup and these agent files) |
| `cliext.wait`, `cliext.compile`, `cliext.edits` | idle waits, compiles with errors as data, edit batches |
| `cliext.test` | `affected`, `run`, `result`: runs whose result is their own; an empty filter fails |
| `cliext.eval`, `cliext.playmode` | snippets with `{{placeholders}}`; `wait_until` a game state; `time_scale`; fast Play mode, `check_statics` |
| `cliext.dialogs`, `cliext.watchdog` | the dialog journal, a scan for blocking dialog calls, the out-of-process watchdog |
| `cliext.asset`, `cliext.diff` | `uses` / `used_by` dependencies; snapshot and compare scenes and prefabs |
| `cliext.vfx` | VFX Graph: `types`, `inspect`, `create`, `edit` (contexts, blocks, operators, parameters, links), `check` |
| `cliext.shadergraph` | Shader Graph: `types`, `inspect`, `create`, `edit`; every edit returns HLSL and node errors |
| `cliext.input` | .inputactions `inspect`/`edit`; virtual devices, `set`, `press`, `actions`, record and replay (Play mode) |
| `cliext.animator` | controllers: `inspect`, `validate`, `edit` (states, transitions, blend trees, layers); `live`, `drive` |
| `cliext.anim` | model takes and clip import, `.anim` keys and events, avatar masks, `check` clips against a model |
| `cliext.timeline` | `inspect`, `edit` (clips, timing, markers, tracks), `bind`, `director` (scrub, play) |
| `cliext.cinemachine` | `list` (live camera, blends), `create`, `set`, `blend` |
| `cliext.uitk` | UXML/USS `inspect`/`edit`; live `tree`, `query`, `click`, `set_value` |
| `cliext.splines` | `list`, `inspect`, `create`, `edit` knots, `sample` |
| `cliext.addressables` | `status`, `init`, `entries`, `edit`, `build`, `analyze` |
| `cliext.import`, `cliext.presets` | import settings across folders; default presets per folder and an `audit` |
| `cliext.physics`, `cliext.navmesh`, `cliext.quality` | collision matrix, materials, raycasts; NavMesh areas, agents, paths, bakes; quality levels, graphics APIs |

Scripts: `cliext-play-capture.ps1` compiles, enters Play mode, optionally slows time (`-TimeScale`), waits for a state,
runs the project's own commands (`-Run`), and screenshots it at timed moments (`NAME@SECONDS`) or after snippets into a
contact sheet. Each group's `--help` and the package README hold the details.

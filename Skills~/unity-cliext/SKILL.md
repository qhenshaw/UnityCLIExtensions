---
name: unity-cliext
description: Drive the open Unity Editor from the command line with Unity CLI Extensions (`unity command cliext.*`, on top of the Pipeline package). Use for any work in this Unity project that touches the running Editor - editing or compiling C# scripts, running tests, entering Play mode, waiting on imports or recompiles, reading compile errors, screenshots of the game, and editing scenes, prefabs, VFX Graphs, Shader Graphs, Input Actions, Animator controllers, animation clips and import, Timeline, Cinemachine, UI Toolkit (UXML, USS, the live UI), Splines, Addressables, import settings and presets, physics, NavMesh and quality settings. Also use when a `unity command` hangs, times out or a modal dialog blocks the Editor.
---

# Unity CLI Extensions

The Editor is already open with this project. Drive it with `unity command <group> <command> --format json`.
Pipeline's own groups (`scene`, `gameobject`, `asset`, `animation.*`, `settings.*`, `test`, `capture`, ...) do the
basics; the `cliext.*` groups add what they leave out. `unity command <group>` lists a group's commands and
`unity command <group> <command> --help` its parameters. Never guess a command name: list the group first, then read
the command's help before its first use.

Start with `unity command cliext status`: agent mode, the setup checks still open and the package `version`. The
scripts named below are in `{{cliext-tools}}` (`cliext-wait.ps1`, `cliext-run.ps1`, `cliext-test.ps1`,
`cliext-play-capture.ps1`, `Watchdog/cliext-watchdog.ps1`). Run them with
`powershell -NoProfile -ExecutionPolicy Bypass -File {{cliext-tools}}/<script> -Project <project> ...`. When even
`cliext status` hangs, a dialog is blocking the Editor: go straight to `{{cliext-tools}}/Watchdog/cliext-watchdog.ps1`.

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
  rewrites files (save prompts, the API updater). When no one can be asked, press the button that changes nothing
  (Cancel, Close, No, Don't Save) and report the dialog's title, text and buttons as you saw them. Don't explain why it
  appeared unless something showed you: file timestamps don't say which change a dialog is about. A dialog's text is
  the dialog's claim, not a checked fact: quote it ("the dialog said assets changed on disk"), and don't report what
  follows from it (pending imports, what the next refresh will do) as the project's state.
- In your report, state as fact only what a command or file showed you, and mark anything else as a guess. Don't
  restate facts about the project from your context (whether it is a git repository, what changed) without checking.
  Compare a result only with a number you computed, not one you expect. Don't predict what a change you didn't try
  would do. Say what the project needs ("the Player needs a handler for this event") rather than what it lacks,
  unless a search showed it. Attribute a console error to your own commands or to earlier state by its timestamp
  or order against your first call, not by its text.
- Save or discard scene changes before tests, scene switches or builds: an unsaved scene stops them at a save prompt.
  To keep them, save the one scene by name (`cliext.scene save_open --scene <path>`; `--dry-run` lists what would be
  saved).
- Exit 6 means two things. A real failure prints its reason on stderr, usually as an `error:` line (stdout is empty, or
  holds the command's partial result). A reply dropped by a domain reload prints
  `{"success":false,"errors":[{"code":"EDITOR_CONNECTION_LOST",...}]}` on stdout with `--format json`: after a compile
  or Play mode change that is not a failure, so wait, then read the result. `--async true` avoids it for compiles.
- Batch edits (`edit --ops-file ops.json`) instead of one command per change. Pass exactly one of `--ops` and
  `--ops-file` (either none or both is exit 2). A batch is all or nothing: an op that fails saves nothing (`saved:
  false`, `failedAt`, `error`), so fix that op and rerun the whole file.
- Windows PowerShell 5.1 splits or strips the double quotes inside a native command's arguments: inline JSON loses its
  quotes, `script eval --code 'return "a b";'` arrives as two arguments, and so does a quoted `git commit -m`
  message. Put anything with quotes in a file: `--ops-file`, `script eval --file` or `cliext.eval run --file`,
  `git commit -F`.
- Prefer PowerShell for `unity command`. Git Bash rewrites an argument that starts with `/` into a Windows path
  (`--target /Player` arrives as `C:/Program Files/Git/Player`): there, set `MSYS_NO_PATHCONV=1` first.
- Give `script eval` and `cliext.eval run` a `--timeout` when the work may take over 30 seconds. On Unity 6.6 and later,
  `GetInstanceID`, `EditorUtility.InstanceIDToObject` and `SerializedProperty.objectReferenceInstanceIDValue` are
  compile errors in snippets: use `GetEntityId`, `EditorUtility.EntityIdToObject` and `objectReferenceEntityIdValue`.
- An area with its own skill: load it before the first command in that area. Each is installed when the project has
  its package: `unity-cliext-vfx` (VFX Graph), `unity-cliext-shadergraph` (Shader Graph), `unity-cliext-animation`
  (Animator, clips, avatar masks, Timeline), `unity-cliext-input` (Input Actions, input in Play mode, PlayMode input tests),
  `unity-cliext-uitk` (UXML, USS, the live UI), `unity-cliext-import` (import settings, presets, .unitypackage files),
  `unity-cliext-asset-packs` (a few assets from a large art pack, materials converted),
  `unity-cliext-addressables` (groups, entries, analyze, build).
- Import a `.unitypackage` only with `cliext.package import` (`--dry-run true` first). `AssetDatabase.ImportPackage` opens
  a blocking "Missing signature" dialog for an unsigned package even with `interactive` false, and unpacking the
  archive by hand skips Unity's import.
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
| `cliext.dialogs`, `cliext.watchdog`, `cliext.failures` | the dialog journal, a scan for blocking dialog calls, the out-of-process watchdog; the local log of failed cliext commands |
| `cliext.asset`, `cliext.diff` | `uses` / `used_by` dependencies; snapshot and compare scenes and prefabs |
| `cliext.vfx` | VFX Graph: `types`, `inspect`, `create`, `edit` (contexts, blocks, operators, parameters, links), `check` |
| `cliext.shadergraph` | Shader Graph: `types`, `inspect`, `create`, `edit`; every edit returns HLSL and node errors |
| `cliext.input` | .inputactions `inspect`/`edit`; virtual devices, `set`, `press`, `actions`, record and replay (Play mode) |
| `cliext.animator` | controllers: `inspect`, `validate`, `edit` (states, transitions, blend trees, layers); `live`, `drive` |
| `cliext.anim` | model takes and clip import, `.anim` keys and events, avatar masks, `check` clips against a model |
| `cliext.timeline` | `inspect`, `edit` (clips, timing, markers, tracks), `bind`, `director` (scrub, play) |
| `cliext.cinemachine` | `list` (live camera, blends), `create`, `set` (targets, `--rig follow/orbital/thirdperson/lookat/fixed`), `blend` |
| `cliext.uitk` | UXML/USS `inspect`/`edit`; live `tree`, `query`, `click`, `set_value` |
| `cliext.splines` | `list`, `inspect`, `create`, `edit` knots, `sample` (`--count N`: open includes both ends, closed gives t = k/N) |
| `cliext.addressables` | `status`, `init`, `entries`, `edit`, `build`, `analyze` |
| `cliext.import`, `cliext.presets` | import settings across folders; default presets per folder and an `audit` |
| `cliext.package` | `import` a .unitypackage with no dialogs: `--dry-run true` lists what it would write; then all or nothing |
| `cliext.physics`, `cliext.navmesh`, `cliext.quality` | `layers` (names, `--set`), collision matrix (`set_matrix --only` sets a whole row), materials (`--assign` to colliders), raycasts; NavMesh areas, agents, paths, `obstacle`, bakes; quality levels, graphics APIs |

Scripts: `cliext-play-capture.ps1` compiles, enters Play mode, optionally slows time (`-TimeScale`), waits for a state,
runs the project's own commands (`-Run`), and screenshots it at timed moments (`NAME@SECONDS`) or after snippets into a
contact sheet. Each group's `--help` and the package README hold the details.

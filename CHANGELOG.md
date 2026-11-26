# Changelog
All notable changes to Unity CLI Extensions. Versions are git tags: install one with `https://github.com/qhenshaw/UnityCLIExtensions.git#<version>`.

## 0.23.0
- Added the `unity-cliext-vfx` skill for building VFX Graph effects with `cliext.vfx`: the edit-check loop, block keys and slot names for VFX Graph 17.6, the defaults that spawn nothing (burst Count 0, Blend 0, a fully random direction blend), the C# side (exact event attribute names, one OnPlay burst per VisualEffect per frame) and slow-motion captures. It comes with ops templates (`ops/burst.json`, `ops/sparks.json`, `ops/streak-from-source.json`) that apply cleanly and pass `cliext.vfx check` once their texture path is set, alone or combined under one Event.
- The agent files install every skill in `Skills~/` that applies to the project: `unity-cliext` always, `unity-cliext-vfx` only when VFX Graph is installed (and it is taken out again if VFX Graph is removed). Each installed SKILL.md carries its own version stamp; the stamp earlier versions wrote is still recognised. The CLAUDE.md block tells agents to load the VFX skill before VFX Graph work when it is installed.
- Added EditMode tests for which skills apply and for reading the version stamp.

## 0.22.0
- `cliext.vfx` checks every graph it edits or inspects for mistakes that compile cleanly but show nothing, and returns them as `warnings` (severity, model id, where, problem): a Single or Periodic Burst left at its default Count of 0, a spawner with no burst or rate block, an Initialize with capacity 0, a flipbook output with no `mainTexture` or a 1x1 grid, plus VFX Graph's own warnings (the badges the graph window shows).
- Every `cliext.vfx` edit and `inspect` also return `eventAttributes`: for each Event context, the attributes its systems read from the event (`Set ... From Source` in Initialize), minus those a spawner sets itself. Variadic attributes are listed per channel, so script code knows to send `scaleX`, `scaleY` and `scaleZ` rather than `scale`, which an event silently ignores.
- Added `cliext.vfx check --path <vfx> [--strict true]`, the same checks on their own; exit 6 on errors (and on warnings with `--strict`).
- Added `cliext.playmode time_scale --value <n>`, which sets `Time.timeScale` and returns the previous value.
- `cliext-play-capture.ps1` captures short effects without C# snippets: `-TimeScale` slows the game down before anything is triggered (and puts the previous value back at the end), `-Run` runs `unity command` lines in order after `-Until` (a project's own debug menu commands, for example), and shots written `NAME@SECONDS` are captured that many real seconds after the last `-Run` step, with the real moment each was taken in the result.
- Fixed `cliext-play-capture.ps1` failing at `-Until` (or a snippet shot) whose spec has no `|key=value` part: the empty `--vars` it passed was dropped by Windows PowerShell 5.1, leaving `--vars` with no value. `--vars` is now passed only when there are vars.
- Added EditMode tests for the VFX checks (in a test assembly built only with VFX Graph) and for `time_scale`.

## 0.21.0
- Unity's "Missing signature" dialog, which stops every import of an unsigned .unitypackage on Unity 6.3 and later, is answered with Import Anyway (or Reimport Anyway) at all times, with or without agent mode. `MissingSignatureAnswer` presses the dialog's own UI Toolkit button from `EditorWindow.windowFocusChanged` as it opens, without moving the mouse, and writes each answer to the dialog journal and the Console. The trust dialogs for invalid or untrusted signatures are left alone. Verified on 6000.6.6f1, with the Editor in the background.
- Added the dialog to the blocking dialogs catalog.

## 0.20.1
- `cliext.vfx` compiles with VFX Graph 17.6, which removed `VisualEffectResource.GetOrCreateGraph`: a version define (`CLIEXT_VFX_GRAPH_17_6_OR_NEWER`) switches to `GetGraph`, then `CreateGraph` when the resource has no graph. Before this, the compile error in the VFX assembly stopped the whole package from loading in projects on VFX Graph 17.6.

## 0.20.0
- Added the `unity-cliext` Claude Code skill (`Skills~/unity-cliext/SKILL.md`), and its automatic install: when the package loads it copies the skill into `.claude/skills/unity-cliext/` and keeps a marked block in `CLAUDE.md` (created if missing, the rest untouched), writing only what differs from the package version. Also `cliext.setup agent [--force] [--remove]`, a menu item, and a setting to turn it off.
- `cliext status` reports the package version and the `Tools~` folder.

## 0.19.0
Fixes from an end-to-end pass over every command set in a live HDRP project:
- Watchdog: the "Package Manager" progress window ("Resolving packages...") counts as busy instead of a blocking error, so `cliext-wait.ps1` no longer stops on it; only "Package Manager Error" escalates. Added a rule that answers Addressables' "Enable Build Report" prompt with Cancel.
- `cliext.vfx`, `cliext.shadergraph`, `cliext.input` and `cliext.animator` `edit` report a missing or unreadable `--ops-file` as an error instead of an exception.
- Added tests for `ValueText`, `TemplatedEval` and the UI Toolkit selector (44 tests pass).

## 0.18.0
- Added `cliext.physics` (layer collision matrix for 3D and 2D, physics materials, raycasts), `cliext.navmesh` (areas and agent types, path and nearest-point queries, and with AI Navigation 2 NavMeshSurface listing and baking without orphaned data) and `cliext.quality` (every quality level's settings, platform exclusions and defaults, graphics APIs and render pipelines per level).
- Added `SerializedText`, for settings assets read and written through SerializedObject.

## 0.17.0
- Added `cliext.anim`: `takes` and `clips` for a model's animation import (takes, clip ranges, loop, root motion, mirror, masks, events), `clip` and `edit_clip` for keys, events and settings on .anim clips, `mask` for avatar masks (body parts and transforms), and `check`, which says whether clips fit a model (humanoid and generic mismatches, invalid avatars, missing paths and components, legacy clips).

## 0.16.0
- Added `cliext.import batch`: import settings across every matching asset in a folder (by type and file name), texture platform overrides, only where they differ, with a dry run.
- Added `cliext.presets`: `list`, `create` (from an asset's importer), `set_default` (per folder or any Preset Manager filter), `apply`, and `audit`, which lists every asset that breaks the default preset that applies to it, setting by setting.

## 0.15.0
- Added `cliext.addressables`: `status`, `init`, `entries`, `edit` (groups and their packing and compression, entries, addresses, labels), `build` (content build, with the first build's report prompt answered so it doesn't block) and `analyze` (the Analyze rules' results as data). Built when the Addressables package is installed; tested with Addressables 5.0.

## 0.14.0
- Added `cliext.splines`: `list`, `inspect` (knots with local and world positions, tangents, modes), `create`, `edit` (add, set and remove knots, closed, tangent modes, add and remove splines) and `sample` (at t or a distance, evenly spaced, or the nearest point to a world position). Built when the Splines package is installed.

## 0.13.0
- Added `cliext.uitk`: `inspect` and `edit` for UXML (add, remove, move, attributes and classes, by USS-like selectors) and USS (set, remove rules and properties), reimported and checked by Unity's importer; and on the live UI `tree`, `query`, `click` and `set_value`.

## 0.12.0
- Added `cliext.timeline`, for what Pipeline's animation.timeline commands leave out: `inspect` (clip timing, ease and blend, markers, bindings), `edit` (add clips including a model's takes, clip timing and easing, move and remove clips and tracks, rename, mute, lock, markers and signals), `bind` (track bindings on a PlayableDirector) and `director` (scrub, play, pause, stop, read).

## 0.11.0
- Added `cliext.cinemachine` (Cinemachine 3): `list` (cameras with priority, targets, lens and rig, brains with the live camera and the blend in progress), `create` (a camera with a follow, orbital, third-person, look-at or fixed rig, adding a brain when needed), `set` (priority, targets, field of view, enabled) and `blend` (default blend, or custom blends between cameras).

## 0.10.0
- Added `cliext.animator`, for what Pipeline's animation.animator commands leave out: `inspect` (blend trees, transition settings, layers, sub-state machines), `validate` (unreachable states, transitions that can never fire, conditions and blend trees on missing or mistyped parameters, unused parameters), `edit` (remove and rename states, state and transition settings, conditions, parameters, layers, blend trees, sub-state machines), and in Play mode `live` (current states, transitions and parameter values) and `drive` (set parameters, cross-fade to a state).

## 0.9.0
- Added `cliext.input`: `inspect` and `edit` for .inputactions assets (maps, actions, bindings, composites, control schemes, with unknown device layouts reported), and in Play mode `add_device` / `remove_device` / `devices` for virtual devices, `set` and `press` to drive their controls (each event carries every held control), `actions` with the value each action had as the game saw it, and `record_start` / `record_stop` / `replay` / `replay_status` for input traces. Virtual devices and replays are removed when Play mode ends. Built when the Input System package is installed.

## 0.8.0
- Added `cliext.shadergraph` for reading and editing Shader Graphs: `types` (nodes by menu path, targets and materials), `inspect` (blocks, nodes with settings and slots, properties, compile errors), `create` (for a target and material, or a copy), `add`, `add_property`, `remove`, `set` (slot values, node settings such as a Custom Function's body, property defaults), `link` / `unlink` (type-checked; a property links through a new property node), and `edit` with ops including `add_block` and Custom Function `add_port` / `remove_port`. Every edit compiles each shader pass and returns HLSL errors and node errors as data. Refuses to edit a graph open in a Shader Graph window. Built only when the Shader Graph package is installed.
- `cliext.compile run --async true` marks the compile pending until it finishes, and `Tools~/cliext-wait.ps1` waits for that mark: before, the wait could return "idle" in the moment before the compile started.
- Added `ValueText`, the shared parser and formatter for values given as text.

## 0.7.0
- Added `cliext.test run` and `cliext.test result`: test runs that record their own result, so the previous run's status is never mistaken for the new one's, and a filter that matches nothing fails with the closest names instead of passing. Filters by part of the full name, exact name, assembly or category, in Edit or Play Mode; failures come back with their messages. Refuses to start while a scene has unsaved changes. Built when the Test Framework package is installed. `cliext.test affected` and `Tools~/cliext-test.ps1` now use it.
- Added `cliext.eval run`: runs a C# snippet file through Pipeline's script eval after filling its `{{key}}` placeholders from `--vars`; an unfilled placeholder is an error.
- Added `cliext.playmode wait_until`: runs a snippet every second until it returns `ready`, to wait for a game state.
- `cliext.compile run --stop-play true` leaves Play mode before compiling.
- Added `Tools~/cliext-play-capture.ps1`: compile, enter Play mode, wait until ready, run a setup snippet and capture the game view for each shot, leave Play mode, and tile the shots into a contact sheet (no Python needed).

## 0.6.0
- Added `cliext.vfx` for reading and editing VFX Graphs: `types` (the library of contexts, blocks, operators and parameter types), `inspect` (every model with a stable id, settings, slot values and links), `create`, `add`, `remove`, `set` (settings, slot values and spaces, parameter defaults), `link` / `unlink` (flow and data), and `edit`, which applies a JSON list of ops with one save and compile. Built only when the Visual Effect Graph package is installed, in an assembly named `Unity.Testing.VisualEffectGraph.Editor` so it can use VFX Graph's editor internals.

## 0.5.0
- `Tools~/cliext-run.ps1` and `Tools~/cliext-wait.ps1` find a blocking dialog without the watchdog running: when its report is missing or stale, they take a watchdog snapshot themselves. Before, a save prompt with no watchdog running showed as `main-thread-busy`, and `cliext-wait.ps1` waited out its timeout.
- `cliext.playmode set_fast --enabled false` puts back the Enter Play Mode settings the project had before fast Play mode was turned on (`restoredPrevious` in the result), instead of always Unity's default. With fast Play mode off, it changes nothing.
- Turning agent mode off stops the watchdog that agent mode started. A watchdog started by hand keeps running.
- `cliext.compile run` and `cliext.edits end` take `--async true`: they answer at once (exit 0) and compile after the reply, so the exit code no longer depends on the domain reload dropping it.
- `Tools~/cliext-test.ps1` refuses to start while an open scene has unsaved changes (the Test Runner would stop at a save prompt), and reopens the scenes that were open before when a run leaves the Editor in another scene (`restoredScenes`).
- `cliext.asset uses` leaves out Editor-only scripts, such as the AssetVersion script HDRP attaches to every material; `--include-editor-scripts true` lists them.
- Added `cliext.diff delete --name n` and `--all true`.

## 0.4.0
- Added edit batches: `cliext.edits begin` stops auto-refresh and locks assembly reloads so several files compile once at `cliext.edits end`, which compiles and answers like `cliext.compile run`. A batch that is never ended ends itself after `--expire-after` seconds, and survives domain reloads.
- Added `cliext.test affected` (changed scripts and .asmdef files mapped to the test assemblies that depend on them, from `--files` or git) and `Tools~/cliext-test.ps1`, which runs them (Edit Mode synchronously, Play Mode asynchronously) and prints one summary. `-All` runs every test assembly.
- Added `Tools~/cliext-run.ps1`: runs a `unity command` with a deadline and, when it passes, says what the Editor was doing (dialog, Safe Mode, hung, compiling, importing, busy, idle).
- Added `cliext.asset uses` and `cliext.asset used_by` (direct or recursive), for prefabs, scenes, materials, scripts, ScriptableObjects and other assets.
- Added scene and prefab diffs: `cliext.diff snapshot`, `compare` (objects added, removed or renamed, components added or removed, properties old to new) and `list`.
- `EditorDeferred.Run` has an overload that skips the timeout warning, for timers.
- Watchdog: recognises an Editor in Safe Mode (`state: safe-mode`); `cliext-wait.ps1` exits 5 for it. Documented that `-automated` enters Safe Mode without asking when scripts have compile errors.

## 0.3.0
- Added `cliext.wait idle`: waits until the Editor isn't compiling, importing or changing Play mode, optionally after a refresh, and returns the standing compile errors. `Tools~/cliext-wait.ps1` waits from outside the Editor, across domain reloads, and returns at once when a dialog blocks the Editor.
- Added `cliext.compile run` and `cliext.compile diagnostics`: compile errors and warnings with file, line, column, code and assembly, kept per assembly in `Library/CLIExtensions/compile.json` so they survive domain reloads and stay readable when nothing recompiled. Fills what Pipeline's `script recompile_status` leaves out.
- Added fast Play mode: `cliext.playmode set_fast` (Enter Play Mode Options without domain and scene reload) and `cliext.playmode check_statics`, which lists static fields, auto-properties and events in the project's runtime code that have no `[RuntimeInitializeOnLoadMethod]` reset. The `enter-play-mode-options` setup check reports the same count.
- The heartbeat now carries `compileFailed`.
- Watchdog: a native window with no buttons (Unity's "Reloading Domain" and other progress windows) counts as busy, not as blocking; the report's `state` is `busy` while only such windows are open.

## 0.2.0
- Added `EditorDeferred`: a replacement for `EditorApplication.delayCall`, whose callbacks can wait minutes in an unfocused Editor driven from the command line. `Run` (with an optional condition, timeout, timeout callback and `Cancel`) and `RunWhenIdle` run the work from `EditorApplication.update`, nudge the Editor with `QueuePlayerLoopUpdate`, and log a warning when the timeout passes.
- The watchdog auto-start and the scene reload after an external change now use `EditorDeferred`; the reload waits until the import has finished.

## 0.1.0
- First version: keeps an Editor driven by `unity command` free of blocking dialogs.
- Agent mode, on with `-automated`, `-cliext-agent`, `CLIEXT_AGENT=1`, batch mode or the per-project setting: saves scenes before Play, keeps Play mode running unfocused, pre-answers the Input System restart prompt, reloads scenes changed on disk under `-automated`, and starts the watchdog.
- `AgentDialog` and `AgentProgress`: non-blocking replacements for `EditorUtility.DisplayDialog`, `DisplayDialogComplex` and the progress bars, with the answers logged to `Library/CLIExtensions/dialogs.jsonl`.
- Setup checks (`launch-flags`, `tmp-essentials`, `input-handling`, `hdrp-wizard`, `interaction-mode`, optional `enter-play-mode-options`) in Project Settings > CLI Extensions and `cliext.setup check` / `apply`.
- Watchdog (`Tools~/Watchdog/cliext-watchdog.ps1`, Windows): finds the Editor's modal dialogs with Win32, reports them in `Library/CLIExtensions/watchdog-status.json`, presses the safe default for known dialogs, escalates the rest, and notices crashes and hangs.
- Commands: `cliext status`, `cliext set_agent_mode`, `cliext.setup check|apply`, `cliext.dialogs log|scan|clear_log`, `cliext.watchdog start|stop|report`, `cliext.package import`, `cliext.scene save_open`.
- A catalog of the dialogs Unity 6 is known to block on, in `Documentation~/blocking-dialogs.md`.

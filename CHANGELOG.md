# Changelog
All notable changes to Unity CLI Extensions. Versions are git tags: install one with `https://github.com/qhenshaw/UnityCLIExtensions.git#<version>`.

## 0.31.0
Field notes, the field report and a one-step bug report, for problems found in real projects. 210 EditMode tests and 152 `cliext-clitest.ps1` checks pass.
- **Field notes.** New `cliext.failures note --kind bug|workaround|missing|docs|slow --what "<one line>" [--command <cmd>] [--workaround "<one line>"]`, or `--file note.json` with the same fields (for Windows PowerShell 5.1). It appends one line to `Library/CLIExtensions/field-notes.jsonl` with the time and the Unity, Pipeline and package versions, and no parameter values. An unknown kind exits 2 and lists the kinds; an unreadable `--file` exits 13. `cliext.failures log --notes true` lists the notes, `summary` adds them grouped by kind and command (each record now has `source`: `failure` or `note`), and `clear --notes true` deletes them (`clear` alone still deletes only the failure log and now names the file it cleared).
- The CLAUDE.md block and the `unity-cliext` skill tell agents to record a field note before moving on when a cliext command fails in a way its error doesn't explain, when they work around one (hand-editing a file a cliext command edits, a `script eval` where a command should exist, reading the package source or README), or when a command they need doesn't exist.
- **Field report.** New `Tools~/cliext-field-report.ps1 -Project <path> [-Days 14] [-Out <dir>]` reads the project's Claude Code transcripts (sessions and subagents), the failure log and the field notes, and writes `field-report.md` and `.json`: failed tool calls by cause (as T3's `score.py` classifies them), reads of the package's source or README, hand edits of files cliext commands edit, repeated `--help`, `script eval` and `cliext.eval` calls with excerpts, whether each area's skill was loaded before its first call, and each note and failure matched to the tool call that made it, with the calls around it. About 6 s on 160 transcripts.
- **Bug report.** New `Tools~/cliext-report.ps1 -Project <path> [-Days 14] [-FullTranscripts] [-NoEditor]` zips the field report, the failure log, field notes, dialog journal, watchdog status, versions, manifest and lock file, installed skills' stamps and edits, the CLAUDE.md block, the end of Editor.log, and the Editor's answers when it answers within a few seconds, into `<project>/CLIExtensionsReports/cliext-report-<time>.zip` with a README.txt, then opens Explorer at it. It reads files directly, so it works with the Editor hung or closed; nothing is uploaded. Tools > CLI Extensions > Create Bug Report and `cliext.failures report` start it out of process. README: "Field notes and the field report" and "Reporting a problem".
- `cliext-clitest.ps1`: new checks `failures.note`, `failures.field-report`, `failures.report-zip` and `failures.report-command`; `uitk.edit` ignores class order.
- The watchdog starts when `watchdog.pid` is empty (left by a watchdog killed mid-write); before, it exited on a null error and `cliext.watchdog start` reported starting it.

## 0.30.0
Fixes from the T3 agent runs on 0.29.0, and area skills. 204 EditMode tests and 148 `cliext-clitest.ps1` checks pass.
- `cliext-clitest.ps1`'s `xcut.skill-freshness` ignores the version stamp of every installed skill (it used to flag `clarify`).
- **Area skills.** The installer now also installs `unity-cliext-shadergraph`, `-animation`, `-input`, `-uitk` and `-addressables` (each only when its package is in the project), `-import`, `-asset-packs` and `clarify` (always), with ops files, snippets and templates; the CLAUDE.md block says when to load each. `unity-cliext-input` carries PlayMode input test templates; `unity-cliext-asset-packs` indexes and extracts only the needed assets from a large .unitypackage and converts its materials.
- **Shader Graph:** new `edit` ops `set_target` (alpha clipping, surface type and other Graph Settings, adding the blocks they use, so Alpha Clip Threshold is offered) and `set_mode` (slider with a range, integer, HDR colour). `inspect` shows target settings and property modes.
- **Timeline:** an Edit-mode `cliext.timeline director --time` scrub puts the bound objects back and leaves the scene clean (`--keep-pose true` keeps the old behaviour); the reply lists each track's active clip; `--read` returns named objects' pose at that time.
- **Clip references:** a bare model path with several clips, or a clip name two clips share, fails with exit 2 listing the clips and their lengths, in `cliext.animator`, `cliext.anim` and `cliext.timeline add_clip`.
- **Folders:** `cliext.anim mask`, `cliext.vfx create` and `cliext.shadergraph create` make missing folders, and a folder made on disk but not imported yet no longer gets a stray `<Folder> 1` beside it.
- **Input:** `cliext.input edit` warns when a control is already bound to another action, and when a binding's groups match no control scheme.
- **UI Toolkit:** `set_value` returns `matches` like `click`; both warn when the selector matches more than one element.
- `cliext-play-capture.ps1 -Run "a","b"` works through `powershell -File` (steps split at `;;` or a comma before a command); new `-DryRun`.
- `cliext.presets set_default` now saves `ProjectSettings/PresetManager.asset`; before, the default lived only in the open Editor. A clean `audit` prints a summary record (`audited`, `presets`, `breaking: 0`, `note`); `list` with no defaults prints `{defaults: 0, note}`.
- `cliext.presets create --only "<properties>" / --exclude "<properties>"` trim the preset through its excluded properties; the platform settings stay whole unless `--exclude` names `m_PlatformSettings`.
- New `cliext.physics layers [--set "8=Player;9=Pickup"]`; `set_matrix --only "<layers>"` sets a whole row; `matrix` shows `unnamedIgnored` and takes `--all true`; `material --assign <object>` puts the material on its colliders.
- New `cliext.navmesh obstacle` (box or capsule, size, center, carve, `--remove`).
- `cliext.cinemachine set --rig follow|orbital|thirdperson|lookat|fixed`; `set` warns when a follow or look-at target can't move a camera that has no body or aim component.
- `cliext.splines sample --count N` on a closed spline returns t = k/N (N different points); the help says what open and closed return.
- `cliext.addressables analyze` no longer counts a "No issues found" result (severity None) as an issue.
- Watchdog: the Missing signature dialog is brought to the front so the package answers it (rule `missing-signature`, action `focus`; `-Click "Import Anyway"` by hand). `-Status` adds a `note` on Unity-drawn modals, naming `cliext.package import` for this one. `-Click` acts on the blocking dialog before a progress window.

## 0.29.0
T1/T2 for the nine systems groups (Cinemachine, Splines, NavMesh, Physics, Quality, import and presets, package import, asset dependencies and diffs, Addressables). `cliext-clitest.ps1` now calls every cliext command in the plan's 21 rows, with failure modes, and 171 EditMode tests pass.
- **`cliext.package import` no longer opens a dialog.** Unity's non-interactive `ImportPackage` still showed the "Missing signature" modal and blocked the Editor. The package now reads the .unitypackage itself (gzip tar), checks everything first (refusing paths outside Assets/ or an existing Packages/ folder), writes files at their GUID's path, undoes a write that fails partway, and refreshes synchronously. New `--dry-run`; the reply lists created, replaced and unchanged paths; a package already imported exits 6. The obsolete `ImportPackage` call in setup uses `AssetPackage.Package.Import` on 6.6.
- **Half-applied changes fixed:** `cliext.cinemachine create`/`set` and `cliext.splines create` resolve every argument before making anything; `cliext.import batch` and `cliext.presets apply` check every asset first and put the .meta files back if applying fails; `cliext.physics material` checks `--set` before creating the asset; `cliext.addressables edit` checks its ops first and rolls back fully (AssetRollback now unloads watched assets after restoring, so a postprocessor that reloaded mid-restore can't keep a removed group).
- **Bugs:** `cliext.quality set antiAliasing=2` set 4x (numbers were read as an index into the names); `cliext.navmesh bake` in an unsaved scene wrote its data into Assets/; `cliext.diff compare` reported changes nobody made on a camera with a CinemachineBrain, and about 60 changes for one parent rename; `cliext.asset used_by` kept package assets after one `--include-packages` call; `cliext.addressables analyze` (read-only) rewrote the URP pipeline asset and ran base classes as rules; `build`/`analyze` with an unsaved scene would stop at a save dialog (now exit 6); a misspelled `--platform` in `cliext.import batch` wrote a bogus override; `presets set_default` dropped the preset's other filters.
- **Silent results now fail:** a raycast that hits nothing (13), a NavMesh query off the mesh or partial (13/6), `entries` with an unknown group or label (13), `levels --level` unknown (2), `splines sample` out of range (2, it used to clamp), batches with unknown settings or values (2, they used to skip assets and exit 0), and every set that would change nothing (6).
- `cliext.uitk edit` keeps a file's line endings: on a CRLF file (usual on Windows) every edit used to rewrite all line endings to LF, and an op that changed nothing was saved as a change.
- `cliext.cinemachine list` in Edit mode no longer reports the brain's live camera as current (it notes that live and blending need Play mode). `cliext.navmesh settings` shows drop and jump, and `set --name none` clears an area's name.
- The same exit codes and one-line, "Did you mean" errors as the authoring groups; every command's help gives its exit codes.

## 0.28.0
T1/T2 for the six authoring groups (VFX Graph, Shader Graph, Animator and animation, Timeline, Input, UI Toolkit). `cliext-clitest.ps1` now calls all 53 of their commands, with failure modes, and 134 EditMode tests pass.
- **A failed batch no longer comes back on the next edit.** In VFX Graph (`remove`), Animator controllers (`remove_state`), animation clips and Timeline (`remove_track`, `remove_clip`), the file was rolled back but the in-memory asset kept the structural change, so the next good edit saved it. Rollback now discards the VFX resource's cached graph (`VisualEffectResource.ForgetAtPath`), and reverts an Undo group recorded at the start of the batch for controllers, clips and timelines (`AssetRollback` `revertUndo`), destroying sub-assets the batch added without Undo. Shader Graph, Input actions and UI Toolkit edit a copy and write only on success; UI Toolkit also puts the file back when the import logs a new error or warning.
- **Bugs:** `cliext.input press`/`set` with a full control path (`<Keyboard>/space`) always failed; `cliext.uitk edit` `remove` on the root emptied the file; `cliext.timeline bind` to an object without the track's component cleared the binding; `cliext.input edit set_action` gave the action a new id (breaking references) and moved it; `cliext.uitk click` on an element with no handler, `set_value` on a Button, out-of-range slider and unknown dropdown values did nothing or the wrong thing with exit 0; `cliext.anim check` with a missing clip checked nothing and exited 0; `cliext.input record_stop` saved an empty trace.
- **Play mode:** commands that only work in Play mode (`cliext.animator live`/`drive`, `cliext.timeline director play`/`pause`, `cliext.input add_device`/`set`/`press`/`record_start`/`replay`, `cliext.uitk click`/`set_value`) exit 6 naming `editor.playmode start` instead of running and doing nothing.
- **Exit codes, the same across groups:** a missing asset is 13 and names how to make one; a wrong-type asset or a path outside Assets/ or Packages/ is 2; an existing path on create is 6 and says to use edit; an unknown kind or enum is 2 and lists the valid ones; an unknown name points at `inspect` (or `tree`, `devices`) with "Did you mean" (now also for two swapped letters).
- **Ops that would change nothing fail** instead of saving a no-op (renames to the same or no name, sets with no value, unlinking a link that isn't there, removing something that isn't there, and so on).
- **Errors are one line:** bad numbers, bools, colours and enums no longer print a `FormatException` stack trace (shared `ValueText`), and enum errors list the names.
- Help text for every group now says what each op needs, which commands need Play mode, and the exit codes.

## 0.27.0
- Local failure log: every cliext command that reports an error (or throws) appends a line to `Library/CLIExtensions/failures.jsonl` with the command, the parameter names it was given (no values), the error, the package's own stack frames, and the Unity, Pipeline and package versions, OS and agent mode. Nothing leaves the machine. Read it with `cliext.failures log` and `cliext.failures summary` (grouped by command, exception and top frame); delete it with `cliext.failures clear`. Commands report errors through one extension, `cmd.Fail(...)`, which finds the failing command from the stack (async commands and lambdas included); a package test forbids `cmd.Log.Error` elsewhere.
- `cliext.setup check` and `apply` with an unknown `--ids` value exit 2 (invalid usage) and list the known ids, instead of failing with an uncaught exception.
- The installed skills name the package's `Tools~` folder by its absolute path (the installer fills `{{cliext-tools}}`), so an agent can reach `Watchdog/cliext-watchdog.ps1` when a dialog blocks every command, `cliext status` included. In 3 of 3 agent runs on 0.26.0 that took 3 to 5 calls of searching.
- `cliext-clitest.ps1` covers every P1 command (26 of 26) and their failure modes: `set_agent_mode`, `setup apply` (idempotent) and unknown ids, `test result` (latest, wait, unknown id), `dialogs clear_log`, a real blocking modal dialog (a main-thread command reports it, the watchdog names it and presses Cancel, the Editor answers again), `set_fast` (settings put back), `wait_until` (exact `ready`, and the near-ready timeout hint), a snippet compile error, `edits end` without a batch, and `test run` refusing an unsaved scene with the exact `save_open --scene` command. 35 checks.

## 0.26.0
- `cliext.scene save_open` takes `--scene <path>` to save one open scene and `--dry-run true` to list what it would save without saving. It now returns the saved scene paths (`saved`, or `wouldSave` on a dry run) instead of a count, and `untitledSkipped`. An unknown `--scene` is exit 13 with the open scenes listed. Its Effect is now Destructive (it overwrites scene files with in-memory changes).
- `cliext.test run` and `cliext-test.ps1`, refusing an unsaved scene, name the exact command that keeps the changes (`cliext.scene save_open --scene <path>`).
- `cliext.playmode wait_until` says that only the exact value `ready` ends the wait, in its help and, when the last value starts with `ready`, in its timeout message (an agent's snippet returned `ready t=…` and waited out the timeout three times).
- The `unity-cliext` skill: a dialog's text is the dialog's claim, to quote rather than report as the project's state (the one agent failure in 18 runs on 0.25.0); keep scene changes by saving that one scene with `--scene`.

## 0.25.0
- The `unity-cliext` skill covers what the first agent baseline (18 runs on 0.24.0) got wrong: when a dialog blocks the Editor and no one can be asked, press the button that changes nothing and report the dialog as seen, without inferring why it appeared from file timestamps (0 of 3 runs passed this scenario); state as fact only what a command or file showed, not what the agent's context says about the project; and never guess a command name, list the group first.
- `cliext-clitest.ps1` reports T1 coverage after its checks (for each core-loop group, the commands the run called and the ones it didn't), and `-T3Ready` checks the environment for agent scenario runs: setup checks applied, agent mode on, the watchdog running, the installed skills current. Its watchdog check leaves the watchdog running when it was running before.

## 0.24.0
- Every batch edit reads its ops the same way, before it looks at its target: exactly one of `--ops` and `--ops-file`, else exit 2 (invalid usage), and invalid JSON or an empty list is exit 2 too. Before, giving neither exited 6 or 13 depending on the command, and giving both silently used the file.
- Added `Tools~/cliext-clitest.ps1`, out-of-process tests that drive a running Editor through `unity command`: setup, compile errors and edit batches, test runs, the dialog journal and the watchdog, snippets with placeholders, exit codes (a dropped reply versus a failure), reply shapes, misspelled and either/or parameters, skill freshness, and Windows PowerShell 5.1 quoting. 23 checks; all pass on Unity 6000.6.6f1.
- The `unity-cliext` skill tells exit 6 from a failure and from a reply dropped by a domain reload (`EDITOR_CONNECTION_LOST` on stdout), says batch edits are all or nothing and take exactly one of `--ops` and `--ops-file`, says to put anything with double quotes in a file under Windows PowerShell 5.1 (`--ops-file`, `script eval --file`, `git commit -F`), and names the Unity 6.6 replacements for `GetInstanceID` and `InstanceIDToObject` in snippets.
- Every batch edit is all-or-nothing: an op that fails saves nothing, and the result says `saved: false` with `failedAt` and `error`. `cliext.vfx edit` (and the single `cliext.vfx` edit commands), `cliext.animator edit`, `cliext.timeline edit`, `cliext.anim edit_clip` and `cliext.addressables edit` used to keep and save the ops before the failure; they now put the assets back as they were, in memory and on disk, through the new `AssetRollback` (which unloads the edited objects so they reload from disk: a reimport alone keeps a native asset's unsaved in-memory state). `cliext.splines edit` reverts through one Undo group and is undone as one step. Each batch edit's description says so.
- Renamed `cliext.anim clips` to `cliext.anim edit_clips`: it edits a model's clips, and its name read like a listing beside `takes` and `clip`.
- `cliext.diff compare` is no longer marked read-only (`--update` rewrites the snapshot), and `cliext.diff delete` is marked destructive.
- `cliext.cinemachine set` takes typed `--priority` (integer), `--fov` (number) and `--enabled` (true/false); leave one out to keep it.
- `cliext.setup check/apply --ids` and `cliext.test affected --files` split lists on `;` like every other list parameter (commas still work).
- `cliext.anim` accepts asset GUIDs wherever it takes a path, like `cliext.animator`, `cliext.vfx`, `cliext.shadergraph` and `cliext.input`, and their `--path` help says so.
- Clearer help: the formats of `animator drive --value`, `uitk click/set_value --selector` and `--value`, `shadergraph set --value`, `cinemachine blend --style` and `--time`, `navmesh set`, `presets apply --folder`; the values of `shadergraph add_property --type` and `create --target`/`--material`; what the -1 and NaN defaults mean (`--x`/`--y`, `--t`, `--distance`, `--index`, `--cost`, `--time`, `--layer`). Descriptions are ASCII only, so Windows PowerShell 5.1 shows them intact.
- Added `CommandSchemaTests`, EditMode tests that lint every command's declared schema: description lengths, kebab-case names, ASCII text, `--ops` with `--ops-file`, all-or-nothing batch edits, effects that match what a command does, `;` list separators, explained sentinel defaults, consistent parameter types, listed enum values and GUID support per group.

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

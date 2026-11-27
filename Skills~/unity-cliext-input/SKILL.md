---
name: unity-cliext-input
description: Edit Input Actions assets, prove an input works in Play mode, and write PlayMode tests that drive the game through real Input System devices, with Unity CLI Extensions' cliext.input commands (inspect, edit with ops files, add_device, press, set, actions, record and replay) and cliext.test. Use before adding or changing an action, binding, composite or control scheme in an .inputactions asset, when a new action has to be shown working in the running game ("press it and confirm the player moved"), when an action seems to do nothing in Play mode, when capturing an input sequence to replay, and when a project needs automated gameplay, input or UI Toolkit tests (movement, menus, pause, shop, drag and drop) or its tests bypass input through autopilots or direct method calls. Load the unity-cliext skill too for the general Editor loop.
---

# Input Actions with cliext.input

Every asset change is a JSON ops file applied with one `edit` call: the asset is read from its file, edited, written
and reimported once, and the result lists each op with `warnings`. In Play mode, virtual devices reach the game like
real ones, and `actions` reports each action's value as the game saw it.

## The loop

1. `unity command cliext.input inspect --path <asset>.inputactions --format json`: maps, actions, bindings by
   `index` and `id`, and `controlSchemes` with their `group` names.
2. Check for collisions: search that output for each new binding's path (and its scheme). The package does not, so
   say in your report which other action already uses the control, or that you didn't check.
3. Write the ops to a file and apply them: `unity command cliext.input edit --path <asset> --ops-file <ops.json> --format json`.
   ```json
   [{"op":"add_action","map":"Gameplay","action":"Jump","type":"Button"},
    {"op":"add_binding","map":"Gameplay","action":"Jump","path":"<Keyboard>/space","groups":"Keyboard&Mouse"},
    {"op":"add_binding","map":"Gameplay","action":"Jump","path":"<Gamepad>/buttonSouth","groups":"Gamepad"}]
   ```
4. Read `applied`, `results` and `warnings`; a warning is a path whose layout or control the Input System doesn't
   know (it still saved). Fix it with `set_binding`.
5. Wire the asset or action to the code (an `InputActionReference` field, `PlayerInput`, or the project-wide actions),
   compile, and save the scene before Play mode.
6. Prove it in Play mode (below), then leave Play mode and report what you pressed and what changed.

An op that fails saves nothing (`saved: false`, `failedAt`, `error`); so does an op that would change nothing. Fix
that op and rerun the whole file. Rerunning an applied file fails safely at its `add_action` ("already exists"), but a
file of only `add_binding` ops adds the bindings a second time. To undo a saved edit, apply the inverse ops
(`remove_action` takes its bindings with it) or restore the file from version control.

## Proving it in Play mode

```
unity command cliext.input add_device --layout Gamepad --format json
<read the state the action changes: position, health, a flag>
unity command cliext.input press --control buttonSouth --hold 0.1 --format json
unity command cliext.input actions --filter Gameplay/Jump --format json
<read the state again>
```
`actions` lists only enabled actions with `phase`, `lastValue`, `lastChange` (Started, Performed, Canceled) and
`secondsAgo`. Judge by `lastValue`/`lastChange` and the game state, never by a snippet that reads the action or the
device: commands and snippets run in the Editor's update and see the Editor's input state, not the game's.
Virtual devices (and replays) are removed when Play mode ends; `remove_device` removes them sooner. To turn a manual
repro into test input: `record_start`, drive the game, `record_stop --out Temp/repro.inputtrace`, later
`replay --file Temp/repro.inputtrace` and `replay_status` until it finishes. The check is evidence for a report, not
a substitute for a test: once the press works, write the test (below) so the binding stays covered.

## PlayMode tests through real devices

Tests that press keys, move sticks and click UI Toolkit elements through the Input System fail on a broken binding,
a disabled action map or a UI element that never gets the click. A test that calls the movement method directly
passes while the game's input is switched off.

1. **Setup:** Input System and Test Framework packages, and a PlayMode test asmdef (`templates/Tests.PlayMode.asmdef`
   in this skill's folder: references the game asmdef, `Unity.InputSystem`, `UnityEngine.TestRunner`,
   `UnityEditor.TestRunner`, `nunit.framework.dll`; define constraint `UNITY_INCLUDE_TESTS`). Replace its `MyGame`
   reference (and the `MyGame.Tests` names) with the game's own asmdef. Unity doesn't flag a reference to an asmdef
   that doesn't exist: the tests then just can't see the game's types (CS0246). A game with no asmdef of its own
   (scripts in Assembly-CSharp) can't be referenced from an asmdef: give the game scripts an asmdef first.
   Then make the copied templates compile before writing tests: every `PROJECT HOOK` comment marks a name to replace.
2. **Copy `templates/InputTestBase.cs`** into the test folder, rename its namespace, and fill in its "project hooks"
   region (load the game scene, start a run, find the UIDocument root). Keep it the only base class.
3. **Write `[UnityTest] IEnumerator` tests** on it: act through `Press` / `Release` / `Set` / `Tap` / `MoveMouse` /
   `Click`, then assert on game state (`templates/ExampleInputTests.cs`). Code shortcuts are for setup only (start a
   run, grant gold); the step under test goes through a device. If it can't be, say so and list a manual check.
4. **Run:** `unity command cliext.test run --mode play --filter <assembly> --filter-type assembly` returns a run id;
   `unity command cliext.test result --wait true`. The result is that run's own, a filter matching nothing fails, and
   `--filter-type name` matches part of the full name (`Class` or `Class.Method_`). Exit Play mode first: recompiles
   wait while it runs, and the `cliext.input` virtual devices are separate from the ones a test adds.

Techniques:
- Queue state events, don't call actions: `StateEvent.From(device)`, `control.WriteValueIntoEvent(value, ptr)`,
  `InputSystem.QueueEvent(ptr)` go through actions, interactions and the UI input module like hardware.
- No `InputTestFixture` when the game enables its actions at startup: it resets the Input System after that and leaves
  the game's actions stale. Use `Keyboard.current` / `Mouse.current` and add a `Gamepad` per test.
- In SetUp set `InputSystem.settings.editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView` and
  `backgroundBehavior = IgnoreFocus` (restore in TearDown), or results depend on the focused window.
- TearDown: release everything (neutral keyboard, mouse buttons up, neutral gamepad), wait a frame, remove added
  devices, reset `Time.timeScale` and pause state.
- Press, `yield return null` twice, release. `Click` moves the mouse, waits, then presses.
- UI Toolkit panel space has its origin top-left and may be scaled; the mouse uses screen pixels, origin bottom-left.
  Convert with the base class's `PanelToScreen`; for world to panel use
  `RuntimePanelUtils.CameraTransformWorldToPanel` and compare in panel space.
- No move event fires when the mouse is already there: `MoveMouse` nudges away first. `WaitForSeconds` stops at
  `timeScale` 0: use `WaitForSecondsRealtime` (or `WaitFor`, unscaled) around pause menus. GPU readbacks such as
  `aliveParticleCount` lag: poll with `WaitFor` for about 30 frames.
- Random content: reseed until the precondition holds, else `Assert.Inconclusive`. Builds start at the main menu:
  keep one test that enters gameplay from the real first scene. Count assertions break as content grows: say why
  the number is what it is in the message.

## Reference

| Command | Notes |
|---|---|
| `edit` ops | `add_map`, `remove_map`, `rename_map`, `add_action` (`type` Button/Value/PassThrough, `controlType`, `interactions`, `processors`), `remove_action`, `rename_action`, `set_action`, `add_binding` (`path`, `groups`, `interactions`, `processors`), `add_composite` (`composite` 2DVector/1DAxis/..., `parts` `Up=<Keyboard>/w;Down=<Keyboard>/s;...`, `groups`), `set_binding`/`remove_binding` (`index` or `id` from inspect), `add_scheme` (`devices` `<Gamepad>;?<Mouse>`, `?` optional), `remove_scheme` (`name`). |
| `add_device --layout <L> [--name n]` | Gamepad, Keyboard, Mouse, XInputController, ... Play mode only. |
| `press --control <c> [--hold s] [--device d]` | Buttons and keys only; default hold 0.1 s, minimum 0.02. Returns at once and releases later. |
| `set --control <c> --value <v> [--hold s]` | Sticks and axes: `--value "[0,1]"`, a number for an axis; `--hold 0` stays until set back. |
| `devices`, `remove_device [--device d]` | Ids, names, layouts and which are `virtual`. |
| `record_start [--device d]`, `record_stop --out Temp/x.inputtrace`, `replay --file <f> [--timing false]`, `replay_status` | Capture a sequence once, replay it on new device copies. |

`--control` is a path on the device (`buttonSouth`, `leftStick`, `space`) or a full path (`<Keyboard>/space`). A
relative path drives the first virtual device unless `--device` (id, name or layout) is given; a full path prefers a
virtual device but falls back to a real one.

## Gotchas

- `add_action` defaults to type **Value**: pass `"type":"Button"` for a button. It also creates a map that doesn't
  exist, so a misspelled `map` makes a new map instead of failing.
- `groups` must match a scheme's `group` from inspect exactly (`;` between several). It isn't checked, and a binding
  in a misspelled group is skipped once a control scheme is active.
- Quote full control paths: `'<Keyboard>/space'`. Unquoted `<` is a redirect in Git Bash and an error in PowerShell.
  In Git Bash, set `MSYS_NO_PATHCONV=1` (or use PowerShell) for any argument starting with `/`, or it becomes a
  Windows path.
- An action missing from `actions` is not enabled in the game: its map is disabled, or the code uses another asset.
- An interaction such as `hold(duration=0.3)` needs a longer `--hold` than its duration to perform.
- Gamepad presses are the proven path. Keyboard and mouse input in Play mode also depends on the Input System's
  `editorInputBehaviorInPlayMode` focus setting, which the package doesn't change: if a key press shows no
  `lastChange`, check that before the binding.
- `edit` rewrites the whole file from the asset: the first edit's diff also has whitespace and the serializer's
  `version`/`priority` fields. It reads the file on disk, so save or close an open Input Actions window first.
- `--help | Select -First N` exits 255 when the pipe closes; that is not a failed command.

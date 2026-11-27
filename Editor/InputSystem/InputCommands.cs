using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using IS = UnityEngine.InputSystem.InputSystem;

namespace UnityCLIExtensions.InputSystem
{
    /// <summary>
    /// <c>cliext.input</c>: edit .inputactions assets (maps, actions, bindings, composites, control schemes), and in
    /// Play mode drive the game through virtual devices, read the enabled actions, and record and replay input.
    /// Built when the Input System package is installed.
    /// </summary>
    [InitializeOnLoad]
    internal static class InputCommands
    {
        private const string Group = "cliext.input";

        static InputCommands()
        {
            // Virtual devices and replays belong to a Play session: clear them when it ends.
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Subscribe();
        }

        // The Input System clears its global callbacks when it resets on entering Play mode (with domain reload off),
        // so subscribe again then, and before each command that reads or drives actions.
        private static void Subscribe()
        {
            IS.onActionChange -= OnActionChange;
            IS.onActionChange += OnActionChange;
        }

        // An action's value read from a command runs in the Editor's update, which sees the Editor's input state, not
        // the game's. So record each action's value when it changes phase, inside the game's own update.
        private static readonly Dictionary<Guid, (string value, string change, double time)> s_LastValues = new Dictionary<Guid, (string, string, double)>();

        private static void OnActionChange(object obj, InputActionChange change)
        {
            if (!(obj is InputAction action)) return;
            if (change != InputActionChange.ActionStarted && change != InputActionChange.ActionPerformed && change != InputActionChange.ActionCanceled) return;
            string value = null;
            try { value = ValueText.Format(action.ReadValueAsObject()); } catch { }
            // Keep the last value the action had while active; a cancel reports no value.
            if (change == InputActionChange.ActionCanceled && s_LastValues.TryGetValue(action.id, out var previous)) value = previous.value;
            s_LastValues[action.id] = (value, change.ToString().Replace("Action", ""), Time.realtimeSinceStartupAsDouble);
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) { s_LastValues.Clear(); s_Held.Clear(); Subscribe(); }
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            StopReplay();
            // A recording outlives Play mode only to be saved; it would otherwise go on recording the Editor's input.
            s_Recording?.Disable();
            foreach (var d in IS.devices.Where(x => VirtualDevices.Contains(x.deviceId)).ToList()) IS.RemoveDevice(d);
            VirtualDevices.Clear();
            s_Held.Clear();
        }

        /// <summary>Ends a replay; disposing the controller removes the devices it created for the replay.</summary>
        private static void StopReplay()
        {
            s_Replay?.Dispose();
            s_Replay = null;
            s_Replaying?.Dispose();
            s_Replaying = null;
        }

        /// <summary>
        /// Virtual devices, set, press, record_start and replay reach the game only while it runs (and a device added
        /// in Edit mode would stay in the Editor): exit 6 naming editor.playmode start.
        /// </summary>
        private static bool NeedPlayMode(CommandContext cmd, string command, string why)
        {
            if (EditorApplication.isPlaying) return true;
            cmd.Fail($"cliext.input {command} {why}: enter Play mode first (editor.playmode start).");
            return false;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Assets

        [CliGroupCommand(Group, "Describe an .inputactions asset: maps, actions (type, control type, interactions, processors) with their bindings by index, and control schemes. " +
                                "A binding whose device layout or control the Input System doesn't know is marked valid: false.",
            Name = "inspect", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("path", "The .inputactions asset path or GUID", Required = true)] string path)
        {
            try
            {
                var asset = InputActionsEditing.Load(path, out string resolved);
                cmd.Result.Write(InputActionsEditing.Describe(asset));
                UnityEngine.Object.DestroyImmediate(asset);
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        [CliGroupCommand(Group, "Edit an .inputactions asset with a JSON list of ops, saved once: add_map, remove_map, rename_map, add_action, remove_action, rename_action, set_action, " +
                                "add_binding, add_composite, set_binding, remove_binding, add_scheme, remove_scheme. An op that fails saves nothing, and so does an op that would change nothing. " +
                                "Bindings take index or id (from inspect); a binding path whose layout or control is unknown saves with a warning. " +
                                "Example: [{\"op\":\"add_action\",\"map\":\"Player\",\"action\":\"Dash\",\"type\":\"Button\"},{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Dash\",\"path\":\"<Gamepad>/buttonEast\",\"groups\":\"Gamepad\"}]",
            Name = "edit", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "The .inputactions asset path or GUID", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            InputOpList list;
            try { string t = json.Trim(); list = JsonUtility.FromJson<InputOpList>(t.StartsWith("[") ? "{\"ops\":" + t + "}" : t); }
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given."); return ExitCode.InvalidUsage; }

            InputActionAsset asset;
            string resolved;
            try { asset = InputActionsEditing.Load(path, out resolved); }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            try
            {
                var warnings = new List<string>();
                var results = new List<object>();
                for (int i = 0; i < list.ops.Length; i++)
                {
                    try { results.Add(InputActionsEditing.Apply(asset, list.ops[i], warnings)); }
                    catch (Exception e)
                    {
                        // The ops ran on a copy read from the file: the file and the imported asset are as they were.
                        string message = AnimationErrors.Message(e);
                        cmd.Result.Write(new { path = resolved, applied = 0, failedAt = i, error = message, saved = false });
                        cmd.Fail($"Op {i} ({list.ops[i].op}) failed: {message} Nothing was saved.");
                        return ExitCode.Failed;
                    }
                }
                // Groups are checked against the schemes the asset has once every op ran (a batch may add the scheme last).
                warnings.AddRange(InputActionsEditing.GroupWarnings(asset, list.ops));
                InputActionsEditing.Save(asset, resolved);
                foreach (var w in warnings) cmd.Log.Warning(w);
                cmd.Result.Write(new { path = resolved, applied = results.Count, results, warnings });
                return ExitCode.Success;
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Play mode: devices, controls, actions

        [CliGroupCommand(Group, "The input devices the Input System has (Play mode or Edit mode), with which ones this package added as virtual devices.",
            Name = "devices", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Devices(CommandContext cmd)
        {
            foreach (var d in IS.devices)
                cmd.Result.WriteRecord(new { id = d.deviceId, name = d.name, layout = d.layout, path = d.path, enabled = d.enabled, @virtual = VirtualDevices.Contains(d.deviceId) });
            return ExitCode.Success;
        }

        private static readonly HashSet<int> VirtualDevices = new HashSet<int>();

        [CliGroupCommand(Group, "Add a virtual input device (--layout Gamepad, Keyboard, Mouse, XInputController, ...) that set and press can drive. Play mode only: " +
                                "it reaches the game like a real one, and is removed when Play mode ends.",
            Name = "add_device", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode AddDevice(
            CommandContext cmd,
            [CliArg("layout", "The device layout", Required = true)] string layout,
            [CliArg("name", "A name for it")] string name = "")
        {
            if (!NeedPlayMode(cmd, "add_device", "adds a device for the running game")) return ExitCode.Failed;
            string l = (layout ?? "").Trim();
            var deviceLayouts = IS.ListLayouts().Where(IsDeviceLayout).OrderBy(x => x).ToList();
            string known = deviceLayouts.FirstOrDefault(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                bool isControl = IS.ListLayouts().Any(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase));
                var close = isControl ? new List<string>() : AnimationErrors.Close(l, deviceLayouts);
                cmd.Fail($"{(isControl ? $"'{l}' is a control layout, not a device layout." : $"No device layout '{l}'.")}" +
                         (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : $" Use one such as: {string.Join(", ", new[] { "Gamepad", "Keyboard", "Mouse", "Touchscreen", "Joystick", "Pen", "XInputController", "DualShock4GamepadHID" }.Where(x => deviceLayouts.Contains(x)))}."));
                return ExitCode.InvalidUsage;
            }
            try
            {
                var device = IS.AddDevice(known, string.IsNullOrWhiteSpace(name) ? "CLI " + known : name);
                VirtualDevices.Add(device.deviceId);
                cmd.Result.Write(new { id = device.deviceId, name = device.name, layout = device.layout, path = device.path });
                return ExitCode.Success;
            }
            catch (Exception e) { cmd.Fail($"Couldn't add a {known}: {e.Message}"); return ExitCode.Failed; }
        }

        private static bool IsDeviceLayout(string layout)
        {
            try { return IS.LoadLayout(layout)?.isDeviceLayout == true; } catch { return false; }
        }

        [CliGroupCommand(Group, "Remove a device by id or name (the virtual ones this package added, by default all of them).",
            Name = "remove_device", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode RemoveDevice(CommandContext cmd, [CliArg("device", "Device id or name (default: every virtual device)")] string device = "")
        {
            List<InputDevice> targets;
            try
            {
                targets = string.IsNullOrWhiteSpace(device)
                    ? IS.devices.Where(d => VirtualDevices.Contains(d.deviceId)).ToList()
                    : new List<InputDevice> { FindDevice(device) };
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            if (string.IsNullOrWhiteSpace(device)) StopReplay();
            foreach (var d in targets) { IS.RemoveDevice(d); VirtualDevices.Remove(d.deviceId); s_Held.Remove(d.deviceId); }
            cmd.Result.Write(new { removed = targets.Select(d => d.name).ToArray() });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Set a control on a device to a value (--control leftStick --value [0,1]; buttonSouth --value 1; <Keyboard>/space ...), as if the player held it. " +
                                "--hold returns it to rest after that many seconds. Play mode only.",
            Name = "set", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Set(
            CommandContext cmd,
            [CliArg("control", "The control: a path on the device (leftStick, buttonSouth, space) or a full path (<Gamepad>/leftStick)", Required = true)] string control,
            [CliArg("value", "The value: a number, or [x,y] for a stick", Required = true)] string value,
            [CliArg("device", "Device id or name (default: from the path, else the first virtual device)")] string device = "",
            [CliArg("hold", "Seconds before returning the control to rest (0: stay)")] double hold = 0)
        {
            if (!NeedPlayMode(cmd, "set", "drives the running game's input")) return ExitCode.Failed;
            return SetControl(cmd, control, value, device, hold, press: false);
        }

        private static ExitCode SetControl(CommandContext cmd, string control, string value, string device, double hold, bool press)
        {
            Subscribe();
            try
            {
                if (hold < 0 || double.IsNaN(hold)) throw new AnimUsageException($"--hold must be 0 or more seconds, not {hold}.");
                var c = FindControl(device, control);
                if (press && !(c is ButtonControl))
                    throw new AnimUsageException($"{c.path} is {AnimationErrors.A(c.layout)} control, not a button or key: use cliext.input set --control {control} --value <value>.");
                object parsed;
                try { parsed = ValueText.Parse(value, c.valueType); }
                catch (ArgumentException e) { throw new AnimUsageException($"{e.Message} ({c.path} is {AnimationErrors.A(c.layout)} control.)"); }
                Write(c, parsed);
                if (hold > 0) ReleaseLater(c, hold);
                cmd.Result.Write(new { device = c.device.name, control = c.path, value = ValueText.Format(parsed), hold = hold > 0 ? hold : (double?)null });
                return ExitCode.Success;
            }
            catch (TargetInvocationException e) { cmd.Fail(AnimationErrors.Message(e.InnerException ?? e)); return ExitCode.Failed; }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            catch (Exception e) { cmd.Fail(AnimationErrors.Message(e)); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Press a button (or key) and release it after --hold seconds (default 0.1). Play mode only; for sticks and axes use set.",
            Name = "press", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Press(
            CommandContext cmd,
            [CliArg("control", "The button or key: buttonSouth, space, leftButton, <Keyboard>/e ...", Required = true)] string control,
            [CliArg("device", "Device id or name")] string device = "",
            [CliArg("hold", "Seconds to hold it")] double hold = 0.1)
        {
            if (!NeedPlayMode(cmd, "press", "drives the running game's input")) return ExitCode.Failed;
            if (hold <= 0 || double.IsNaN(hold)) { cmd.Fail($"--hold must be more than 0 seconds, not {hold}."); return ExitCode.InvalidUsage; }
            return SetControl(cmd, control, "1", device, Math.Max(0.02, hold), press: true);
        }

        [CliGroupCommand(Group, "The enabled input actions (Play mode): map, action, phase, value and whether it triggered this frame. --filter matches the action's map/name.",
            Name = "actions", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Actions(CommandContext cmd, [CliArg("filter", "Only actions whose map/name contains this")] string filter = "")
        {
            Subscribe();
            foreach (var a in IS.ListEnabledActions())
            {
                string key = $"{a.actionMap?.name}/{a.name}";
                if (!string.IsNullOrEmpty(filter) && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                s_LastValues.TryGetValue(a.id, out var last);
                cmd.Result.WriteRecord(new
                {
                    action = key,
                    phase = a.phase.ToString(),
                    control = a.activeControl?.path,
                    // The value at the action's last started / performed / canceled, as the game saw it.
                    lastValue = last.value,
                    lastChange = last.change,
                    secondsAgo = last.change == null ? (double?)null : Math.Round(Time.realtimeSinceStartupAsDouble - last.time, 2)
                });
            }
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Record and replay

        private static InputEventTrace s_Recording;
        private static string s_RecordingDevice;
        private static InputEventTrace s_Replaying;
        private static InputEventTrace.ReplayController s_Replay;

        [CliGroupCommand(Group, "Start recording input events (every device, or --device) for replay. Play mode only. Stop with record_stop.",
            Name = "record_start", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode RecordStart(CommandContext cmd, [CliArg("device", "Only this device (id or name)")] string device = "")
        {
            if (!NeedPlayMode(cmd, "record_start", "records the running game's input")) return ExitCode.Failed;
            if (s_Recording != null)
            {
                cmd.Fail($"Already recording ({s_RecordingDevice}, {s_Recording.eventCount} events so far): save it with cliext.input record_stop --out <file> first.");
                return ExitCode.Failed;
            }
            InputDevice only = null;
            try { if (!string.IsNullOrWhiteSpace(device)) only = FindDevice(device); }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            s_Recording = only == null ? new InputEventTrace(growBuffer: true) : new InputEventTrace(only, growBuffer: true);
            s_RecordingDevice = only == null ? "all devices" : only.name;
            s_Recording.Enable();
            cmd.Result.Write(new { recording = true, device = only == null ? "all" : only.name });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Stop recording and save the events to --out (an .inputtrace file outside Assets/ is best). A recording that caught no events fails and saves nothing.",
            Name = "record_stop", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode RecordStop(CommandContext cmd, [CliArg("out", "Where to save the trace", Required = true)] string outPath)
        {
            if (s_Recording == null) { cmd.Fail("Not recording: start with cliext.input record_start (in Play mode)."); return ExitCode.Failed; }
            if (string.IsNullOrWhiteSpace(outPath)) { cmd.Fail("Pass --out, the .inputtrace file to save, such as Temp/run.inputtrace."); return ExitCode.InvalidUsage; }
            s_Recording.Disable();
            long events = s_Recording.eventCount;
            string what = s_RecordingDevice;
            if (events == 0)
            {
                s_Recording.Dispose();
                s_Recording = null;
                cmd.Fail($"The recording of {what} caught no input events, so nothing was saved. Drive the game between record_start and record_stop " +
                         "(cliext.input set or press, or a real device) while in Play mode.");
                return ExitCode.Failed;
            }
            string full = Path.GetFullPath(Path.IsPathRooted(outPath) ? outPath : Path.Combine(CliExtPaths.ProjectRoot, outPath));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                s_Recording.WriteTo(full);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                // Keep the recording so it can be saved elsewhere.
                cmd.Fail($"Couldn't save the trace to '{full}': {e.Message} The recording is kept: run record_stop again with another --out.");
                return ExitCode.Failed;
            }
            s_Recording.Dispose();
            s_Recording = null;
            cmd.Result.Write(new { saved = full, events });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Replay a recorded .inputtrace in Play mode, on new copies of the recorded devices, keeping the original timing (--timing false plays it as fast as frames allow).",
            Name = "replay", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Replay(
            CommandContext cmd,
            [CliArg("file", "The .inputtrace file", Required = true)] string file,
            [CliArg("timing", "Keep the recorded timing")] bool timing = true)
        {
            if (!NeedPlayMode(cmd, "replay", "plays input into the running game")) return ExitCode.Failed;
            string full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(CliExtPaths.ProjectRoot, file));
            if (!File.Exists(full)) { cmd.Fail($"No trace at '{full}'. Record one with cliext.input record_start, then record_stop --out <file>."); return ExitCode.NotFound; }
            InputEventTrace trace;
            try { trace = InputEventTrace.LoadFrom(full); }
            catch (Exception e) { cmd.Fail($"'{full}' isn't an input trace ({e.Message}). Give a file saved by cliext.input record_stop."); return ExitCode.InvalidUsage; }
            if (trace.eventCount == 0) { trace.Dispose(); cmd.Fail($"'{full}' has no events to replay. Record again with input between record_start and record_stop."); return ExitCode.Failed; }
            StopReplay();
            s_Replaying = trace;
            s_Replay = s_Replaying.Replay().WithAllDevicesMappedToNewInstances();
            if (timing) s_Replay.PlayAllEventsAccordingToTimestamps(); else s_Replay.PlayAllFramesOneByOne();
            cmd.Result.Write(new { replaying = full, events = s_Replaying.eventCount });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Whether a replay is still running, and how far it got.",
            Name = "replay_status", Tags = new[] { "cliext", "input" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode ReplayStatus(CommandContext cmd)
        {
            if (s_Replay == null) { cmd.Result.Write(new { replaying = false, recording = s_Recording != null }); return ExitCode.Success; }
            bool finished = s_Replay.finished;
            cmd.Result.Write(new { replaying = !finished, position = s_Replay.position, events = s_Replaying?.eventCount, recording = s_Recording != null });
            if (finished) StopReplay();
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------

        private const string ListDevices = "cliext.input devices";

        /// <summary>A device by id, name or layout (virtual devices first); not found, with the names, when none matches.</summary>
        internal static InputDevice FindDevice(string device)
        {
            string d = device.Trim();
            var found = int.TryParse(d, out int id) ? IS.GetDeviceById(id) : null;
            found ??= IS.devices.FirstOrDefault(x => string.Equals(x.name, d, StringComparison.OrdinalIgnoreCase))
                      ?? IS.devices.FirstOrDefault(x => string.Equals(x.layout, d, StringComparison.OrdinalIgnoreCase) && VirtualDevices.Contains(x.deviceId))
                      ?? IS.devices.FirstOrDefault(x => string.Equals(x.layout, d, StringComparison.OrdinalIgnoreCase));
            return found ?? throw new AnimNotFoundException($"No device '{device}'." +
                AnimationErrors.Hint(d, IS.devices.Select(x => x.name), "Devices", ListDevices) + " Add one with cliext.input add_device --layout <layout>.");
        }

        internal static InputControl FindControl(string device, string control)
        {
            string c = (control ?? "").Trim();
            if (c.Length == 0) throw new AnimUsageException("Pass --control, such as buttonSouth, leftStick or <Keyboard>/space.");
            if (c.StartsWith("<") || c.StartsWith("/"))
            {
                var match = InputSystemControls(c);
                if (match != null) return match;
                string layout = InputControlPath.TryGetDeviceLayout(c);
                var devices = string.IsNullOrEmpty(layout) ? new List<InputDevice>()
                    : IS.devices.Where(x => InputControlPath.MatchesPrefix("<" + layout + ">", x)).ToList();
                if (!string.IsNullOrEmpty(layout) && devices.Count == 0)
                    throw new AnimNotFoundException($"No control matches '{c}': there is no {layout} device. Add one with cliext.input add_device --layout {layout}.");
                var sample = devices.FirstOrDefault(x => VirtualDevices.Contains(x.deviceId)) ?? devices.FirstOrDefault();
                string last = c.Substring(c.LastIndexOf('/') + 1);
                throw new AnimNotFoundException($"No control matches '{c}'." +
                    (sample != null ? AnimationErrors.Hint(last, ControlNames(sample), "Controls", "cliext.input devices") : $" {ListDevices} lists the devices."));
            }
            InputDevice d = !string.IsNullOrWhiteSpace(device) ? FindDevice(device)
                : IS.devices.FirstOrDefault(x => VirtualDevices.Contains(x.deviceId))
                  ?? throw new AnimNotFoundException("No virtual device to drive: add one with cliext.input add_device --layout Gamepad (or Keyboard, Mouse), or pass --device.");
            return d.TryGetChildControl(c) ?? throw new AnimNotFoundException($"{d.name} has no control '{c}'." + AnimationErrors.Hint(c, ControlNames(d), "Controls", "cliext.input devices"));
        }

        /// <summary>The device's controls as paths relative to it (buttonSouth, leftStick, leftStick/x).</summary>
        private static IEnumerable<string> ControlNames(InputDevice d) =>
            d.allControls.Select(x => x.path.Substring(d.path.Length).TrimStart('/')).Where(x => x.Length > 0);

        private static InputControl InputSystemControls(string path)
        {
            // Prefer a virtual device's control over a real one's.
            var matches = InputSystemFind(path);
            return matches.FirstOrDefault(m => VirtualDevices.Contains(m.device.deviceId)) ?? matches.FirstOrDefault();
        }

        internal static List<InputControl> InputSystemFind(string path)
        {
            // InputControlList.CopyTo throws NotImplementedException, so don't hand it to ToList (which calls it).
            var result = new List<InputControl>();
            using (var list = IS.FindControls(path))
                for (int i = 0; i < list.Count; i++) result.Add(list[i]);
            return result;
        }

        // What each virtual device is holding, by control path. A state event starts from the device's state as the
        // Editor sees it, which doesn't include earlier events the game consumed, so every event carries all of them.
        private static readonly Dictionary<int, Dictionary<string, (InputControl control, object value)>> s_Held =
            new Dictionary<int, Dictionary<string, (InputControl, object)>>();

        private static void Write(InputControl control, object value, bool release = false)
        {
            if (!s_Held.TryGetValue(control.device.deviceId, out var held))
                s_Held[control.device.deviceId] = held = new Dictionary<string, (InputControl, object)>();
            if (release) held.Remove(control.path); else held[control.path] = (control, value);

            // A full state event for the device with every held control written in (delta events can't address
            // bitfield controls such as gamepad buttons). The player loop processes it, so it reaches the game's
            // actions like real input.
            using (StateEvent.From(control.device, out InputEventPtr eventPtr))
            {
                foreach (var c in control.device.allControls)
                    if (c.valueType.IsValueType && !(c.device == c) && c.children.Count == 0 && !held.ContainsKey(c.path) && !IsHeldChild(c, held))
                        try { c.WriteValueFromObjectIntoEvent(eventPtr, Activator.CreateInstance(c.valueType)); } catch { }
                if (release) control.WriteValueFromObjectIntoEvent(eventPtr, value);
                foreach (var h in held.Values) h.control.WriteValueFromObjectIntoEvent(eventPtr, h.value);
                IS.QueueEvent(eventPtr);
            }
        }

        private static bool IsHeldChild(InputControl c, Dictionary<string, (InputControl control, object value)> held)
        {
            for (var p = c.parent; p != null; p = p.parent) if (held.ContainsKey(p.path)) return true;
            return false;
        }

        private static void ReleaseLater(InputControl control, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            object rest = control.valueType.IsValueType ? Activator.CreateInstance(control.valueType) : null;
            EditorDeferred.Run(() => { if (control.device.added) Write(control, rest, release: true); },
                () => clock.Elapsed.TotalSeconds >= seconds, timeoutSeconds: seconds + 30, onTimeout: null, label: "cliext.input release", warnOnTimeout: false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityCLIExtensions.InputSystem
{
    /// <summary>One edit to an .inputactions asset, as given to <c>cliext.input edit --ops</c>.</summary>
    [Serializable]
    internal class InputOp
    {
        /// <summary>add_map, remove_map, rename_map, add_action, remove_action, rename_action, set_action,
        /// add_binding, add_composite, set_binding, remove_binding, add_scheme, remove_scheme.</summary>
        public string op;
        public string map;
        public string action;
        /// <summary>For rename_*: the new name. For add_scheme/remove_scheme: the scheme.</summary>
        public string name;
        /// <summary>For add_action/set_action: Value, Button or PassThrough.</summary>
        public string type;
        /// <summary>For add_action/set_action: the expected control type (Vector2, Button, Axis, ...).</summary>
        public string controlType;
        /// <summary>For bindings: the control path (&lt;Gamepad&gt;/leftStick, &lt;Keyboard&gt;/space).</summary>
        public string path;
        /// <summary>For bindings: control schemes, separated by ';' (Keyboard&amp;Mouse;Gamepad).</summary>
        public string groups;
        public string interactions;
        public string processors;
        /// <summary>For add_composite: the composite (2DVector, 1DAxis, ButtonWithOneModifier, ...).</summary>
        public string composite;
        /// <summary>For add_composite: its parts, name=path separated by ';' (Up=&lt;Keyboard&gt;/w;Down=&lt;Keyboard&gt;/s;...).</summary>
        public string parts;
        /// <summary>For set_binding/remove_binding: the binding's index within the action (see inspect), or use id.</summary>
        public int index = -1;
        public string id;
        /// <summary>For add_scheme: required devices, separated by ';' (&lt;Keyboard&gt;;&lt;Mouse&gt;).</summary>
        public string devices;
    }

    [Serializable]
    internal class InputOpList
    {
        public InputOp[] ops;
    }

    /// <summary>Loads, edits, saves and describes .inputactions assets through the Input System's public API.</summary>
    internal static class InputActionsEditing
    {
        public static InputActionAsset Load(string path, out string resolved)
        {
            resolved = (path ?? "").Trim().Replace('\\', '/');
            if (resolved.Length == 32 && resolved.All(Uri.IsHexDigit)) resolved = AssetDatabase.GUIDToAssetPath(resolved);
            if (!File.Exists(resolved)) throw new ArgumentException($"No .inputactions file at '{resolved}'.");
            return InputActionAsset.FromJson(File.ReadAllText(resolved));
        }

        public static void Save(InputActionAsset asset, string path)
        {
            File.WriteAllText(path, asset.ToJson());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        public static object Describe(InputActionAsset asset) => new
        {
            maps = asset.actionMaps.Select(m => new
            {
                name = m.name,
                id = m.id.ToString(),
                actions = m.actions.Select(a => new
                {
                    name = a.name,
                    id = a.id.ToString(),
                    type = a.type.ToString(),
                    controlType = a.expectedControlType,
                    interactions = Empty(a.interactions),
                    processors = Empty(a.processors),
                    bindings = BindingsOf(a).Select(x => new
                    {
                        index = x.index,
                        id = x.binding.id.ToString(),
                        path = x.binding.path,
                        name = x.binding.isPartOfComposite || x.binding.isComposite ? x.binding.name : null,
                        composite = x.binding.isComposite ? true : (bool?)null,
                        part = x.binding.isPartOfComposite ? true : (bool?)null,
                        groups = Empty(x.binding.groups),
                        interactions = Empty(x.binding.interactions),
                        processors = Empty(x.binding.processors),
                        valid = x.binding.isComposite || IsKnownPath(x.binding.path) ? (bool?)null : false
                    }).ToArray()
                }).ToArray()
            }).ToArray(),
            controlSchemes = asset.controlSchemes.Select(s => new
            {
                name = s.name,
                group = s.bindingGroup,
                devices = s.deviceRequirements.Select(d => (d.isOptional ? "optional " : "") + d.controlPath).ToArray()
            }).ToArray()
        };

        private static string Empty(string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>The action's bindings with their index in the action (what set_binding and remove_binding take).</summary>
        public static IEnumerable<(int index, InputBinding binding)> BindingsOf(InputAction action)
        {
            var list = action.bindings;
            for (int i = 0; i < list.Count; i++) yield return (i, list[i]);
        }

        /// <summary>True when the path names a device layout the Input System knows (or is empty).</summary>
        public static bool IsKnownPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string layout = InputControlPath.TryGetDeviceLayout(path);
            return string.IsNullOrEmpty(layout) || UnityEngine.InputSystem.InputSystem.ListLayouts().Any(l => string.Equals(l, layout, StringComparison.OrdinalIgnoreCase));
        }

        public static object Apply(InputActionAsset asset, InputOp op, List<string> warnings)
        {
            InputActionMap Map(bool create = false)
            {
                if (string.IsNullOrWhiteSpace(op.map)) throw new ArgumentException($"'{op.op}' needs a map.");
                var m = asset.FindActionMap(op.map);
                if (m != null) return m;
                if (create) return asset.AddActionMap(op.map);
                throw new ArgumentException($"No action map '{op.map}'. Maps: {string.Join(", ", asset.actionMaps.Select(x => x.name))}");
            }
            InputAction Action()
            {
                var m = Map();
                if (string.IsNullOrWhiteSpace(op.action)) throw new ArgumentException($"'{op.op}' needs an action.");
                return m.FindAction(op.action) ?? throw new ArgumentException($"No action '{op.action}' in {m.name}. Actions: {string.Join(", ", m.actions.Select(a => a.name))}");
            }
            int BindingIndex(InputAction a)
            {
                if (!string.IsNullOrWhiteSpace(op.id))
                {
                    var match = BindingsOf(a).FirstOrDefault(x => x.binding.id.ToString() == op.id.Trim());
                    if (match.binding.id == default) throw new ArgumentException($"No binding with id '{op.id}' on {a.name}.");
                    return match.index;
                }
                if (op.index < 0 || op.index >= a.bindings.Count) throw new ArgumentException($"{a.name} has bindings 0-{a.bindings.Count - 1}; pass index or id.");
                return op.index;
            }
            void CheckPath(string p)
            {
                if (!IsKnownPath(p)) warnings.Add($"'{p}' names a device layout the Input System doesn't know.");
            }

            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add_map":
                    if (asset.FindActionMap(op.map) != null) throw new ArgumentException($"Map '{op.map}' already exists.");
                    return new { op = "add_map", map = asset.AddActionMap(op.map).name };
                case "remove_map":
                    asset.RemoveActionMap(Map());
                    return new { op = "remove_map", map = op.map };
                case "rename_map":
                {
                    // Maps have no public Rename (actions do); the name lives in m_Name.
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("rename_map needs the new name.");
                    if (asset.FindActionMap(op.name) != null) throw new ArgumentException($"Map '{op.name}' already exists.");
                    var field = typeof(InputActionMap).GetField("m_Name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                                ?? throw new ArgumentException("This Input System version can't rename maps from here.");
                    field.SetValue(Map(), op.name);
                    return new { op = "rename_map", map = op.name };
                }
                case "add_action":
                {
                    var m = Map(create: true);
                    if (m.FindAction(op.action) != null) throw new ArgumentException($"Action '{op.action}' already exists in {m.name}.");
                    var type = string.IsNullOrEmpty(op.type) ? InputActionType.Value : (InputActionType)Enum.Parse(typeof(InputActionType), op.type, true);
                    var a = m.AddAction(op.action, type, expectedControlLayout: string.IsNullOrEmpty(op.controlType) ? null : op.controlType,
                        interactions: op.interactions, processors: op.processors);
                    return new { op = "add_action", map = m.name, action = a.name, id = a.id.ToString() };
                }
                case "remove_action":
                    Action().RemoveAction();
                    return new { op = "remove_action", action = op.action };
                case "rename_action":
                    Action().Rename(op.name);
                    return new { op = "rename_action", action = op.name };
                case "set_action":
                {
                    // InputAction's type, control type, interactions and processors have no setters: rebuild the action in place.
                    var a = Action();
                    var m = a.actionMap;
                    var type = string.IsNullOrEmpty(op.type) ? a.type : (InputActionType)Enum.Parse(typeof(InputActionType), op.type, true);
                    string controlType = op.controlType ?? a.expectedControlType;
                    string interactions = op.interactions ?? a.interactions;
                    string processors = op.processors ?? a.processors;
                    var bindings = a.bindings.ToList();
                    string name = a.name;
                    a.RemoveAction();
                    m.AddAction(name, type, expectedControlLayout: string.IsNullOrEmpty(controlType) ? null : controlType,
                        interactions: interactions, processors: processors);
                    foreach (var b in bindings)
                    {
                        var copy = b;
                        copy.action = name;
                        m.AddBinding(copy);
                    }
                    return new { op = "set_action", action = name, type = type.ToString(), controlType, interactions, processors, note = "The action moved to the end of its map." };
                }
                case "add_binding":
                {
                    var a = Action();
                    if (string.IsNullOrWhiteSpace(op.path)) throw new ArgumentException("add_binding needs a path, such as <Gamepad>/buttonSouth.");
                    CheckPath(op.path);
                    var syntax = a.AddBinding(op.path, interactions: op.interactions, processors: op.processors, groups: op.groups);
                    return new { op = "add_binding", action = a.name, path = op.path, index = a.bindings.Count - 1 };
                }
                case "add_composite":
                {
                    var a = Action();
                    if (string.IsNullOrWhiteSpace(op.composite)) throw new ArgumentException("add_composite needs a composite, such as 2DVector.");
                    var composite = a.AddCompositeBinding(op.composite, interactions: op.interactions, processors: op.processors);
                    foreach (string part in (op.parts ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"Composite part '{part}' isn't name=path.");
                        string partPath = part.Substring(eq + 1).Trim();
                        CheckPath(partPath);
                        composite = composite.With(part.Substring(0, eq).Trim(), partPath, op.groups);
                    }
                    return new { op = "add_composite", action = a.name, composite = op.composite, count = a.bindings.Count };
                }
                case "set_binding":
                {
                    var a = Action();
                    int i = BindingIndex(a);
                    var binding = a.bindings[i];
                    if (op.path != null) { CheckPath(op.path); binding.path = op.path; }
                    if (op.groups != null) binding.groups = op.groups;
                    if (op.interactions != null) binding.interactions = op.interactions;
                    if (op.processors != null) binding.processors = op.processors;
                    a.ChangeBinding(i).To(binding);
                    return new { op = "set_binding", action = a.name, index = i, path = binding.path, groups = binding.groups };
                }
                case "remove_binding":
                {
                    var a = Action();
                    int i = BindingIndex(a);
                    var b = a.bindings[i];
                    a.ChangeBinding(i).Erase();
                    return new { op = "remove_binding", action = a.name, index = i, path = b.path, composite = b.isComposite };
                }
                case "add_scheme":
                {
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("add_scheme needs a name.");
                    var syntax = asset.AddControlScheme(op.name);
                    foreach (string d in (op.devices ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string device = d.Trim();
                        bool optional = device.StartsWith("?");
                        syntax = optional ? syntax.WithOptionalDevice(device.Substring(1)) : syntax.WithRequiredDevice(device);
                    }
                    syntax.Done();
                    return new { op = "add_scheme", name = op.name };
                }
                case "remove_scheme":
                    asset.RemoveControlScheme(op.name);
                    return new { op = "remove_scheme", name = op.name };
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: add_map, remove_map, rename_map, add_action, remove_action, rename_action, set_action, add_binding, add_composite, set_binding, remove_binding, add_scheme, remove_scheme.");
            }
        }
    }
}

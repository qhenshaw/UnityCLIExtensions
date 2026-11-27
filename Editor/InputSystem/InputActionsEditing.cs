using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using IS = UnityEngine.InputSystem.InputSystem;

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
        internal const string Ops = "add_map, remove_map, rename_map, add_action, remove_action, rename_action, set_action, add_binding, add_composite, set_binding, remove_binding, add_scheme, remove_scheme";
        private const string Inspect = "cliext.input inspect";

        // The composites the Input System ships (it has no public list of the registered ones).
        private static readonly string[] BuiltInComposites = { "1DAxis", "2DVector", "3DVector", "ButtonWithOneModifier", "ButtonWithTwoModifiers", "OneModifier", "TwoModifiers" };

        /// <summary>
        /// A fresh copy of the asset read from its file (the imported asset is never touched, so a failed edit leaves
        /// it as it was). Invalid usage for no path, a path outside Assets/ or Packages/ or a file that isn't
        /// .inputactions; not found, naming how to make one, when nothing is there.
        /// </summary>
        public static InputActionAsset Load(string path, out string resolved)
        {
            resolved = AnimationErrors.AssetPath(path);
            if (resolved.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(path)) throw new AnimNotFoundException($"No asset has the GUID '{path.Trim()}'. Pass the .inputactions path instead.");
                throw new AnimUsageException("Pass --path, an .inputactions asset path or GUID, such as Assets/InputSystem_Actions.inputactions.");
            }
            if (!resolved.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                throw new AnimUsageException($"'{path}' isn't under Assets/ or Packages/. Give the asset's path, such as Assets/InputSystem_Actions.inputactions.");
            string full = FileUtil.GetPhysicalPath(resolved);
            bool exists = File.Exists(full);
            if (!resolved.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
            {
                var main = exists ? AssetDatabase.LoadMainAssetAtPath(resolved) : null;
                throw new AnimUsageException($"'{resolved}' is {(main != null ? AnimationErrors.A(main.GetType().Name) : exists ? "not an Input Actions file" : "not an .inputactions path")}, not an .inputactions asset.");
            }
            if (!exists)
                throw new AnimNotFoundException($"No .inputactions asset at '{resolved}'. Create one in the Project window (Create > Input Actions), " +
                                                "or copy an existing one (AssetDatabase.CopyAsset) and edit that.");
            try { return InputActionAsset.FromJson(File.ReadAllText(full)); }
            catch (Exception e) { throw new ArgumentException($"'{resolved}' isn't valid Input Actions JSON ({e.Message}). Fix or replace the file."); }
        }

        public static void Save(InputActionAsset asset, string path)
        {
            File.WriteAllText(FileUtil.GetPhysicalPath(path), asset.ToJson());
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

        /// <summary>True when the path names a device layout the Input System knows, and a control it has (or is empty).</summary>
        public static bool IsKnownPath(string path) => PathProblem(path) == null;

        /// <summary>
        /// Why a binding path won't match anything: its device layout is unknown, or no device built on that layout has
        /// its first control. Null when it's fine, or when the path uses wildcards, usages or other syntax this doesn't check.
        /// </summary>
        public static string PathProblem(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string layout = InputControlPath.TryGetDeviceLayout(path);
            // "*/{Submit}" gives "*": any device.
            if (string.IsNullOrEmpty(layout) || layout.Contains("*")) return null;
            var layouts = IS.ListLayouts().ToList();
            if (!layouts.Any(l => string.Equals(l, layout, StringComparison.OrdinalIgnoreCase)))
                return $"'{path}' names a device layout the Input System doesn't know.{Close(layout, layouts)}";

            // <Layout>/control[/...]: check the first control when it's a plain name.
            int close = path.IndexOf('>');
            if (close < 0 || close + 1 >= path.Length || path[close + 1] != '/') return null;
            string rest = path.Substring(close + 2);
            string first = rest.Split('/')[0];
            if (first.Length == 0 || first.IndexOfAny(new[] { '*', '{', '#', '(', '<', '[' }) >= 0) return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string l in new[] { layout }.Concat(IS.ListLayoutsBasedOn(layout)))
                {
                    var loaded = IS.LoadLayout(l);
                    if (loaded == null) continue;
                    foreach (var c in loaded.controls)
                    {
                        names.Add(c.name.ToString().Split('/')[0]);
                        foreach (var alias in c.aliases) names.Add(alias.ToString());
                    }
                }
            }
            catch { return null; }
            if (names.Count == 0 || names.Contains(first)) return null;
            return $"'{path}': no {layout} has a control '{first}'.{Close(first, names)}";
        }

        private static string Close(string name, IEnumerable<string> candidates)
        {
            var close = AnimationErrors.Close(name, candidates);
            return close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "";
        }

        private static string NeedName(string what, string op) => $"{op} needs {what}.";

        /// <summary>The registered interactions or processors a "name(param=v),name" list names that the Input System doesn't have.</summary>
        private static void CheckNamed(string list, string kind, Func<string, Type> lookup, IEnumerable<string> known)
        {
            if (string.IsNullOrWhiteSpace(list)) return;
            int depth = 0, start = 0;
            var names = new List<string>();
            for (int i = 0; i <= list.Length; i++)
            {
                char ch = i < list.Length ? list[i] : ',';
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == ',' && depth == 0)
                {
                    string item = list.Substring(start, i - start).Trim();
                    int paren = item.IndexOf('(');
                    names.Add((paren >= 0 ? item.Substring(0, paren) : item).Trim());
                    start = i + 1;
                }
            }
            foreach (string n in names)
            {
                if (n.Length == 0) throw new ArgumentException($"The {kind} list '{list}' has an empty entry.");
                if (lookup(n) == null)
                {
                    var all = known.ToList();
                    var near = AnimationErrors.Close(n, all);
                    throw new ArgumentException($"No {kind} '{n}'." + (near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : $" {char.ToUpper(kind[0]) + kind.Substring(1)}s: {string.Join(", ", all)}."));
                }
            }
        }

        private static void CheckInteractions(string s) => CheckNamed(s, "interaction", IS.TryGetInteraction, IS.ListInteractions());
        private static void CheckProcessors(string s) => CheckNamed(s, "processor", IS.TryGetProcessor, IS.ListProcessors());

        private static readonly FieldInfo s_ActionType = typeof(InputAction).GetField("m_Type", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_ActionControlType = typeof(InputAction).GetField("m_ExpectedControlType", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_ActionInteractions = typeof(InputAction).GetField("m_Interactions", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_ActionProcessors = typeof(InputAction).GetField("m_Processors", BindingFlags.Instance | BindingFlags.NonPublic);

        public static object Apply(InputActionAsset asset, InputOp op, List<string> warnings)
        {
            string opName = (op.op ?? "").Trim().ToLowerInvariant();
            InputActionMap Map(bool create = false)
            {
                if (string.IsNullOrWhiteSpace(op.map)) throw new ArgumentException($"{opName} needs a map.");
                var m = asset.FindActionMap(op.map);
                if (m != null) return m;
                if (create) return asset.AddActionMap(op.map);
                throw new ArgumentException($"No action map '{op.map}'." + AnimationErrors.Hint(op.map, asset.actionMaps.Select(x => x.name), "Maps", Inspect));
            }
            InputAction Action()
            {
                var m = Map();
                if (string.IsNullOrWhiteSpace(op.action)) throw new ArgumentException($"{opName} needs an action.");
                return m.FindAction(op.action) ?? throw new ArgumentException($"No action '{op.action}' in {m.name}." + AnimationErrors.Hint(op.action, m.actions.Select(a => a.name), "Actions", Inspect));
            }
            int BindingIndex(InputAction a)
            {
                if (!string.IsNullOrWhiteSpace(op.id))
                {
                    var match = BindingsOf(a).FirstOrDefault(x => x.binding.id.ToString() == op.id.Trim());
                    if (match.binding.id == default) throw new ArgumentException($"No binding with id '{op.id}' on {a.name}. {Inspect} lists each binding's id and index.");
                    return match.index;
                }
                if (a.bindings.Count == 0) throw new ArgumentException($"{a.name} has no bindings.");
                if (op.index < 0 || op.index >= a.bindings.Count)
                    throw new ArgumentException($"{a.name} has bindings 0-{a.bindings.Count - 1}{(op.index >= 0 ? $", not {op.index}" : "")}: pass index or id ({Inspect} lists them).");
                return op.index;
            }
            void CheckPath(string p)
            {
                string problem = PathProblem(p);
                if (problem != null) warnings.Add(problem);
            }
            InputActionType ActionType(InputActionType fallback) =>
                string.IsNullOrWhiteSpace(op.type) ? fallback : AnimationErrors.ParseEnum<InputActionType>(op.type, "action type");

            switch (opName)
            {
                case "add_map":
                    if (string.IsNullOrWhiteSpace(op.map)) throw new ArgumentException("add_map needs a map (the new map's name).");
                    if (asset.FindActionMap(op.map) != null) throw new ArgumentException($"Map '{op.map}' already exists.");
                    return new { op = "add_map", map = asset.AddActionMap(op.map).name };
                case "remove_map":
                    asset.RemoveActionMap(Map());
                    return new { op = "remove_map", map = op.map };
                case "rename_map":
                {
                    // Maps have no public Rename (actions do); the name lives in m_Name.
                    var m = Map();
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("rename_map needs name (the new name).");
                    if (op.name == m.name) throw new ArgumentException($"Map '{m.name}' already has that name.");
                    if (asset.FindActionMap(op.name) != null) throw new ArgumentException($"Map '{op.name}' already exists.");
                    var field = typeof(InputActionMap).GetField("m_Name", BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? throw new ArgumentException("This Input System version can't rename maps from here.");
                    field.SetValue(m, op.name);
                    return new { op = "rename_map", map = op.name };
                }
                case "add_action":
                {
                    if (string.IsNullOrWhiteSpace(op.action)) throw new ArgumentException("add_action needs an action (the new action's name).");
                    var type = ActionType(InputActionType.Value);
                    CheckInteractions(op.interactions);
                    CheckProcessors(op.processors);
                    var m = Map(create: true);
                    if (m.FindAction(op.action) != null) throw new ArgumentException($"Action '{op.action}' already exists in {m.name}.");
                    var a = m.AddAction(op.action, type, expectedControlLayout: string.IsNullOrEmpty(op.controlType) ? null : op.controlType,
                        interactions: op.interactions, processors: op.processors);
                    return new { op = "add_action", map = m.name, action = a.name, id = a.id.ToString() };
                }
                case "remove_action":
                    Action().RemoveAction();
                    return new { op = "remove_action", action = op.action };
                case "rename_action":
                {
                    var a = Action();
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("rename_action needs name (the new name).");
                    if (op.name == a.name) throw new ArgumentException($"Action '{a.name}' already has that name.");
                    if (a.actionMap.FindAction(op.name) != null) throw new ArgumentException($"{a.actionMap.name} already has an action '{op.name}'.");
                    a.Rename(op.name);
                    return new { op = "rename_action", action = op.name };
                }
                case "set_action":
                {
                    // InputAction's type, control type, interactions and processors have no public setters. Set the
                    // serialized fields, which keeps the action's id (what InputActionReferences point at) and its place.
                    var a = Action();
                    var type = ActionType(a.type);
                    string controlType = op.controlType ?? a.expectedControlType;
                    string interactions = op.interactions ?? a.interactions;
                    string processors = op.processors ?? a.processors;
                    if (op.type == null && op.controlType == null && op.interactions == null && op.processors == null)
                        throw new ArgumentException("set_action needs type, controlType, interactions or processors.");
                    if (type == a.type && (controlType ?? "") == (a.expectedControlType ?? "") && (interactions ?? "") == (a.interactions ?? "") && (processors ?? "") == (a.processors ?? ""))
                        throw new ArgumentException($"{a.name} already has those settings: set_action would change nothing.");
                    CheckInteractions(op.interactions);
                    CheckProcessors(op.processors);
                    if (s_ActionType == null || s_ActionControlType == null || s_ActionInteractions == null || s_ActionProcessors == null)
                        throw new ArgumentException("This Input System version can't change an action's settings from here.");
                    s_ActionType.SetValue(a, type);
                    s_ActionControlType.SetValue(a, string.IsNullOrEmpty(controlType) ? null : controlType);
                    s_ActionInteractions.SetValue(a, interactions);
                    s_ActionProcessors.SetValue(a, processors);
                    return new { op = "set_action", action = a.name, type = type.ToString(), controlType, interactions, processors };
                }
                case "add_binding":
                {
                    var a = Action();
                    if (string.IsNullOrWhiteSpace(op.path)) throw new ArgumentException("add_binding needs a path, such as <Gamepad>/buttonSouth.");
                    CheckInteractions(op.interactions);
                    CheckProcessors(op.processors);
                    CheckPath(op.path);
                    a.AddBinding(op.path, interactions: op.interactions, processors: op.processors, groups: op.groups);
                    return new { op = "add_binding", action = a.name, path = op.path, index = a.bindings.Count - 1 };
                }
                case "add_composite":
                {
                    var a = Action();
                    if (string.IsNullOrWhiteSpace(op.composite)) throw new ArgumentException("add_composite needs a composite, such as 2DVector.");
                    string compositeName = op.composite.Split('(')[0].Trim();
                    var compositeType = IS.TryGetBindingComposite(compositeName);
                    if (compositeType == null)
                    {
                        var near = AnimationErrors.Close(compositeName, BuiltInComposites);
                        throw new ArgumentException($"No composite '{compositeName}'." + (near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : $" Composites: {string.Join(", ", BuiltInComposites)}."));
                    }
                    var partNames = compositeType.GetFields(BindingFlags.Instance | BindingFlags.Public)
                        .Where(f => f.GetCustomAttribute<UnityEngine.InputSystem.Layouts.InputControlAttribute>() != null).Select(f => f.Name).ToList();
                    var parts = new List<(string name, string path)>();
                    foreach (string part in (op.parts ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"Composite part '{part}' isn't name=path, such as Up=<Keyboard>/w.");
                        string partName = part.Substring(0, eq).Trim();
                        string known = partNames.FirstOrDefault(n => string.Equals(n, partName, StringComparison.OrdinalIgnoreCase));
                        if (partNames.Count > 0 && known == null)
                            throw new ArgumentException($"{compositeName} has no part '{partName}'." + AnimationErrors.Hint(partName, partNames, "Parts", "cliext.input edit --help"));
                        parts.Add((known ?? partName, part.Substring(eq + 1).Trim()));
                    }
                    if (parts.Count == 0)
                        throw new ArgumentException($"add_composite needs parts, name=path separated by ';'. {compositeName}'s parts: {string.Join(", ", partNames)}.");
                    CheckInteractions(op.interactions);
                    CheckProcessors(op.processors);
                    var composite = a.AddCompositeBinding(op.composite, interactions: op.interactions, processors: op.processors);
                    foreach (var (partName, partPath) in parts)
                    {
                        CheckPath(partPath);
                        composite = composite.With(partName, partPath, op.groups);
                    }
                    return new { op = "add_composite", action = a.name, composite = op.composite, count = a.bindings.Count };
                }
                case "set_binding":
                {
                    var a = Action();
                    int i = BindingIndex(a);
                    var binding = a.bindings[i];
                    if (op.path == null && op.groups == null && op.interactions == null && op.processors == null)
                        throw new ArgumentException("set_binding needs path, groups, interactions or processors.");
                    var before = binding;
                    if (op.path != null) { if (op.path.Trim().Length == 0 && !binding.isComposite) throw new ArgumentException("set_binding's path is empty: give a control path, or remove_binding."); CheckPath(op.path); binding.path = op.path; }
                    if (op.groups != null) binding.groups = op.groups;
                    if (op.interactions != null) { CheckInteractions(op.interactions); binding.interactions = op.interactions; }
                    if (op.processors != null) { CheckProcessors(op.processors); binding.processors = op.processors; }
                    if ((before.path ?? "") == (binding.path ?? "") && (before.groups ?? "") == (binding.groups ?? "") &&
                        (before.interactions ?? "") == (binding.interactions ?? "") && (before.processors ?? "") == (binding.processors ?? ""))
                        throw new ArgumentException($"Binding {i} of {a.name} already has those settings: set_binding would change nothing.");
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
                    if (asset.controlSchemes.Any(s => string.Equals(s.name, op.name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"Control scheme '{op.name}' already exists.");
                    var syntax = asset.AddControlScheme(op.name);
                    foreach (string d in (op.devices ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string device = d.Trim();
                        bool optional = device.StartsWith("?");
                        CheckPath(optional ? device.Substring(1) : device);
                        syntax = optional ? syntax.WithOptionalDevice(device.Substring(1)) : syntax.WithRequiredDevice(device);
                    }
                    syntax.Done();
                    return new { op = "add_scheme", name = op.name };
                }
                case "remove_scheme":
                {
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("remove_scheme needs a name.");
                    var schemes = asset.controlSchemes.Select(s => s.name).ToList();
                    if (!schemes.Any(s => string.Equals(s, op.name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"No control scheme '{op.name}'." + AnimationErrors.Hint(op.name, schemes, "Control schemes", Inspect));
                    asset.RemoveControlScheme(op.name);
                    return new { op = "remove_scheme", name = op.name };
                }
                default:
                {
                    var near = AnimationErrors.Close(opName, Ops.Split(new[] { ", " }, StringSplitOptions.None));
                    throw new ArgumentException($"Unknown op '{op.op}'.{(near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : "")} Ops: {Ops}.");
                }
            }
        }
    }
}

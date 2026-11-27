using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCLIExtensions.Animation
{
    /// <summary>One edit to an AnimatorController, as given to <c>cliext.animator edit --ops</c>.</summary>
    [Serializable]
    internal class AnimatorOp
    {
        /// <summary>See the command's description for the list.</summary>
        public string op;
        /// <summary>The layer (name or index). Default: the first.</summary>
        public string layer;
        /// <summary>A state, by name or path inside the layer (Locomotion/Walk).</summary>
        public string state;
        /// <summary>For transitions: the source state (or "Any") and destination state (or "Exit").</summary>
        public string from;
        public string to;
        /// <summary>Which of several transitions between the same states (default 0).</summary>
        public int index;
        public string name;
        /// <summary>Settings as name=value pairs separated by ';' (speed=1.2;tag=Move;writeDefaults=false).</summary>
        public string set;
        /// <summary>For transitions: conditions "Param Mode Threshold" separated by ';' (Speed Greater 0.1;Grounded If;Jump If).</summary>
        public string conditions;
        /// <summary>For motions: an AnimationClip path (or a sub-asset: Assets/Char.fbx@Run).</summary>
        public string motion;
        /// <summary>For add_blendtree: Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, Direct.</summary>
        public string type;
        public string parameter;
        public string parameterY;
        /// <summary>For add_blendtree: children "clipPath@threshold" (1D) or "clipPath@x,y" (2D) separated by ';'.</summary>
        public string children;
        /// <summary>For set_parameter: the default value.</summary>
        public string value;
    }

    [Serializable]
    internal class AnimatorOpList
    {
        public AnimatorOp[] ops;
    }

    internal static class AnimatorEditing
    {
        /// <summary>The controller at a path or GUID: not found (naming the command that makes one), or invalid usage when the asset there is something else.</summary>
        public static AnimatorController Load(string path)
        {
            string p = AnimationErrors.AssetPath(path);
            AnimationErrors.RequireType(p, "AnimatorController", typeof(AnimatorController),
                $"Create one with animation.animator create_controller --path {(p.Length > 0 ? p : "Assets/Name.controller")}, or pass an existing .controller path or GUID.", path);
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(p);
        }

        public static AnimatorControllerLayer[] Layers(AnimatorController c) => c.layers;

        public static int LayerIndex(AnimatorController c, string layer)
        {
            var layers = c.layers;
            if (string.IsNullOrWhiteSpace(layer)) return 0;
            if (int.TryParse(layer, out int i))
            {
                if (i >= 0 && i < layers.Length) return i;
                throw new ArgumentException($"No layer {i}: the controller has {layers.Length} (0-{layers.Length - 1}: {string.Join(", ", layers.Select(l => l.name))}).");
            }
            int found = Array.FindIndex(layers, l => string.Equals(l.name, layer.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found < 0) throw new ArgumentException($"No layer '{layer}'." + AnimationErrors.Hint(layer, layers.Select(l => l.name), "Layers", "cliext.animator inspect"));
            return found;
        }

        private static string ParameterHint(AnimatorController c, string name) =>
            AnimationErrors.Hint(name, c.parameters.Select(p => p.name), "Parameters", "cliext.animator inspect");

        /// <summary>Every state in a state machine and its sub-machines, with its path (Sub/State).</summary>
        public static IEnumerable<(string path, AnimatorState state, AnimatorStateMachine owner)> AllStates(AnimatorStateMachine sm, string prefix = "")
        {
            foreach (var s in sm.states) yield return (prefix + s.state.name, s.state, sm);
            foreach (var sub in sm.stateMachines)
                foreach (var x in AllStates(sub.stateMachine, prefix + sub.stateMachine.name + "/")) yield return x;
        }

        public static IEnumerable<(string path, AnimatorStateMachine sm, AnimatorStateMachine parent)> AllMachines(AnimatorStateMachine sm, string prefix = "", AnimatorStateMachine parent = null)
        {
            yield return (prefix.TrimEnd('/'), sm, parent);
            foreach (var sub in sm.stateMachines)
                foreach (var x in AllMachines(sub.stateMachine, prefix + sub.stateMachine.name + "/", sm)) yield return x;
        }

        public static (AnimatorState state, AnimatorStateMachine owner) FindState(AnimatorStateMachine root, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("No state given.");
            string n = name.Trim();
            var all = AllStates(root).ToList();
            var match = all.FirstOrDefault(s => string.Equals(s.path, n, StringComparison.OrdinalIgnoreCase));
            if (match.state == null)
            {
                var byName = all.Where(s => string.Equals(s.state.name, n, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byName.Count > 1) throw new ArgumentException($"'{n}' names {byName.Count} states: {string.Join(", ", byName.Select(s => s.path))}. Use the path.");
                match = byName.FirstOrDefault();
            }
            if (match.state == null) throw new ArgumentException($"No state '{n}'." + AnimationErrors.Hint(n, all.Select(s => s.path), "States", "cliext.animator inspect"));
            return (match.state, match.owner);
        }

        public static Motion LoadMotion(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec) || spec == "null") return null;
            string path = spec, sub = null;
            int at = spec.LastIndexOf('@');
            if (at > 0 && !spec.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) { path = spec.Substring(0, at); sub = spec.Substring(at + 1); }
            var assets = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Motion>().Where(m => !m.name.StartsWith("__preview__")).ToList();
            Motion motion;
            if (AssetImporter.GetAtPath(path) is ModelImporter)
                // A model: a bare path names its clip only when it has one; a name two clips share names neither.
                motion = AnimationErrors.ModelClip(path, sub);
            else
                motion = sub == null ? (AssetDatabase.LoadAssetAtPath<Motion>(path) ?? assets.FirstOrDefault())
                                     : assets.FirstOrDefault(m => string.Equals(m.name, sub, StringComparison.OrdinalIgnoreCase));
            return motion ?? throw new ArgumentException($"No motion '{spec}'." + (assets.Count > 0
                ? $" Clips there: {string.Join(", ", assets.Select(m => m.name))}."
                : " Give an .anim path, or a model's clip as Assets/Model.fbx@Clip (cliext.anim takes lists a model's clips; animation.clip create makes an .anim)."));
        }

        private static string MotionName(Motion m) => m == null ? null : m is BlendTree ? $"BlendTree({m.name})" : $"{AssetDatabase.GetAssetPath(m)}@{m.name}";

        /// <summary>Sets fields or properties on <paramref name="target"/> from "name=value;name=value".</summary>
        public static List<string> SetMembers(object target, string pairs)
        {
            var done = new List<string>();
            if (string.IsNullOrWhiteSpace(pairs)) return done;
            var type = target.GetType();
            foreach (string pair in pairs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{pair}' isn't name=value.");
                string name = pair.Substring(0, eq).Trim(), text = pair.Substring(eq + 1).Trim();
                var prop = type.GetProperties().FirstOrDefault(p => p.CanWrite && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                // Structs such as AnimationClipSettings expose fields instead.
                var field = prop == null ? type.GetFields().FirstOrDefault(f => !f.IsInitOnly && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
                if (prop == null && field == null)
                    throw new ArgumentException($"{type.Name} has no settable '{name}'. It has: {string.Join(", ", type.GetProperties().Where(p => p.CanWrite && IsSimple(p.PropertyType)).Select(p => p.Name).Concat(type.GetFields().Where(f => IsSimple(f.FieldType)).Select(f => f.Name)))}");
                Type valueType = prop != null ? prop.PropertyType : field.FieldType;
                object v = valueType == typeof(Motion) ? LoadMotion(text)
                         : valueType == typeof(AvatarMask) ? (object)(text == "" || text == "null" ? null : AssetDatabase.LoadAssetAtPath<AvatarMask>(text) ?? throw new ArgumentException($"No AvatarMask at '{text}'. Create one with cliext.anim mask --path {text}."))
                         : ValueText.Parse(text, valueType);
                if (prop != null) prop.SetValue(target, v); else field.SetValue(target, v);
                done.Add(prop != null ? prop.Name : field.Name);
            }
            return done;
        }

        private static bool IsSimple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Motion) || t == typeof(AvatarMask);

        public static AnimatorCondition[] ParseConditions(AnimatorController c, string conditions)
        {
            var list = new List<AnimatorCondition>();
            if (string.IsNullOrWhiteSpace(conditions)) return list.ToArray();
            foreach (string part in conditions.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length < 2) throw new ArgumentException($"Condition '{part}' should be 'Parameter Mode [Threshold]' (If, IfNot, Greater, Less, Equals, NotEqual).");
                var param = c.parameters.FirstOrDefault(p => p.name == bits[0]) ?? throw new ArgumentException($"No parameter '{bits[0]}'." + ParameterHint(c, bits[0]));
                var mode = AnimationErrors.ParseEnum<AnimatorConditionMode>(bits[1], "condition mode");
                float threshold = bits.Length > 2 ? (float)ValueText.Parse(bits[2], typeof(float)) : 0;
                if (bits.Length < 3 && mode != AnimatorConditionMode.If && mode != AnimatorConditionMode.IfNot)
                    throw new ArgumentException($"Condition '{part.Trim()}' needs a threshold: '{param.name} {mode} 0.5'.");
                if ((mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot) && param.type != AnimatorControllerParameterType.Bool && param.type != AnimatorControllerParameterType.Trigger)
                    throw new ArgumentException($"{mode} needs a Bool or Trigger; {param.name} is {param.type}.");
                list.Add(new AnimatorCondition { parameter = param.name, mode = mode, threshold = threshold });
            }
            return list.ToArray();
        }

        public static AnimatorStateTransition FindTransition(AnimatorStateMachine root, string from, string to, int index)
        {
            IEnumerable<AnimatorStateTransition> candidates;
            if (string.Equals(from, "Any", StringComparison.OrdinalIgnoreCase) || string.Equals(from, "AnyState", StringComparison.OrdinalIgnoreCase))
                candidates = AllMachines(root).SelectMany(m => m.sm.anyStateTransitions);
            else candidates = FindState(root, from).state.transitions;
            bool toExit = string.Equals(to, "Exit", StringComparison.OrdinalIgnoreCase);
            AnimatorState target = toExit ? null : FindState(root, to).state;
            var matches = candidates.Where(t => toExit ? t.isExit : t.destinationState == target).ToList();
            if (matches.Count == 0) throw new ArgumentException($"No transition from {from} to {to}. cliext.animator inspect lists each state's transitions (Pipeline's animation.animator add_transition adds one).");
            if (index < 0 || index >= matches.Count) throw new ArgumentException($"{matches.Count} transition(s) from {from} to {to}; index {index} is out of range.");
            return matches[index];
        }

        // ---------------------------------------------------------------------------------------------------------

        public static object Apply(AnimatorController c, AnimatorOp op)
        {
            int li = LayerIndex(c, op.layer);
            var layers = c.layers;
            var root = layers[li].stateMachine;
            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "remove_state":
                {
                    var (state, owner) = FindState(root, op.state);
                    owner.RemoveState(state);
                    return new { op = "remove_state", state = op.state };
                }
                case "rename_state":
                {
                    var (state, owner) = FindState(root, op.state);
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("rename_state needs a name (the new name).");
                    string newName = op.name.Trim();
                    if (state.name == newName) throw new ArgumentException($"The state is already called '{newName}'.");
                    if (owner.states.Any(s => s.state != state && string.Equals(s.state.name, newName, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"There is already a state '{newName}' there; pick another name.");
                    state.name = newName;
                    return new { op = "rename_state", state = newName };
                }
                case "set_state":
                {
                    var (state, _) = FindState(root, op.state);
                    if (op.motion == null && string.IsNullOrWhiteSpace(op.set)) throw new ArgumentException("set_state needs a motion or set=\"name=value;...\" (speed, tag, writeDefaultValues, ...).");
                    if (op.motion != null) state.motion = LoadMotion(op.motion);
                    var done = SetMembers(state, op.set);
                    return new { op = "set_state", state = op.state, set = done, motion = MotionName(state.motion) };
                }
                case "set_default":
                {
                    var (state, owner) = FindState(root, op.state);
                    if (owner.defaultState == state) throw new ArgumentException($"'{op.state}' is already the default state.");
                    owner.defaultState = state;
                    return new { op = "set_default", state = op.state };
                }
                case "add_substatemachine":
                {
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("add_substatemachine needs a name (and state: the parent sub-state machine's path, or none for the layer).");
                    var machines = AllMachines(root).ToList();
                    var parent = string.IsNullOrWhiteSpace(op.state) ? root : machines.FirstOrDefault(m => string.Equals(m.path, op.state, StringComparison.OrdinalIgnoreCase)).sm
                                 ?? throw new ArgumentException($"No sub-state machine '{op.state}'." + AnimationErrors.Hint(op.state, machines.Select(m => m.path), "Sub-state machines", "cliext.animator inspect"));
                    if (parent.stateMachines.Any(s => string.Equals(s.stateMachine.name, op.name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"There is already a sub-state machine '{op.name.Trim()}' there.");
                    var sm = parent.AddStateMachine(op.name.Trim());
                    return new { op = "add_substatemachine", path = (string.IsNullOrWhiteSpace(op.state) ? "" : op.state + "/") + sm.name };
                }
                case "remove_substatemachine":
                {
                    var machines = AllMachines(root).ToList();
                    var m = machines.FirstOrDefault(x => string.Equals(x.path, op.state, StringComparison.OrdinalIgnoreCase));
                    if (m.sm == null || m.parent == null) throw new ArgumentException($"No sub-state machine '{op.state}'." + AnimationErrors.Hint(op.state, machines.Select(x => x.path), "Sub-state machines", "cliext.animator inspect"));
                    m.parent.RemoveStateMachine(m.sm);
                    return new { op = "remove_substatemachine", path = op.state };
                }
                case "set_transition":
                {
                    if (op.conditions == null && string.IsNullOrWhiteSpace(op.set)) throw new ArgumentException("set_transition needs conditions=\"Speed Greater 0.1;...\" (\"\" clears them) or set=\"hasExitTime=false;duration=0.1\".");
                    var t = FindTransition(root, op.from, op.to, op.index);
                    if (op.conditions != null) t.conditions = ParseConditions(c, op.conditions);
                    var done = SetMembers(t, op.set);
                    return new { op = "set_transition", from = op.from, to = op.to, set = done, conditions = t.conditions.Select(x => $"{x.parameter} {x.mode} {x.threshold}").ToArray() };
                }
                case "remove_transition":
                {
                    var t = FindTransition(root, op.from, op.to, op.index);
                    if (string.Equals(op.from, "Any", StringComparison.OrdinalIgnoreCase) || string.Equals(op.from, "AnyState", StringComparison.OrdinalIgnoreCase))
                        AllMachines(root).First(m => m.sm.anyStateTransitions.Contains(t)).sm.RemoveAnyStateTransition(t);
                    else FindState(root, op.from).state.RemoveTransition(t);
                    return new { op = "remove_transition", from = op.from, to = op.to };
                }
                case "remove_parameter":
                {
                    int pi = Array.FindIndex(c.parameters, p => p.name == op.name);
                    if (pi < 0) throw new ArgumentException($"No parameter '{op.name}'." + ParameterHint(c, op.name));
                    int stripped = 0;
                    foreach (var layer in c.layers)
                        foreach (var t in AllTransitions(layer.stateMachine))
                        {
                            var kept = t.conditions.Where(x => x.parameter != op.name).ToArray();
                            if (kept.Length != t.conditions.Length) { stripped += t.conditions.Length - kept.Length; t.conditions = kept; }
                        }
                    c.RemoveParameter(pi);
                    return new { op = "remove_parameter", name = op.name, conditionsRemoved = stripped };
                }
                case "set_parameter":
                {
                    var ps = c.parameters;
                    int pi = Array.FindIndex(ps, p => p.name == op.name);
                    if (pi < 0) throw new ArgumentException($"No parameter '{op.name}'." + ParameterHint(c, op.name));
                    var p = ps[pi];
                    if (p.type == AnimatorControllerParameterType.Trigger) throw new ArgumentException($"'{p.name}' is a Trigger: it has no default value.");
                    if (op.value == null) throw new ArgumentException($"set_parameter needs a value (the default for {p.type} '{p.name}').");
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float: p.defaultFloat = (float)ValueText.Parse(op.value, typeof(float)); break;
                        case AnimatorControllerParameterType.Int: p.defaultInt = (int)ValueText.Parse(op.value, typeof(int)); break;
                        default: p.defaultBool = (bool)ValueText.Parse(op.value, typeof(bool)); break;
                    }
                    c.parameters = ps;
                    return new { op = "set_parameter", name = op.name, value = op.value };
                }
                case "set_layer":
                {
                    if (string.IsNullOrWhiteSpace(op.set)) throw new ArgumentException("set_layer needs set=\"defaultWeight=1;blendingMode=Additive;...\".");
                    var layer = layers[li];
                    var done = SetMembers(layer, op.set);
                    c.layers = layers;
                    return new { op = "set_layer", layer = layer.name, set = done };
                }
                case "remove_layer":
                    if (string.IsNullOrWhiteSpace(op.layer)) throw new ArgumentException("remove_layer needs the layer (name or index); it doesn't default to the first.");
                    if (layers.Length == 1) throw new ArgumentException("A controller needs one layer.");
                    c.RemoveLayer(li);
                    return new { op = "remove_layer", layer = layers[li].name };
                case "add_blendtree":
                {
                    if (string.IsNullOrWhiteSpace(op.state)) throw new ArgumentException("add_blendtree needs the state's name.");
                    var blendType = string.IsNullOrEmpty(op.type) ? BlendTreeType.Simple1D : AnimationErrors.ParseEnum<BlendTreeType>(op.type, "blend tree type");
                    bool twoD = blendType != BlendTreeType.Simple1D && blendType != BlendTreeType.Direct;
                    foreach (var (key, given) in new[] { ("parameter", op.parameter), ("parameterY", op.parameterY) })
                    {
                        if (blendType == BlendTreeType.Direct || (key == "parameterY" && !twoD)) continue;
                        if (string.IsNullOrEmpty(given)) throw new ArgumentException($"A {blendType} blend tree needs {key} (a Float parameter).");
                        var bp = c.parameters.FirstOrDefault(x => x.name == given) ?? throw new ArgumentException($"No parameter '{given}' for the blend tree." + ParameterHint(c, given));
                        if (bp.type != AnimatorControllerParameterType.Float) throw new ArgumentException($"'{given}' is {bp.type}; a blend tree needs a Float parameter.");
                    }
                    // Children first, so a bad one fails before anything is added to the controller.
                    var kids = new List<(Motion motion, float[] at)>();
                    foreach (string child in (op.children ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int at = child.LastIndexOf('@');
                        if (at <= 0) throw new ArgumentException($"Child '{child}' should be clip@threshold or clip@x,y.");
                        var motion = LoadMotion(child.Substring(0, at).Trim());
                        var nums = child.Substring(at + 1).Split(',').Select(s => (float)ValueText.Parse(s.Trim(), typeof(float))).ToArray();
                        if (twoD && nums.Length < 2) throw new ArgumentException($"Child '{child}' needs a position x,y for a {blendType} blend tree.");
                        kids.Add((motion, nums));
                    }
                    BlendTree tree;
                    var existing = AllStates(root).FirstOrDefault(s => string.Equals(s.path, op.state, StringComparison.OrdinalIgnoreCase)).state;
                    if (existing != null)
                    {
                        tree = new BlendTree { name = op.state.Split('/').Last(), hideFlags = HideFlags.HideInHierarchy };
                        AssetDatabase.AddObjectToAsset(tree, c);
                        Undo.RegisterCreatedObjectUndo(tree, "add_blendtree");
                        existing.motion = tree;
                    }
                    else
                    {
                        var state = c.CreateBlendTreeInController(op.state, out tree, li);
                    }
                    tree.blendType = blendType;
                    if (!string.IsNullOrEmpty(op.parameter)) tree.blendParameter = op.parameter;
                    if (!string.IsNullOrEmpty(op.parameterY)) tree.blendParameterY = op.parameterY;
                    tree.useAutomaticThresholds = false;
                    foreach (var (motion, nums) in kids)
                    {
                        if (!twoD) tree.AddChild(motion, nums[0]);
                        else tree.AddChild(motion, new Vector2(nums[0], nums[1]));
                    }
                    return new { op = "add_blendtree", state = op.state, type = tree.blendType.ToString(), children = tree.children.Length };
                }
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: remove_state, rename_state, set_state, set_default, add_substatemachine, remove_substatemachine, set_transition, remove_transition, remove_parameter, set_parameter, set_layer, remove_layer, add_blendtree.");
            }
        }

        /// <summary>
        /// Applies every op and saves the controller once, or, when one fails, puts the controller back as it was on
        /// disk and in memory (states and transitions the batch removed included) and saves nothing.
        /// </summary>
        public static bool TryApplyAll(AnimatorController c, IList<AnimatorOp> ops, List<object> results, out int failedAt, out string error) =>
            TryApplyAll(c, ops, results, out failedAt, out error, out _);

        public static bool TryApplyAll(AnimatorController c, IList<AnimatorOp> ops, List<object> results, out int failedAt, out string error, out Exception failure)
        {
            failedAt = -1;
            error = null;
            using var rollback = AssetRollback.Begin(new[] { AssetDatabase.GetAssetPath(c) }, revertUndo: true);
            failure = null;
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(Apply(c, ops[i])); }
                catch (Exception e)
                {
                    failedAt = i;
                    failure = e;
                    error = AnimationErrors.Message(e);
                    results.Clear();
                    rollback.Rollback();
                    return false;
                }
            }
            EditorUtility.SetDirty(c);
            AssetDatabase.SaveAssetIfDirty(c);
            return true;
        }

        public static IEnumerable<AnimatorStateTransition> AllTransitions(AnimatorStateMachine root) =>
            AllStates(root).SelectMany(s => s.state.transitions).Concat(AllMachines(root).SelectMany(m => m.sm.anyStateTransitions));

        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Problems in a controller: missing motions, conditions on missing or mistyped parameters, unreachable states, transitions that can never fire.</summary>
        public static List<object> Validate(AnimatorController c)
        {
            var issues = new List<object>();
            void Add(string severity, string where, string problem) => issues.Add(new { severity, where, problem });
            var paramTypes = c.parameters.ToDictionary(p => p.name, p => p.type);
            var used = new HashSet<string>();
            foreach (var layer in c.layers)
            {
                var root = layer.stateMachine;
                var states = AllStates(root).ToList();
                if (states.Count == 0) { Add("warning", layer.name, "The layer has no states."); continue; }
                var reachable = new HashSet<AnimatorState>();
                var queue = new Queue<AnimatorState>();
                foreach (var m in AllMachines(root))
                {
                    if (m.sm.defaultState != null) queue.Enqueue(m.sm.defaultState);
                    foreach (var t in m.sm.anyStateTransitions) if (t.destinationState != null) queue.Enqueue(t.destinationState);
                    foreach (var t in m.sm.entryTransitions) if (t.destinationState != null) queue.Enqueue(t.destinationState);
                }
                while (queue.Count > 0)
                {
                    var s = queue.Dequeue();
                    if (!reachable.Add(s)) continue;
                    foreach (var t in s.transitions)
                    {
                        if (t.destinationState != null) queue.Enqueue(t.destinationState);
                        if (t.destinationStateMachine != null && t.destinationStateMachine.defaultState != null) queue.Enqueue(t.destinationStateMachine.defaultState);
                    }
                }
                foreach (var s in states)
                {
                    string where = $"{layer.name}/{s.path}";
                    if (s.state.motion == null && layer.syncedLayerIndex < 0) Add("warning", where, "No motion.");
                    if (s.state.motion is BlendTree bt)
                    {
                        if (bt.children.Length == 0) Add("warning", where, "The blend tree has no children.");
                        foreach (var p in new[] { bt.blendParameter, bt.blendType == BlendTreeType.Simple1D || bt.blendType == BlendTreeType.Direct ? null : bt.blendParameterY })
                        {
                            if (string.IsNullOrEmpty(p)) continue;
                            used.Add(p);
                            if (!paramTypes.TryGetValue(p, out var pt)) Add("error", where, $"The blend tree uses '{p}', which isn't a parameter.");
                            else if (pt != AnimatorControllerParameterType.Float) Add("error", where, $"The blend tree uses '{p}', a {pt}; blend parameters must be Float.");
                        }
                    }
                    if (!string.IsNullOrEmpty(s.state.speedParameter) && s.state.speedParameterActive) used.Add(s.state.speedParameter);
                    if (!reachable.Contains(s.state)) Add("warning", where, "Unreachable: no default, Entry, Any State or other transition leads here.");
                }
                var withSource = states.SelectMany(s => s.state.transitions.Select(t => (from: s.path, t)))
                    .Concat(AllMachines(root).SelectMany(m => m.sm.anyStateTransitions.Select(t => (from: "Any State", t))));
                foreach (var (from, t) in withSource)
                {
                    string where = $"{layer.name}: {from} -> {(t.isExit ? "Exit" : t.destinationState != null ? t.destinationState.name : t.destinationStateMachine?.name)}";
                    if (t.conditions.Length == 0 && !t.hasExitTime) Add("error", where, "No conditions and no exit time: it can never fire (Unity ignores it).");
                    foreach (var cond in t.conditions)
                    {
                        used.Add(cond.parameter);
                        if (!paramTypes.TryGetValue(cond.parameter, out var pt)) { Add("error", where, $"Condition on '{cond.parameter}', which isn't a parameter."); continue; }
                        bool boolMode = cond.mode == AnimatorConditionMode.If || cond.mode == AnimatorConditionMode.IfNot;
                        bool isBool = pt == AnimatorControllerParameterType.Bool || pt == AnimatorControllerParameterType.Trigger;
                        if (boolMode != isBool) Add("error", where, $"Condition '{cond.parameter} {cond.mode}' doesn't fit a {pt} parameter.");
                        if (pt == AnimatorControllerParameterType.Int && (cond.mode == AnimatorConditionMode.Greater || cond.mode == AnimatorConditionMode.Less) && cond.threshold != Mathf.Round(cond.threshold))
                            Add("warning", where, $"Int parameter '{cond.parameter}' compared with a fractional threshold {cond.threshold}.");
                    }
                    if (t.duration > 0 && t.hasExitTime && t.exitTime > 1 && t.destinationState == null && !t.isExit) Add("warning", where, "Exit time past the end of a non-looping state.");
                }
            }
            foreach (var p in c.parameters)
                if (!used.Contains(p.name)) issues.Add(new { severity = "info", where = "parameters", problem = $"'{p.name}' isn't used by any condition or blend tree (scripts may still set it)." });
            return issues;
        }

        public static object Describe(AnimatorController c) => new
        {
            parameters = c.parameters.Select(p => new { p.name, type = p.type.ToString(), @default = p.type == AnimatorControllerParameterType.Float ? ValueText.Format(p.defaultFloat) : p.type == AnimatorControllerParameterType.Int ? p.defaultInt.ToString() : ValueText.Format(p.defaultBool) }).ToArray(),
            layers = c.layers.Select((l, i) => new
            {
                index = i,
                l.name,
                weight = l.defaultWeight,
                blending = l.blendingMode.ToString(),
                mask = l.avatarMask != null ? AssetDatabase.GetAssetPath(l.avatarMask) : null,
                synced = l.syncedLayerIndex >= 0 ? (int?)l.syncedLayerIndex : null,
                ik = l.iKPass,
                defaultState = l.stateMachine.defaultState?.name,
                states = AllStates(l.stateMachine).Select(s => new
                {
                    s.path,
                    motion = MotionName(s.state.motion),
                    blendTree = s.state.motion is BlendTree bt ? new { type = bt.blendType.ToString(), parameter = bt.blendParameter, parameterY = bt.blendParameterY, children = bt.children.Select(ch => $"{MotionName(ch.motion)} @ {(bt.blendType == BlendTreeType.Simple1D ? ValueText.Format(ch.threshold) : ValueText.Format(ch.position))}").ToArray() } : null,
                    speed = s.state.speed,
                    speedParameter = s.state.speedParameterActive ? s.state.speedParameter : null,
                    s.state.tag,
                    s.state.writeDefaultValues,
                    behaviours = s.state.behaviours.Select(b => b.GetType().Name).ToArray(),
                    transitions = s.state.transitions.Select(t => DescribeTransition(t)).ToArray()
                }).ToArray(),
                anyState = AllMachines(l.stateMachine).SelectMany(m => m.sm.anyStateTransitions).Select(t => DescribeTransition(t)).ToArray(),
                subMachines = AllMachines(l.stateMachine).Where(m => m.path.Length > 0).Select(m => m.path).ToArray()
            }).ToArray()
        };

        private static object DescribeTransition(AnimatorStateTransition t) => new
        {
            to = t.isExit ? "Exit" : t.destinationState != null ? t.destinationState.name : t.destinationStateMachine?.name,
            conditions = t.conditions.Select(x => x.mode == AnimatorConditionMode.If || x.mode == AnimatorConditionMode.IfNot ? $"{x.parameter} {x.mode}" : $"{x.parameter} {x.mode} {ValueText.Format(x.threshold)}").ToArray(),
            hasExitTime = t.hasExitTime,
            exitTime = t.hasExitTime ? (float?)t.exitTime : null,
            duration = t.duration,
            fixedDuration = t.hasFixedDuration,
            offset = t.offset,
            interruption = t.interruptionSource == TransitionInterruptionSource.None ? null : t.interruptionSource.ToString()
        };
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace UnityCLIExtensions.Project
{
    /// <summary>
    /// <c>cliext.physics</c>, <c>cliext.navmesh</c> and <c>cliext.quality</c>: the project settings Pipeline's settings
    /// commands leave out: the layer collision matrix, physics materials and raycasts; NavMesh areas, agents and path
    /// queries; quality levels with every setting, platform defaults and graphics APIs.
    /// </summary>
    /// <remarks>
    /// Every argument is resolved before anything changes. An unknown layer, area, agent type, quality level, platform,
    /// setting or enum name is invalid usage (exit 2) with "Did you mean" and the valid names; a missing asset, folder or
    /// NavMesh is not found (exit 13) and says how to make one; a change that would change nothing exits 6.
    /// </remarks>
    internal static class ProjectSettingsCommands
    {
        private static ExitCode Report(CommandContext cmd, Exception e)
        {
            cmd.Fail(AnimationErrors.Message(e));
            return e is FormatException || e is OverflowException ? ExitCode.InvalidUsage : AnimationErrors.Exit(e);
        }

        private static bool IsArgError(Exception e) => e is ArgumentException || e is FormatException || e is OverflowException;

        /// <summary>" Did you mean: x? Kinds: a, b, c. command lists them." (Did you mean only when a name is close.)</summary>
        internal static string NameHint(string name, IEnumerable<string> candidates, string kinds, string listCommand)
        {
            var all = candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            var close = AnimationErrors.Close(name, all);
            string mean = close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "";
            string list = all.Count == 0 ? $" There are no {kinds.ToLowerInvariant()}." : $" {kinds}: {string.Join(", ", all)}.";
            return mean + list + (listCommand != null ? $" {listCommand} lists them." : "");
        }

        private static Vector3 Vec(string text, string arg)
        {
            try { return (Vector3)ValueText.Parse(text, typeof(Vector3)); }
            catch (ArgumentException e) { throw new AnimUsageException($"{arg}: {e.Message}"); }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Physics

        private static string MatrixAsset(bool twoD) => twoD ? "ProjectSettings/Physics2DSettings.asset" : "ProjectSettings/DynamicsManager.asset";

        private static IEnumerable<string> LayerNames() => Enumerable.Range(0, 32).Select(LayerMask.LayerToName).Where(l => l.Length > 0);

        /// <summary>A layer by name or index 0-31; anything else is invalid usage naming the layers.</summary>
        internal static int Layer(string name, string arg = "layer")
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) throw new AnimUsageException($"{arg}: give a layer name or index. Layers: {string.Join(", ", LayerNames())}.");
            if (int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            {
                if (i < 0 || i > 31) throw new AnimUsageException($"{arg}: layer {i} is outside 0-31. Layers: {string.Join(", ", LayerNames())}.");
                return i;
            }
            i = LayerMask.NameToLayer(n);
            if (i < 0) throw new AnimUsageException($"{arg}: no layer '{n}'.{NameHint(n, LayerNames(), "Layers", "cliext.physics matrix")}");
            return i;
        }

        private static string LayerLabel(int i) => LayerMask.LayerToName(i) is string s && s.Length > 0 ? s : $"layer {i}";

        /// <summary>Layer indexes as compact ranges: "3, 6-7, 10-31"; null for none.</summary>
        internal static string Ranges(IEnumerable<int> indexes)
        {
            var list = indexes.OrderBy(i => i).ToList();
            if (list.Count == 0) return null;
            var parts = new List<string>();
            for (int i = 0; i < list.Count;)
            {
                int j = i;
                while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
                parts.Add(j == i ? list[i].ToString(CultureInfo.InvariantCulture) : j == i + 1 ? $"{list[i]}, {list[j]}" : $"{list[i]}-{list[j]}");
                i = j + 1;
            }
            return string.Join(", ", parts);
        }

        [CliGroupCommand("cliext.physics", "The layer collision matrix (3D, or --2d true): for each named layer, the named layers it does not collide with (ignores), " +
                                           "and unnamedIgnored, the unnamed layers (by index, such as \"3, 6-7, 10-31\") it doesn't collide with either; null when it collides with all of them. " +
                                           "--all true lists the unnamed layers' rows too (as \"layer 12\").",
            Name = "matrix", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Matrix(CommandContext cmd, [CliArg("2d", "The 2D matrix")] bool twoD = false, [CliArg("all", "Also list the unnamed layers' rows")] bool all = false)
        {
            var so = SerializedText.Settings(MatrixAsset(twoD));
            var m = so.FindProperty("m_LayerCollisionMatrix");
            var named = Enumerable.Range(0, 32).Where(i => LayerMask.LayerToName(i).Length > 0).ToList();
            var unnamed = Enumerable.Range(0, 32).Except(named).ToList();
            foreach (int a in all ? Enumerable.Range(0, 32) : named)
            {
                uint bits = (uint)m.GetArrayElementAtIndex(a).longValue;
                var ignores = named.Where(b => (bits & (1u << b)) == 0).Select(LayerMask.LayerToName).ToArray();
                cmd.Result.WriteRecord(new { layer = LayerLabel(a), index = a, ignores, unnamedIgnored = Ranges(unnamed.Where(b => (bits & (1u << b)) == 0)) });
            }
            return ExitCode.Success;
        }

        private const string TagAsset = "ProjectSettings/TagManager.asset";

        /// <summary>Unity's built-in layers, whose names can't change: 0 Default, 1 TransparentFX, 2 Ignore Raycast, 4 Water, 5 UI.</summary>
        internal static readonly int[] BuiltinLayers = { 0, 1, 2, 4, 5 };

        /// <summary>
        /// The 32 layer names after --set "8=Player;9=Pickup" (none clears an index) on <paramref name="current"/>, with each
        /// change as "8: (none) -> Player". A built-in, used or repeated index, a name another layer has, a bad pair or an
        /// index outside 0-31 is invalid usage; all of them already set is an ArgumentException (nothing would change).
        /// </summary>
        internal static string[] PlanLayers(string set, string[] current, out List<string> changes)
        {
            var next = (string[])current.Clone();
            changes = new List<string>();
            var seen = new HashSet<int>();
            var pairs = (set ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (pairs.Count == 0) throw new AnimUsageException("--set: give index=name pairs such as \"8=Player;9=Pickup\" (none clears one).");
            string Free() => Ranges(Enumerable.Range(3, 29).Where(i => !BuiltinLayers.Contains(i) && string.IsNullOrEmpty(next[i]))) ?? "none";
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new AnimUsageException($"--set: '{pair}' isn't index=name (such as 8=Player).");
                string idx = pair.Substring(0, eq).Trim(), name = pair.Substring(eq + 1).Trim();
                if (!int.TryParse(idx, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i < 0 || i > 31)
                    throw new AnimUsageException($"--set: '{idx}' isn't a layer index 0-31 (user layers are 3 and 6-31).");
                if (!seen.Add(i)) throw new AnimUsageException($"--set names layer {i} twice.");
                if (BuiltinLayers.Contains(i))
                    throw new AnimUsageException($"--set: layer {i} is Unity's built-in {current[i]}, whose name can't change: name a user layer (free: {Free()}).");
                bool clear = string.Equals(name, "none", StringComparison.OrdinalIgnoreCase) || name.Length == 0;
                if (clear)
                {
                    if (!string.IsNullOrEmpty(next[i])) { changes.Add($"{i}: {next[i]} -> (none)"); next[i] = ""; }
                    continue;
                }
                if (string.Equals(next[i], name, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(next[i]))
                    throw new AnimUsageException($"--set: layer {i} is already {next[i]}: pick a free index (free: {Free()}), or clear it first with {i}=none.");
                int other = Array.FindIndex(next, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (other >= 0) throw new AnimUsageException($"--set: layer {other} is already named {next[other]}: layer names are unique.");
                changes.Add($"{i}: (none) -> {name}");
                next[i] = name;
            }
            if (changes.Count == 0) throw new ArgumentException($"The layers already have {string.Join(";", pairs)}: nothing would change.");
            return next;
        }

        [CliGroupCommand("cliext.physics", "The 32 layers (index, name, builtin), or with --set \"8=Player;9=Pickup\" name user layers (3, 6-31; 8=none clears one), saved to ProjectSettings/TagManager.asset. " +
                                           "A built-in index (0, 1, 2, 4, 5), an index that already has another name (clear it first with 8=none), a name another layer has, " +
                                           "an index given twice or outside 0-31 exits 2 naming the free indexes; names the layers already have exit 6.",
            Name = "layers", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Layers(CommandContext cmd, [CliArg("set", "index=name pairs separated by ';'")] string set = "")
        {
            try
            {
                var so = SerializedText.Settings(TagAsset);
                var layers = so.FindProperty("layers");
                var current = Enumerable.Range(0, 32).Select(i => i < layers.arraySize ? layers.GetArrayElementAtIndex(i).stringValue : "").ToArray();
                if (string.IsNullOrWhiteSpace(set))
                {
                    for (int i = 0; i < 32; i++)
                        cmd.Result.WriteRecord(new { index = i, name = current[i].Length > 0 ? current[i] : null, builtin = BuiltinLayers.Contains(i) });
                    return ExitCode.Success;
                }
                var next = PlanLayers(set, current, out var changes);
                for (int i = 0; i < 32; i++) layers.GetArrayElementAtIndex(i).stringValue = next[i];
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { changes, saved = TagAsset });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        /// <summary>
        /// The matrix rows after making layer <paramref name="a"/> collide with exactly the layers in <paramref name="keep"/>
        /// (a bit mask) and ignore every other one, both directions. <paramref name="changed"/> gets the layers whose pair with a changes.
        /// </summary>
        internal static uint[] OnlyRows(uint[] rows, int a, uint keep, out List<int> changed)
        {
            var next = (uint[])rows.Clone();
            changed = new List<int>();
            for (int b = 0; b < 32; b++)
            {
                bool want = (keep & (1u << b)) != 0;
                bool now = (rows[a] & (1u << b)) != 0;
                if (want != now) changed.Add(b);
                next[a] = want ? next[a] | (1u << b) : next[a] & ~(1u << b);
                next[b] = want ? next[b] | (1u << a) : next[b] & ~(1u << a);
            }
            return next;
        }

        [CliGroupCommand("cliext.physics", "Make two layers collide or not (--a, --b, --collide; both directions), or with --only \"Player;Enemy\" make --a collide with just those layers and ignore all 31 others " +
                                           "(named or not; list --a itself to keep it colliding with its own layer). In the 3D matrix or with --2d true the 2D one. Saved to the project settings. " +
                                           "An unknown layer exits 2 with the layer names (cliext.physics matrix lists them and what each ignores), as does --only with --b or --collide, or neither; " +
                                           "layers that already collide (or ignore each other), or an --only the matrix already has, exit 6.",
            Name = "set_matrix", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetMatrix(
            CommandContext cmd,
            [CliArg("a", "A layer (name or index)", Required = true)] string a,
            [CliArg("b", "The other layer")] string b = "",
            [CliArg("collide", "true to collide, false to ignore")] bool? collide = null,
            [CliArg("only", "The only layers --a collides with, separated by ';'")] string only = "",
            [CliArg("2d", "The 2D matrix")] bool twoD = false)
        {
            try
            {
                bool hasOnly = !string.IsNullOrWhiteSpace(only), hasB = !string.IsNullOrWhiteSpace(b);
                if (hasOnly && (hasB || collide.HasValue)) throw new AnimUsageException("--only sets every pair for --a: leave out --b and --collide.");
                if (!hasOnly && (!hasB || !collide.HasValue))
                    throw new AnimUsageException("Pass --b and --collide true|false for one pair, or --only \"<layers>\" for the only layers --a collides with.");
                int la = Layer(a, "--a");
                if (hasOnly)
                {
                    uint keep = 0;
                    foreach (string l in only.Split(';').Where(x => x.Trim().Length > 0)) keep |= 1u << Layer(l, "--only");
                    var sm = SerializedText.Settings(MatrixAsset(twoD));
                    var mp = sm.FindProperty("m_LayerCollisionMatrix");
                    var rows = Enumerable.Range(0, 32).Select(i => (uint)mp.GetArrayElementAtIndex(i).longValue).ToArray();
                    var next = OnlyRows(rows, la, keep, out var changed);
                    string keepText = string.Join(", ", Enumerable.Range(0, 32).Where(i => (keep & (1u << i)) != 0).Select(LayerLabel));
                    if (changed.Count == 0)
                        throw new ArgumentException($"{LayerLabel(la)} already collides only with {keepText} in the {(twoD ? "2D" : "3D")} matrix: nothing would change.");
                    for (int i = 0; i < 32; i++) mp.GetArrayElementAtIndex(i).longValue = next[i];
                    sm.ApplyModifiedPropertiesWithoutUndo();
                    foreach (int x in changed)
                    {
                        bool ignore = (keep & (1u << x)) == 0;
                        if (twoD) Physics2D.IgnoreLayerCollision(la, x, ignore); else Physics.IgnoreLayerCollision(la, x, ignore);
                    }
                    AssetDatabase.SaveAssets();
                    cmd.Result.Write(new { a = LayerLabel(la), collidesOnlyWith = keepText.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries), changed = changed.Select(LayerLabel).ToArray(), matrix = twoD ? "2D" : "3D" });
                    return ExitCode.Success;
                }
                bool on = collide.Value;
                int lb = Layer(b, "--b");
                var so = SerializedText.Settings(MatrixAsset(twoD));
                var m = so.FindProperty("m_LayerCollisionMatrix");
                bool collidesNow = ((uint)m.GetArrayElementAtIndex(la).longValue & (1u << lb)) != 0;
                if (collidesNow == on)
                    throw new ArgumentException($"{LayerLabel(la)} and {LayerLabel(lb)} already {(on ? "collide" : "ignore each other")} in the {(twoD ? "2D" : "3D")} matrix: nothing would change.");
                void Set(int row, int col)
                {
                    var e = m.GetArrayElementAtIndex(row);
                    uint bits = (uint)e.longValue;
                    bits = on ? bits | (1u << col) : bits & ~(1u << col);
                    e.longValue = bits;
                }
                Set(la, lb); Set(lb, la);
                so.ApplyModifiedPropertiesWithoutUndo();
                if (twoD) Physics2D.IgnoreLayerCollision(la, lb, !on); else Physics.IgnoreLayerCollision(la, lb, !on);
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { a = LayerLabel(la), b = LayerLabel(lb), collide = on, matrix = twoD ? "2D" : "3D" });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        internal static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        private static IEnumerable<GameObject> SceneObjects() =>
            Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.scene.IsValid() && (g.hideFlags & HideFlags.HideInHierarchy) == 0);

        /// <summary>A GameObject in the open scenes by hierarchy path (a leading / is allowed) or name, inactive ones too; unknown is not found with "Did you mean".</summary>
        internal static GameObject FindSceneObject(string name, string arg)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) throw new AnimUsageException($"{arg}: give a GameObject's name or hierarchy path.");
            string path = n.TrimStart('/');
            var all = SceneObjects().ToList();
            var go = all.FirstOrDefault(g => PathOf(g.transform) == path) ?? all.FirstOrDefault(g => string.Equals(g.name, path, StringComparison.OrdinalIgnoreCase))
                     ?? all.FirstOrDefault(g => string.Equals(PathOf(g.transform), path, StringComparison.OrdinalIgnoreCase));
            if (go != null) return go;
            var close = AnimationErrors.Close(path, all.Select(g => g.name).Distinct());
            throw new AnimNotFoundException($"{arg}: no GameObject '{n}' in the open scenes.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Give its name or hierarchy path (scene get_hierarchy lists them).");
        }

        /// <summary>The colliders on the object (3D Collider, or Collider2D with <paramref name="twoD"/>); none is not found, saying how to add one.</summary>
        internal static List<Component> ColliderTargets(string obj, bool twoD)
        {
            var go = FindSceneObject(obj, "--assign");
            var list = twoD ? go.GetComponents<Collider2D>().Cast<Component>().ToList() : go.GetComponents<Collider>().Cast<Component>().ToList();
            if (list.Count == 0)
            {
                bool other = twoD ? go.GetComponent<Collider>() != null : go.GetComponent<Collider2D>() != null;
                throw new AnimNotFoundException($"--assign: '{PathOf(go.transform)}' has no {(twoD ? "Collider2D" : "Collider")}" +
                                                (other ? $" (it has a {(twoD ? "3D Collider: leave out --2d" : "Collider2D: pass --2d true with a .physicsMaterial2D")})." : $": add one first (component add --type {(twoD ? "BoxCollider2D" : "BoxCollider")}), or name the object that has it."));
            }
            return list;
        }

        private static UnityEngine.Object SharedMaterial(Component c) => c is Collider c3 ? (UnityEngine.Object)c3.sharedMaterial : ((Collider2D)c).sharedMaterial;

        private static readonly string[] Material3DFields ={ "dynamicFriction", "staticFriction", "bounciness", "frictionCombine", "bounceCombine" };
        private static readonly string[] Material2DFields = { "friction", "bounciness" };

        /// <summary>Parses "name=value;..." for a physics material into field -> value, checking names and ranges.</summary>
        internal static Dictionary<string, object> ParseMaterialSet(string set, bool twoD)
        {
            var fields = twoD ? Material2DFields : Material3DFields;
            var values = new Dictionary<string, object>();
            foreach (string pair in (set ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.Trim().Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new AnimUsageException($"'{pair.Trim()}' isn't name=value.");
                string given = pair.Substring(0, eq).Trim(), text = pair.Substring(eq + 1).Trim();
                string name = fields.FirstOrDefault(f => string.Equals(f, given, StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    var close = AnimationErrors.Close(given, fields);
                    throw new AnimUsageException($"A {(twoD ? "PhysicsMaterial2D" : "PhysicsMaterial")} has no '{given}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Settings: {string.Join(", ", fields)}.");
                }
                object v;
                if (name.EndsWith("Combine")) v = AnimationErrors.ParseEnum<PhysicsMaterialCombine>(text, $"{name} value");
                else
                {
                    float f;
                    try { f = (float)ValueText.Parse(text, typeof(float)); }
                    catch (ArgumentException e) { throw new AnimUsageException($"{name}: {e.Message}"); }
                    if (float.IsNaN(f) || f < 0 || (name == "bounciness" && f > 1))
                        throw new AnimUsageException($"{name} {text} is out of range: give {(name == "bounciness" ? "0 to 1" : "0 or more")}.");
                    v = f;
                }
                values[name] = v;
            }
            return values;
        }

        private static object MaterialValue(UnityEngine.Object mat, string field)
        {
            var p = mat.GetType().GetProperty(field);
            return p.GetValue(mat);
        }

        [CliGroupCommand("cliext.physics", "Create or change a physics material (--path .physicMaterial, or .physicsMaterial2D with --2d true): --set \"dynamicFriction=0.2;staticFriction=0.3;bounciness=0.5;frictionCombine=Minimum;bounceCombine=Maximum\" (2D: friction, bounciness). " +
                                           "Frictions are 0 or more, bounciness 0 to 1, combines Average, Minimum, Multiply or Maximum. Everything is checked before the asset is made or changed: " +
                                           "a path outside Assets/ or Packages/, the wrong extension or an asset of another type exits 2, as do an unknown setting or a bad value; a missing folder exits 13; " +
                                           "an existing material with no --set and no --assign exits 2, and values it already has exit 6. " +
                                           "--assign <object> (a name or hierarchy path in the open scenes) also puts it on that object's colliders (Collider, or Collider2D with --2d true), not its children's; " +
                                           "an unknown object or one with no collider exits 13 (add one with component add), and colliders that already have it, with no --set change, exit 6.",
            Name = "material", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Material(
            CommandContext cmd,
            [CliArg("path", "The material asset", Required = true)] string path,
            [CliArg("set", "Settings: name=value;...")] string set = "",
            [CliArg("2d", "A 2D physics material")] bool twoD = false,
            [CliArg("assign", "A GameObject (name or hierarchy path) whose colliders get the material")] string assign = "")
        {
            try
            {
                bool hasAssign = !string.IsNullOrWhiteSpace(assign);
                var colliders = hasAssign ? ColliderTargets(assign, twoD) : new List<Component>();
                path = AnimationErrors.AssetPath(path);
                Type type = twoD ? typeof(PhysicsMaterial2D) : typeof(PhysicsMaterial);
                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                    throw new AnimUsageException($"--path '{path}' isn't under Assets/ or Packages/.");
                string ext = Path.GetExtension(path);
                if (twoD && !string.Equals(ext, ".physicsMaterial2D", StringComparison.OrdinalIgnoreCase))
                    throw new AnimUsageException($"--path '{path}': a 2D physics material ends in .physicsMaterial2D{(ext.StartsWith(".physic", StringComparison.OrdinalIgnoreCase) ? " (for a 3D one, leave out --2d)" : "")}.");
                if (!twoD && !string.Equals(ext, ".physicMaterial", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".physicsMaterial", StringComparison.OrdinalIgnoreCase))
                    throw new AnimUsageException($"--path '{path}': a physics material ends in .physicMaterial{(string.Equals(ext, ".physicsMaterial2D", StringComparison.OrdinalIgnoreCase) ? " (for a 2D one, pass --2d true)" : "")}.");
                var main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main != null && !type.IsInstanceOfType(main))
                    throw new AnimUsageException($"'{path}' is {AnimationErrors.A(main.GetType().Name)}, not {AnimationErrors.A(type.Name)}.");
                if (main == null)
                {
                    string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                        throw new AnimNotFoundException($"No folder '{folder}' for --path: create it first (asset create_folder, or AssetDatabase.CreateFolder).");
                }
                var values = ParseMaterialSet(set, twoD);
                if (main != null && values.Count == 0 && !hasAssign)
                    throw new AnimUsageException($"Nothing to change on '{path}': pass --set \"{(twoD ? "friction=0.4;bounciness=0" : "dynamicFriction=0.6;bounciness=0")}\", or --assign <object> to put it on a collider.");
                bool valuesSame = main != null && values.All(kv => Equals(MaterialValue(main, kv.Key), kv.Value));
                bool assignedSame = colliders.All(c => SharedMaterial(c) == main && main != null);
                if (valuesSame && (!hasAssign || assignedSame))
                {
                    string have = values.Count > 0 ? $"'{path}' already has {string.Join(", ", values.Select(kv => $"{kv.Key} {ValueText.Format(kv.Value)}"))}" : $"'{path}'";
                    throw new ArgumentException(hasAssign ? $"{have}, and {colliders[0].gameObject.name}'s collider(s) already use it: nothing would change." : $"{have}: nothing would change.");
                }

                bool created = main == null;
                var mat = main ?? (UnityEngine.Object)Activator.CreateInstance(type);
                if (!created) Undo.RecordObject(mat, "cliext.physics material");
                var done = new List<string>();
                foreach (var kv in values)
                {
                    if (Equals(MaterialValue(mat, kv.Key), kv.Value)) continue;
                    mat.GetType().GetProperty(kv.Key).SetValue(mat, kv.Value);
                    done.Add(kv.Key);
                }
                if (created) AssetDatabase.CreateAsset(mat, path);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssetIfDirty(mat);
                var assigned = new List<string>();
                foreach (var c in colliders)
                {
                    if (SharedMaterial(c) == mat) continue;
                    Undo.RecordObject(c, "cliext.physics material --assign");
                    if (c is Collider c3) c3.sharedMaterial = (PhysicsMaterial)mat; else ((Collider2D)c).sharedMaterial = (PhysicsMaterial2D)mat;
                    EditorUtility.SetDirty(c);
                    if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
                    assigned.Add($"{PathOf(c.transform)} {c.GetType().Name}");
                }
                string[] assignedTo = hasAssign ? assigned.ToArray() : null;
                object describe = mat is PhysicsMaterial m3
                    ? (object)new { path, created, m3.dynamicFriction, m3.staticFriction, m3.bounciness, frictionCombine = m3.frictionCombine.ToString(), bounceCombine = m3.bounceCombine.ToString(), set = done, assigned = assignedTo }
                    : new { path, created, ((PhysicsMaterial2D)mat).friction, ((PhysicsMaterial2D)mat).bounciness, set = done, assigned = assignedTo };
                cmd.Result.Write(describe);
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        [CliGroupCommand("cliext.physics", "Cast a ray in the open scenes (Edit or Play mode): --from [x,y,z] --direction [x,y,z] (or --to [x,y,z]), --distance, --layers (names separated by ';'), --all true for every hit. " +
                                           "Returns each hit's object, collider, point, normal, distance and layer. A ray that hits nothing exits 13; a bad vector, a zero direction, " +
                                           "both --direction and --to, a distance of 0 or less or an unknown layer exits 2.",
            Name = "raycast", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Raycast(
            CommandContext cmd,
            [CliArg("from", "Origin [x,y,z]", Required = true)] string from,
            [CliArg("direction", "Direction [x,y,z] (default [0,-1,0], straight down)")] string direction = "",
            [CliArg("to", "A target point instead of a direction (the distance defaults to the target's)")] string to = "",
            [CliArg("distance", "Max distance")] float distance = 1000,
            [CliArg("layers", "Layer names separated by ';' (default: all)")] string layers = "",
            [CliArg("all", "Every hit, not just the first")] bool all = false)
        {
            try
            {
                var origin = Vec(from, "--from");
                if (!string.IsNullOrWhiteSpace(to) && !string.IsNullOrWhiteSpace(direction))
                    throw new AnimUsageException("Give --direction or --to, not both.");
                Vector3 dir;
                if (!string.IsNullOrWhiteSpace(to))
                {
                    dir = Vec(to, "--to") - origin;
                    if (dir.sqrMagnitude < 1e-12f) throw new AnimUsageException("--to is the same point as --from: give a target somewhere else, or --direction.");
                    if (distance == 1000) distance = dir.magnitude;
                }
                else
                {
                    dir = string.IsNullOrWhiteSpace(direction) ? Vector3.down : Vec(direction, "--direction");
                    if (dir.sqrMagnitude < 1e-12f) throw new AnimUsageException("--direction [0,0,0] has no direction: give one such as [0,-1,0].");
                }
                if (!(distance > 0)) throw new AnimUsageException($"--distance {ValueText.Format(distance)}: give more than 0.");
                var layerList = string.IsNullOrWhiteSpace(layers) ? new List<int>() : layers.Split(';').Where(l => l.Trim().Length > 0).Select(l => Layer(l, "--layers")).ToList();
                int mask = layerList.Count == 0 ? Physics.DefaultRaycastLayers : layerList.Aggregate(0, (x, y) => x | (1 << y));
                Physics.SyncTransforms();
                var hits = all ? Physics.RaycastAll(origin, dir.normalized, distance, mask, QueryTriggerInteraction.Collide).OrderBy(h => h.distance).ToArray()
                               : (Physics.Raycast(origin, dir.normalized, out RaycastHit h1, distance, mask, QueryTriggerInteraction.Collide) ? new[] { h1 } : Array.Empty<RaycastHit>());
                if (hits.Length == 0)
                {
                    cmd.Fail($"The ray from {ValueText.Format(origin)} along {ValueText.Format(dir.normalized)} hit nothing within {ValueText.Format(distance)}" +
                             $"{(layerList.Count > 0 ? $" on {string.Join(", ", layerList.Select(LayerLabel))}" : "")}: aim it at a collider, raise --distance{(layerList.Count > 0 ? " or change --layers" : "")}.");
                    return ExitCode.NotFound;
                }
                foreach (var h in hits)
                    cmd.Result.WriteRecord(new { gameObject = h.collider.gameObject.name, collider = h.collider.GetType().Name, trigger = h.collider.isTrigger ? true : (bool?)null, point = ValueText.Format(h.point), normal = ValueText.Format(h.normal), distance = Math.Round(h.distance, 4), layer = LayerMask.LayerToName(h.collider.gameObject.layer) });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        // ---------------------------------------------------------------------------------------------------------
        // NavMesh

        private const string NavAsset = "ProjectSettings/NavMeshAreas.asset";

        private static SerializedProperty AgentSettings(SerializedObject so, int agentTypeId)
        {
            var settings = so.FindProperty("m_Settings");
            for (int i = 0; i < settings.arraySize; i++)
                if (settings.GetArrayElementAtIndex(i).FindPropertyRelative("agentTypeID").intValue == agentTypeId) return settings.GetArrayElementAtIndex(i);
            return null;
        }

        [CliGroupCommand("cliext.navmesh", "The NavMesh areas (index, name, cost) and agent types (id, name, radius, height, max slope, step height, drop and jump) of the project.",
            Name = "settings", Tags = new[] { "cliext", "navmesh" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode NavSettings(CommandContext cmd)
        {
            var so = SerializedText.Settings(NavAsset);
            var areas = so.FindProperty("areas");
            cmd.Result.Write(new
            {
                areas = Enumerable.Range(0, areas.arraySize).Select(i => new { index = i, name = areas.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue, cost = areas.GetArrayElementAtIndex(i).FindPropertyRelative("cost").floatValue })
                    .Where(a => a.name.Length > 0).ToArray(),
                agents = Enumerable.Range(0, NavMesh.GetSettingsCount()).Select(i => NavMesh.GetSettingsByIndex(i)).Select(s =>
                {
                    var sp = AgentSettings(so, s.agentTypeID);
                    return new
                    {
                        id = s.agentTypeID, name = NavMesh.GetSettingsNameFromID(s.agentTypeID),
                        radius = s.agentRadius, height = s.agentHeight, maxSlope = s.agentSlope, stepHeight = s.agentClimb,
                        drop = sp?.FindPropertyRelative("ledgeDropHeight")?.floatValue, jump = sp?.FindPropertyRelative("maxJumpAcrossDistance")?.floatValue,
                        voxelSize = s.overrideVoxelSize ? (float?)s.voxelSize : null
                    };
                }).ToArray()
            });
            return ExitCode.Success;
        }

        private static IEnumerable<string> AgentNames() => Enumerable.Range(0, NavMesh.GetSettingsCount()).Select(i => NavMesh.GetSettingsNameFromID(NavMesh.GetSettingsByIndex(i).agentTypeID));

        private static readonly string[] AgentReadOnly = { "agentTypeID", "debug" };

        [CliGroupCommand("cliext.navmesh", "Name an area and set its cost (--index 3-31 for user areas; 0-2 are Walkable, Not Walkable, Jump, whose cost can change but not their name), " +
                                           "or change an agent type (--agent name or id, --set \"agentRadius=0.4;agentHeight=1.8;agentSlope=45;agentClimb=0.4;ledgeDropHeight=0;maxJumpAcrossDistance=0\"). " +
                                           "Costs are 1 or more; radius and height more than 0; slope 0-60. Everything is checked before anything is saved: an unknown agent or setting, " +
                                           "a bad index or value, or nothing to change exits 2; values the project already has exit 6.",
            Name = "set", Tags = new[] { "cliext", "navmesh" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode NavSet(
            CommandContext cmd,
            [CliArg("index", "Index of the area to change, 0-31 (default -1: no area change)")] int index = -1,
            [CliArg("name", "New name for the area at --index (3-31); none clears it")] string name = "",
            [CliArg("cost", "New cost for the area at --index, 1 or more (default -1: keep it)")] float cost = -1,
            [CliArg("agent", "Agent type name or id")] string agent = "",
            [CliArg("set", "Agent settings: name=value;...")] string set = "")
        {
            try
            {
                bool areaChange = index != -1, agentChange = !string.IsNullOrWhiteSpace(agent);
                if (!areaChange && !agentChange)
                {
                    if (!string.IsNullOrEmpty(name) || cost != -1) throw new AnimUsageException("--name and --cost change the area at --index: pass --index 3-31.");
                    if (!string.IsNullOrWhiteSpace(set)) throw new AnimUsageException("--set changes the agent type named by --agent: pass --agent (cliext.navmesh settings lists them).");
                    throw new AnimUsageException("Nothing to change: pass --index with --name or --cost, or --agent with --set.");
                }
                if (!agentChange && !string.IsNullOrWhiteSpace(set)) throw new AnimUsageException("--set changes the agent type named by --agent: pass --agent (cliext.navmesh settings lists them).");
                var so = SerializedText.Settings(NavAsset);
                var changes = new List<string>();
                if (areaChange)
                {
                    var areas = so.FindProperty("areas");
                    if (index < 0 || index >= areas.arraySize) throw new AnimUsageException($"--index {index}: areas are 0-{areas.arraySize - 1} (3-{areas.arraySize - 1} for user areas).");
                    if (string.IsNullOrEmpty(name) && cost == -1) throw new AnimUsageException($"--index {index}: pass --name or --cost to change the area.");
                    var a = areas.GetArrayElementAtIndex(index);
                    var np = a.FindPropertyRelative("name");
                    var cp = a.FindPropertyRelative("cost");
                    string label = np.stringValue.Length > 0 ? $"Area {index} ({np.stringValue})" : $"Area {index}";
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (index < 3) throw new AnimUsageException($"Areas 0-2 are built in (Walkable, Not Walkable, Jump); their names can't change. Name a user area, 3-{areas.arraySize - 1}.");
                        bool clear = string.Equals(name.Trim(), "none", StringComparison.OrdinalIgnoreCase);
                        string newName = clear ? "" : name.Trim();
                        if (!clear)
                            for (int i = 0; i < areas.arraySize; i++)
                                if (i != index && string.Equals(areas.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue, newName, StringComparison.OrdinalIgnoreCase))
                                    throw new AnimUsageException($"Area {i} is already named '{newName}': pick another name.");
                        if (np.stringValue != newName) { changes.Add($"area {index} name: {(np.stringValue.Length > 0 ? np.stringValue : "(none)")} -> {(clear ? "(none)" : newName)}"); np.stringValue = newName; }
                    }
                    if (cost != -1)
                    {
                        if (float.IsNaN(cost) || cost < 1) throw new AnimUsageException($"--cost {ValueText.Format(cost)}: area costs are 1 or more.");
                        if (cp.floatValue != cost) { changes.Add($"area {index} cost: {ValueText.Format(cp.floatValue)} -> {ValueText.Format(cost)}"); cp.floatValue = cost; }
                    }
                    if (changes.Count == 0) throw new ArgumentException($"{label} already has {(string.IsNullOrEmpty(name) ? "" : $"name {name}")}{(!string.IsNullOrEmpty(name) && cost != -1 ? ", " : "")}{(cost != -1 ? $"cost {ValueText.Format(cost)}" : "")}: nothing would change.");
                }
                if (agentChange)
                {
                    int id = AgentId(agent);
                    string agentName = NavMesh.GetSettingsNameFromID(id);
                    if (string.IsNullOrWhiteSpace(set)) throw new AnimUsageException($"--agent {agentName}: pass --set \"agentRadius=0.5;agentHeight=2\" to change it.");
                    var sp = AgentSettings(so, id) ?? throw new AnimNotFoundException($"Agent type {agentName} has no settings in {NavAsset}.");
                    var agentChanges = SerializedText.SetFields(sp, set, AgentReadOnly);
                    void Range(string field, float min, float max, bool exclusiveMin, string want)
                    {
                        float v = sp.FindPropertyRelative(field).floatValue;
                        if (float.IsNaN(v) || (exclusiveMin ? v <= min : v < min) || v > max) throw new AnimUsageException($"{field} {ValueText.Format(v)} is out of range: give {want}.");
                    }
                    Range("agentRadius", 0, float.MaxValue, true, "more than 0");
                    Range("agentHeight", 0, float.MaxValue, true, "more than 0");
                    Range("agentSlope", 0, 60, false, "0 to 60");
                    Range("agentClimb", 0, float.MaxValue, false, "0 or more");
                    Range("ledgeDropHeight", 0, float.MaxValue, false, "0 or more");
                    Range("maxJumpAcrossDistance", 0, float.MaxValue, false, "0 or more");
                    if (agentChanges.Count == 0) throw new ArgumentException($"Agent type {agentName} already has {set.Trim().TrimEnd(';')}: nothing would change.");
                    changes.AddRange(agentChanges.Select(c => $"{agentName} {c}"));
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { changes });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        [CliGroupCommand("cliext.navmesh", "Query the baked NavMesh in the open scenes: a path from --from to --to (status, corners, length), or with --sample [x,y,z] the nearest NavMesh point within --distance. " +
                                           "--agent picks the agent type, --areas the walkable areas (names separated by ';'). No NavMesh, a point not within --distance of it, or no path exits 13 " +
                                           "(bake with cliext.navmesh bake); a partial path exits 6 with the path so far; an unknown agent or area, a bad point or both --sample and --from/--to exits 2.",
            Name = "query", Tags = new[] { "cliext", "navmesh" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode NavQuery(
            CommandContext cmd,
            [CliArg("from", "Path start [x,y,z]")] string from = "",
            [CliArg("to", "Path end [x,y,z]")] string to = "",
            [CliArg("sample", "A point to find the nearest NavMesh position for")] string sample = "",
            [CliArg("distance", "Search radius for --sample, and for snapping --from/--to")] float distance = 2,
            [CliArg("agent", "Agent type name or id")] string agent = "",
            [CliArg("areas", "Allowed areas (names; default all)")] string areas = "")
        {
            try
            {
                bool hasSample = !string.IsNullOrWhiteSpace(sample), hasFrom = !string.IsNullOrWhiteSpace(from), hasTo = !string.IsNullOrWhiteSpace(to);
                if (hasSample && (hasFrom || hasTo)) throw new AnimUsageException("Give --sample, or --from and --to, not both.");
                if (!hasSample && !(hasFrom && hasTo)) throw new AnimUsageException(hasFrom || hasTo ? $"A path needs both ends: pass {(hasFrom ? "--to" : "--from")} too." : "Pass --from and --to for a path, or --sample for the nearest NavMesh point.");
                if (!(distance > 0)) throw new AnimUsageException($"--distance {ValueText.Format(distance)}: give more than 0.");
                var areaNames = NavMesh.GetAreaNames();
                int areaMask = string.IsNullOrWhiteSpace(areas) ? NavMesh.AllAreas
                    : areas.Split(';').Where(a => a.Trim().Length > 0).Select(a =>
                    {
                        int i = NavMesh.GetAreaFromName(a.Trim());
                        if (i < 0) throw new AnimUsageException($"--areas: no area '{a.Trim()}'.{NameHint(a.Trim(), areaNames, "Areas", "cliext.navmesh settings")}");
                        return 1 << i;
                    }).Aggregate(0, (x, y) => x | y);
                int agentId = AgentId(agent);
                var filter = new NavMeshQueryFilter { areaMask = areaMask, agentTypeID = agentId };
                Vector3 p = hasSample ? Vec(sample, "--sample") : default, a0 = hasFrom ? Vec(from, "--from") : default, b0 = hasTo ? Vec(to, "--to") : default;
                if (NavMesh.CalculateTriangulation().vertices.Length == 0)
                {
                    cmd.Fail("The open scenes have no baked NavMesh: bake one with cliext.navmesh bake (cliext.navmesh surfaces lists the surfaces).");
                    return ExitCode.NotFound;
                }
                string agentLabel = string.IsNullOrWhiteSpace(agent) ? "" : $" for {NavMesh.GetSettingsNameFromID(agentId)}";
                if (hasSample)
                {
                    bool ok = NavMesh.SamplePosition(p, out NavMeshHit hit, distance, filter);
                    cmd.Result.Write(new { found = ok, position = ok ? ValueText.Format(hit.position) : null, distance = ok ? Math.Round(hit.distance, 4) : (double?)null, area = ok ? AreaName(hit.mask) : null });
                    if (!ok) { cmd.Fail($"No NavMesh{agentLabel} within {ValueText.Format(distance)} of {ValueText.Format(p)}: raise --distance or pick a point nearer the NavMesh{(string.IsNullOrWhiteSpace(areas) ? "" : ", or allow more --areas")}."); return ExitCode.NotFound; }
                    return ExitCode.Success;
                }
                // Snap both ends to the NavMesh first; CalculatePath fails on points off it.
                bool sa = NavMesh.SamplePosition(a0, out NavMeshHit ha, distance, filter), sb = NavMesh.SamplePosition(b0, out NavMeshHit hb, distance, filter);
                if (!sa || !sb)
                {
                    string which = !sa ? $"--from {ValueText.Format(a0)}" : $"--to {ValueText.Format(b0)}";
                    cmd.Result.Write(new { status = "PathInvalid", detail = $"{which} isn't within {ValueText.Format(distance)} of the NavMesh{agentLabel}." });
                    cmd.Fail($"{which} isn't within {ValueText.Format(distance)} of the NavMesh{agentLabel}: move it onto the NavMesh (cliext.navmesh query --sample finds the nearest point) or raise --distance.");
                    return ExitCode.NotFound;
                }
                var path = new NavMeshPath();
                NavMesh.CalculatePath(ha.position, hb.position, filter, path);
                float length = 0;
                for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                cmd.Result.Write(new { status = path.status.ToString(), length = Math.Round(length, 3), corners = path.corners.Select(c => ValueText.Format(c)).ToArray(), from = ValueText.Format(ha.position), to = ValueText.Format(hb.position) });
                if (path.status == NavMeshPathStatus.PathComplete) return ExitCode.Success;
                if (path.status == NavMeshPathStatus.PathPartial)
                {
                    cmd.Fail($"Only a partial path: the NavMesh{agentLabel} doesn't connect {ValueText.Format(ha.position)} to {ValueText.Format(hb.position)}{(string.IsNullOrWhiteSpace(areas) ? "" : " through the allowed --areas")}; the corners end where it stops.");
                    return ExitCode.Failed;
                }
                cmd.Fail($"No path from {ValueText.Format(ha.position)} to {ValueText.Format(hb.position)}{agentLabel}: check both points are on the NavMesh (--sample) and the --areas.");
                return ExitCode.NotFound;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        internal sealed class ObstaclePlan
        {
            public NavMeshObstacleShape Shape;
            public Vector3 Size, Center;
            public float Radius, Height;
            public bool Carve;
            public bool Add;
            public readonly List<string> Changes = new List<string>();
        }

        /// <summary>
        /// Checks and resolves an obstacle change against the object's current NavMeshObstacle (null: one is added with
        /// Unity's defaults). Bad shapes, sizes or vectors are invalid usage; values it already has are an ArgumentException.
        /// </summary>
        internal static ObstaclePlan PlanObstacle(NavMeshObstacle existing, string label, string shape, string size, string center, float radius, float height, bool? carve)
        {
            var p = new ObstaclePlan
            {
                Add = existing == null,
                Shape = existing != null ? existing.shape : NavMeshObstacleShape.Box,
                Size = existing != null ? existing.size : Vector3.one,
                Center = existing != null ? existing.center : Vector3.zero,
                Radius = existing != null ? existing.radius : 0.5f,
                // NavMeshObstacle.height reads back half what it was set to (the extent); size.y is the full height.
                Height = existing != null ? existing.size.y : 2f,
                Carve = existing != null && existing.carving
            };
            if (!string.IsNullOrWhiteSpace(shape))
            {
                var s = ParseCurrent<NavMeshObstacleShape>(shape, "--shape", "obstacle shape", "Shapes");
                if (s != p.Shape) { p.Changes.Add($"shape: {p.Shape} -> {s}"); p.Shape = s; }
            }
            if (!string.IsNullOrWhiteSpace(size))
            {
                var v = Vec(size, "--size");
                if (!(v.x > 0 && v.y > 0 && v.z > 0)) throw new AnimUsageException($"--size {ValueText.Format(v)}: every side must be more than 0.");
                if (p.Shape != NavMeshObstacleShape.Box) throw new AnimUsageException("--size is the box's size: a capsule takes --radius and --height (or pass --shape Box).");
                if (v != p.Size) { p.Changes.Add($"size: {ValueText.Format(p.Size)} -> {ValueText.Format(v)}"); p.Size = v; }
            }
            if (radius != -1 || height != -1)
            {
                if (p.Shape != NavMeshObstacleShape.Capsule) throw new AnimUsageException("--radius and --height are the capsule's: a box takes --size (or pass --shape Capsule).");
                if (radius != -1)
                {
                    if (!(radius > 0)) throw new AnimUsageException($"--radius {ValueText.Format(radius)}: give more than 0.");
                    if (radius != p.Radius) { p.Changes.Add($"radius: {ValueText.Format(p.Radius)} -> {ValueText.Format(radius)}"); p.Radius = radius; }
                }
                if (height != -1)
                {
                    if (!(height > 0)) throw new AnimUsageException($"--height {ValueText.Format(height)}: give more than 0.");
                    if (height != p.Height) { p.Changes.Add($"height: {ValueText.Format(p.Height)} -> {ValueText.Format(height)}"); p.Height = height; }
                }
            }
            if (!string.IsNullOrWhiteSpace(center))
            {
                var v = Vec(center, "--center");
                if (v != p.Center) { p.Changes.Add($"center: {ValueText.Format(p.Center)} -> {ValueText.Format(v)}"); p.Center = v; }
            }
            if (carve.HasValue && carve.Value != p.Carve) { p.Changes.Add($"carve: {p.Carve.ToString().ToLowerInvariant()} -> {carve.Value.ToString().ToLowerInvariant()}"); p.Carve = carve.Value; }
            if (p.Add) p.Changes.Insert(0, "added NavMeshObstacle");
            else if (p.Changes.Count == 0) throw new ArgumentException($"{label}'s NavMeshObstacle already has those values: nothing would change.");
            return p;
        }

        [CliGroupCommand("cliext.navmesh", "Add or change the NavMeshObstacle on --object (a name or hierarchy path in the open scenes): --shape Box or Capsule, --size [x,y,z] (box), " +
                                           "--radius and --height (capsule), --center [x,y,z] (local), --carve true to cut a hole in the NavMesh where it stands. Sizes are local, scaled by the object's transform. " +
                                           "Without one, it is added (Box, size [1,1,1], or what you give); --remove true takes it off. An unknown object exits 13; an unknown shape (with the shapes), " +
                                           "a size of 0 or less, a bad vector, or --size on a capsule exits 2; values it already has, or --remove with no obstacle, exit 6.",
            Name = "obstacle", Tags = new[] { "cliext", "navmesh" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Obstacle(
            CommandContext cmd,
            [CliArg("object", "The GameObject (name or hierarchy path)", Required = true)] string obj,
            [CliArg("shape", "Box or Capsule")] string shape = "",
            [CliArg("size", "Box size [x,y,z]")] string size = "",
            [CliArg("center", "Local center [x,y,z]")] string center = "",
            [CliArg("radius", "Capsule radius (-1: keep it)")] float radius = -1,
            [CliArg("height", "Capsule height (-1: keep it)")] float height = -1,
            [CliArg("carve", "true to carve the NavMesh, false not to (leave out to keep it)")] bool? carve = null,
            [CliArg("remove", "Remove the obstacle")] bool remove = false)
        {
            try
            {
                var go = FindSceneObject(obj, "--object");
                var existing = go.GetComponent<NavMeshObstacle>();
                string label = PathOf(go.transform);
                if (remove)
                {
                    if (!string.IsNullOrWhiteSpace(shape + size + center) || radius != -1 || height != -1 || carve.HasValue)
                        throw new AnimUsageException("--remove takes the obstacle off: leave out the other settings.");
                    if (existing == null) throw new ArgumentException($"'{label}' has no NavMeshObstacle: nothing would change.");
                    Undo.DestroyObjectImmediate(existing);
                    if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
                    cmd.Result.Write(new { @object = label, removed = true });
                    return ExitCode.Success;
                }
                var plan = PlanObstacle(existing, label, shape, size, center, radius, height, carve);
                var o = existing != null ? existing : Undo.AddComponent<NavMeshObstacle>(go);
                if (existing != null) Undo.RecordObject(o, "cliext.navmesh obstacle");
                o.shape = plan.Shape;
                if (plan.Shape == NavMeshObstacleShape.Box) o.size = plan.Size;
                else { o.radius = plan.Radius; o.height = plan.Height; }
                o.center = plan.Center;
                o.carving = plan.Carve;
                EditorUtility.SetDirty(o);
                if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
                cmd.Result.Write(new
                {
                    @object = label, changes = plan.Changes, shape = o.shape.ToString(),
                    size = o.shape == NavMeshObstacleShape.Box ? ValueText.Format(o.size) : null,
                    radius = o.shape == NavMeshObstacleShape.Capsule ? (float?)o.radius : null,
                    height = o.shape == NavMeshObstacleShape.Capsule ? (float?)o.size.y : null,
                    center = ValueText.Format(o.center), carve = o.carving
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        internal static int AgentId(string agent)
        {
            if (string.IsNullOrWhiteSpace(agent)) return NavMesh.GetSettingsByIndex(0).agentTypeID;
            string a = agent.Trim();
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                if (string.Equals(NavMesh.GetSettingsNameFromID(s.agentTypeID), a, StringComparison.OrdinalIgnoreCase) || s.agentTypeID.ToString(CultureInfo.InvariantCulture) == a) return s.agentTypeID;
            }
            throw new AnimUsageException($"No agent type '{a}'.{NameHint(a, AgentNames(), "Agent types", "cliext.navmesh settings")}");
        }

        private static string AreaName(int mask)
        {
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { var names = NavMesh.GetAreaNames(); return names.FirstOrDefault(n => NavMesh.GetAreaFromName(n) == i) ?? i.ToString(); }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Quality and graphics

        private const string QualityAsset = "ProjectSettings/QualitySettings.asset";

        /// <summary>A quality level by name or index; anything else is invalid usage with the level names.</summary>
        internal static int QualityIndex(string level, string arg = "--level")
        {
            var names = QualitySettings.names;
            string l = (level ?? "").Trim();
            int i = int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : Array.FindIndex(names, x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i >= names.Length)
                throw new AnimUsageException($"{arg}: no quality level '{l}'.{NameHint(l, names, "Levels", "cliext.quality levels")} Indexes are 0-{names.Length - 1}.");
            return i;
        }

        /// <summary>The platform names quality settings use (Standalone, Android, iPhone, ...), from the per-platform defaults.</summary>
        private static List<string> QualityPlatforms(SerializedObject so)
        {
            var defaults = so.FindProperty("m_PerPlatformDefaultQuality");
            var list = new List<string>();
            if (defaults != null && defaults.isArray)
                for (int i = 0; i < defaults.arraySize; i++) list.Add(defaults.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue);
            return list;
        }

        private static string QualityPlatform(string given, List<string> platforms, string arg)
        {
            string g = given.Trim();
            string match = platforms.FirstOrDefault(p => string.Equals(p, g, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new AnimUsageException($"{arg}: no platform '{g}'.{NameHint(g, platforms, "Platforms", null)}");
            return match;
        }

        [CliGroupCommand("cliext.quality", "Every quality level with all its settings (shadows, distances, anti-aliasing, vSync, LOD, textures, render pipeline asset, the platforms it is excluded from), " +
                                           "the current level, and each platform's default level. --level narrows to one; an unknown level exits 2 with the level names.",
            Name = "levels", Tags = new[] { "cliext", "quality" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Levels(CommandContext cmd, [CliArg("level", "One level (name or index)")] string level = "")
        {
            try
            {
                int only = string.IsNullOrWhiteSpace(level) ? -1 : QualityIndex(level);
                var so = SerializedText.Settings(QualityAsset);
                var levels = so.FindProperty("m_QualitySettings");
                var defaults = so.FindProperty("m_PerPlatformDefaultQuality");
                var platformDefaults = new Dictionary<string, string>();
                if (defaults != null && defaults.isArray)
                    for (int i = 0; i < defaults.arraySize; i++)
                    {
                        var e = defaults.GetArrayElementAtIndex(i);
                        int lv = e.FindPropertyRelative("second").intValue;
                        platformDefaults[e.FindPropertyRelative("first").stringValue] = lv < levels.arraySize ? levels.GetArrayElementAtIndex(lv).FindPropertyRelative("name").stringValue : lv.ToString();
                    }
                cmd.Result.Write(new
                {
                    current = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    platformDefaults,
                    levels = Enumerable.Range(0, levels.arraySize).Where(i => only < 0 || i == only)
                        .Select(i =>
                        {
                            var l = levels.GetArrayElementAtIndex(i);
                            var ex = l.FindPropertyRelative("excludedTargetPlatforms");
                            return new
                            {
                                index = i,
                                settings = SerializedText.Fields(l),
                                excludedPlatforms = ex == null ? null : Enumerable.Range(0, ex.arraySize).Select(k => ex.GetArrayElementAtIndex(k).stringValue).ToArray()
                            };
                        }).ToArray()
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        [CliGroupCommand("cliext.quality", "Change a quality level (--level name or index): --set \"shadowDistance=40;vSyncCount=0;antiAliasing=2;lodBias=1;customRenderPipeline=Assets/Settings/Low.asset\" (names as quality levels lists them; " +
                                           "an enum takes its name or its stored number, so antiAliasing=2 is 2x), --exclude \"Android;iPhone\" to set the platforms it is left out on (none to clear). " +
                                           "Or --platform Android --default Low for a platform's default level. Everything is checked before anything is saved: an unknown level, setting or platform, " +
                                           "a bad value or nothing to change exits 2; a missing asset exits 13; values the level already has exit 6.",
            Name = "set", Tags = new[] { "cliext", "quality" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetQuality(
            CommandContext cmd,
            [CliArg("level", "The level (name or index)")] string level = "",
            [CliArg("set", "Settings: name=value;...")] string set = "",
            [CliArg("exclude", "Platforms to exclude it from, separated by ';' (none to clear)")] string exclude = "",
            [CliArg("platform", "A platform (Standalone, Android, iPhone, WebGL, ...) for --default")] string platform = "",
            [CliArg("default", "The platform's default level")] string defaultLevel = "")
        {
            try
            {
                bool hasLevel = !string.IsNullOrWhiteSpace(level), hasSet = !string.IsNullOrWhiteSpace(set), hasExclude = !string.IsNullOrWhiteSpace(exclude);
                bool hasPlatform = !string.IsNullOrWhiteSpace(platform), hasDefault = !string.IsNullOrWhiteSpace(defaultLevel);
                if (!hasLevel && !hasPlatform && !hasDefault)
                    throw new AnimUsageException((hasSet || hasExclude) ? "--set and --exclude change the level named by --level: pass --level (cliext.quality levels lists them)." : "Nothing to change: pass --level with --set or --exclude, or --platform with --default.");
                if (!hasLevel && (hasSet || hasExclude)) throw new AnimUsageException("--set and --exclude change the level named by --level: pass --level (cliext.quality levels lists them).");
                if (hasLevel && !hasSet && !hasExclude) throw new AnimUsageException($"--level {level.Trim()}: pass --set or --exclude to change it.");
                if (hasPlatform != hasDefault) throw new AnimUsageException(hasPlatform ? "--platform needs --default: the level to use on it." : "--default needs --platform: the platform it is the default on.");

                var so = SerializedText.Settings(QualityAsset);
                var levels = so.FindProperty("m_QualitySettings");
                var platforms = QualityPlatforms(so);
                var changes = new List<string>();
                if (hasLevel)
                {
                    int li = QualityIndex(level);
                    var l = levels.GetArrayElementAtIndex(li);
                    changes.AddRange(SerializedText.SetFields(l, set, "excludedTargetPlatforms").Select(c => $"{QualitySettings.names[li]} {c}"));
                    if (hasExclude)
                    {
                        var ex = l.FindPropertyRelative("excludedTargetPlatforms");
                        var list = string.Equals(exclude.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? new string[0]
                            : exclude.Split(';').Where(x => x.Trim().Length > 0).Select(x => QualityPlatform(x, platforms, "--exclude")).Distinct().ToArray();
                        var old = Enumerable.Range(0, ex.arraySize).Select(k => ex.GetArrayElementAtIndex(k).stringValue).ToArray();
                        if (!old.OrderBy(x => x).SequenceEqual(list.OrderBy(x => x)))
                        {
                            ex.arraySize = list.Length;
                            for (int k = 0; k < list.Length; k++) ex.GetArrayElementAtIndex(k).stringValue = list[k];
                            changes.Add($"{QualitySettings.names[li]} excluded: {(old.Length > 0 ? string.Join(", ", old) : "(none)")} -> {(list.Length > 0 ? string.Join(", ", list) : "(none)")}");
                        }
                    }
                }
                if (hasPlatform)
                {
                    string plat = QualityPlatform(platform, platforms, "--platform");
                    int target = QualityIndex(defaultLevel, "--default");
                    var defaults = so.FindProperty("m_PerPlatformDefaultQuality");
                    for (int i = 0; i < defaults.arraySize; i++)
                    {
                        var e = defaults.GetArrayElementAtIndex(i);
                        if (e.FindPropertyRelative("first").stringValue != plat) continue;
                        var second = e.FindPropertyRelative("second");
                        if (second.intValue != target)
                        {
                            changes.Add($"{plat} default: {(second.intValue < QualitySettings.names.Length ? QualitySettings.names[second.intValue] : second.intValue.ToString())} -> {QualitySettings.names[target]}");
                            second.intValue = target;
                        }
                    }
                }
                if (changes.Count == 0) throw new ArgumentException("The quality settings already have those values: nothing would change.");
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { changes });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        private static IEnumerable<string> CurrentNames<T>() where T : struct, Enum =>
            Enum.GetNames(typeof(T)).Where(n => !typeof(T).GetField(n).IsDefined(typeof(ObsoleteAttribute), false));

        /// <summary>An enum value by name (not obsolete, not a number); invalid usage with "Did you mean" and the names.</summary>
        internal static T ParseCurrent<T>(string text, string arg, string kind, string kinds, params string[] leaveOut) where T : struct, Enum
        {
            string t = (text ?? "").Trim();
            var names = CurrentNames<T>().Except(leaveOut).ToList();
            string match = names.FirstOrDefault(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new AnimUsageException($"{arg}: '{t}' isn't {AnimationErrors.A(kind)}.{NameHint(t, names, kinds, null)}");
            return (T)Enum.Parse(typeof(T), match);
        }

        [CliGroupCommand("cliext.quality", "The graphics APIs a platform builds with (--platform StandaloneWindows64, Android, iOS, WebGL, ...; default the active one), and with --set \"Vulkan;OpenGLES3\" the list to use (--auto true goes back to Unity's choice). " +
                                           "Also lists the render pipeline asset in Graphics settings and each quality level's override. " +
                                           "An unknown platform or API, a repeated API, or both --set and --auto exits 2; a list it already uses (or --auto when it is automatic) exits 6.",
            Name = "graphics", Tags = new[] { "cliext", "quality" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Graphics(
            CommandContext cmd,
            [CliArg("platform", "The build target")] string platform = "",
            [CliArg("set", "Graphics APIs in order, separated by ';'")] string set = "",
            [CliArg("auto", "Use Unity's default APIs for the platform")] bool auto = false)
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(platform) ? EditorUserBuildSettings.activeBuildTarget : ParseCurrent<BuildTarget>(platform, "--platform", "build target", "Build targets", nameof(BuildTarget.NoTarget));
                bool hasSet = !string.IsNullOrWhiteSpace(set);
                if (auto && hasSet) throw new AnimUsageException("Give --set or --auto true, not both.");
                if (auto)
                {
                    if (PlayerSettings.GetUseDefaultGraphicsAPIs(target)) throw new ArgumentException($"{target} already uses Unity's default graphics APIs: nothing would change.");
                    PlayerSettings.SetUseDefaultGraphicsAPIs(target, true);
                }
                else if (hasSet)
                {
                    var apis = set.Split(';').Where(a => a.Trim().Length > 0).Select(a => ParseCurrent<GraphicsDeviceType>(a, "--set", "graphics API", "Graphics APIs", nameof(GraphicsDeviceType.Null))).ToArray();
                    if (apis.Length == 0) throw new AnimUsageException("--set: give the graphics APIs in order, such as \"Direct3D12;Vulkan\".");
                    var twice = apis.GroupBy(a => a).FirstOrDefault(g => g.Count() > 1);
                    if (twice != null) throw new AnimUsageException($"--set lists {twice.Key} twice.");
                    if (!PlayerSettings.GetUseDefaultGraphicsAPIs(target) && PlayerSettings.GetGraphicsAPIs(target).SequenceEqual(apis))
                        throw new ArgumentException($"{target} already uses {string.Join(", ", apis)}: nothing would change.");
                    PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                    PlayerSettings.SetGraphicsAPIs(target, apis);
                }
                if (auto || hasSet) AssetDatabase.SaveAssets();
                cmd.Result.Write(new
                {
                    platform = target.ToString(),
                    automatic = PlayerSettings.GetUseDefaultGraphicsAPIs(target),
                    apis = PlayerSettings.GetGraphicsAPIs(target).Select(a => a.ToString()).ToArray(),
                    defaultRenderPipeline = GraphicsSettings.defaultRenderPipeline != null ? AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline) : null,
                    qualityOverrides = Enumerable.Range(0, QualitySettings.names.Length).Select(i => new { level = QualitySettings.names[i], pipeline = QualitySettings.GetRenderPipelineAssetAt(i) != null ? AssetDatabase.GetAssetPath(QualitySettings.GetRenderPipelineAssetAt(i)) : null }).ToArray()
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }
    }
}

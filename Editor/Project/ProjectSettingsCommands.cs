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

        [CliGroupCommand("cliext.physics", "The layer collision matrix (3D, or --2d true): for each named layer, the layers it does not collide with.",
            Name = "matrix", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Matrix(CommandContext cmd, [CliArg("2d", "The 2D matrix")] bool twoD = false)
        {
            var so = SerializedText.Settings(MatrixAsset(twoD));
            var m = so.FindProperty("m_LayerCollisionMatrix");
            var layers = Enumerable.Range(0, 32).Where(i => LayerMask.LayerToName(i).Length > 0).ToList();
            foreach (int a in layers)
            {
                uint bits = (uint)m.GetArrayElementAtIndex(a).longValue;
                var ignores = layers.Where(b => (bits & (1u << b)) == 0).Select(LayerMask.LayerToName).ToArray();
                cmd.Result.WriteRecord(new { layer = LayerMask.LayerToName(a), index = a, ignores });
            }
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.physics", "Make two layers collide or not (both directions), in the 3D matrix or with --2d true the 2D one. Saved to the project settings. " +
                                           "An unknown layer exits 2 with the layer names (cliext.physics matrix lists them and what each ignores); layers that already collide (or ignore each other) exit 6.",
            Name = "set_matrix", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetMatrix(
            CommandContext cmd,
            [CliArg("a", "A layer (name or index)", Required = true)] string a,
            [CliArg("b", "The other layer", Required = true)] string b,
            [CliArg("collide", "true to collide, false to ignore", Required = true)] bool collide,
            [CliArg("2d", "The 2D matrix")] bool twoD = false)
        {
            try
            {
                int la = Layer(a, "--a"), lb = Layer(b, "--b");
                var so = SerializedText.Settings(MatrixAsset(twoD));
                var m = so.FindProperty("m_LayerCollisionMatrix");
                bool collidesNow = ((uint)m.GetArrayElementAtIndex(la).longValue & (1u << lb)) != 0;
                if (collidesNow == collide)
                    throw new ArgumentException($"{LayerLabel(la)} and {LayerLabel(lb)} already {(collide ? "collide" : "ignore each other")} in the {(twoD ? "2D" : "3D")} matrix: nothing would change.");
                void Set(int row, int col)
                {
                    var e = m.GetArrayElementAtIndex(row);
                    uint bits = (uint)e.longValue;
                    bits = collide ? bits | (1u << col) : bits & ~(1u << col);
                    e.longValue = bits;
                }
                Set(la, lb); Set(lb, la);
                so.ApplyModifiedPropertiesWithoutUndo();
                if (twoD) Physics2D.IgnoreLayerCollision(la, lb, !collide); else Physics.IgnoreLayerCollision(la, lb, !collide);
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { a = LayerLabel(la), b = LayerLabel(lb), collide, matrix = twoD ? "2D" : "3D" });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        private static readonly string[] Material3DFields = { "dynamicFriction", "staticFriction", "bounciness", "frictionCombine", "bounceCombine" };
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
                                           "an existing material with no --set exits 2, and values it already has exit 6.",
            Name = "material", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Material(
            CommandContext cmd,
            [CliArg("path", "The material asset", Required = true)] string path,
            [CliArg("set", "Settings: name=value;...")] string set = "",
            [CliArg("2d", "A 2D physics material")] bool twoD = false)
        {
            try
            {
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
                if (main != null && values.Count == 0)
                    throw new AnimUsageException($"Nothing to change on '{path}': pass --set \"{(twoD ? "friction=0.4;bounciness=0" : "dynamicFriction=0.6;bounciness=0")}\".");
                if (main != null && values.All(kv => Equals(MaterialValue(main, kv.Key), kv.Value)))
                    throw new ArgumentException($"'{path}' already has {string.Join(", ", values.Select(kv => $"{kv.Key} {ValueText.Format(kv.Value)}"))}: nothing would change.");

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
                object describe = mat is PhysicsMaterial m3
                    ? (object)new { path, created, m3.dynamicFriction, m3.staticFriction, m3.bounciness, frictionCombine = m3.frictionCombine.ToString(), bounceCombine = m3.bounceCombine.ToString(), set = done }
                    : new { path, created, ((PhysicsMaterial2D)mat).friction, ((PhysicsMaterial2D)mat).bounciness, set = done };
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

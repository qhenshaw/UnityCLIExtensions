using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Pipeline.Commands;
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
    internal static class ProjectSettingsCommands
    {
        // ---------------------------------------------------------------------------------------------------------
        // Physics

        private static string MatrixAsset(bool twoD) => twoD ? "ProjectSettings/Physics2DSettings.asset" : "ProjectSettings/DynamicsManager.asset";

        private static int Layer(string name)
        {
            int i = int.TryParse(name, out int n) ? n : LayerMask.NameToLayer(name);
            if (i < 0 || i > 31) throw new ArgumentException($"No layer '{name}'. Layers: {string.Join(", ", Enumerable.Range(0, 32).Select(LayerMask.LayerToName).Where(l => l.Length > 0))}");
            return i;
        }

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

        [CliGroupCommand("cliext.physics", "Make two layers collide or not (both directions), in the 3D matrix or with --2d true the 2D one. Saved to the project settings.",
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
                int la = Layer(a), lb = Layer(b);
                var so = SerializedText.Settings(MatrixAsset(twoD));
                var m = so.FindProperty("m_LayerCollisionMatrix");
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
                cmd.Result.Write(new { a = LayerMask.LayerToName(la), b = LayerMask.LayerToName(lb), collide, matrix = twoD ? "2D" : "3D" });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand("cliext.physics", "Create or change a physics material (--path .physicMaterial, or .physicsMaterial2D with --2d true): --set \"dynamicFriction=0.2;staticFriction=0.3;bounciness=0.5;frictionCombine=Minimum;bounceCombine=Maximum\" (2D: friction, bounciness).",
            Name = "material", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Material(
            CommandContext cmd,
            [CliArg("path", "The material asset", Required = true)] string path,
            [CliArg("set", "Settings: name=value;...")] string set = "",
            [CliArg("2d", "A 2D physics material")] bool twoD = false)
        {
            try
            {
                UnityEngine.Object mat = twoD ? (UnityEngine.Object)AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(path) : AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
                bool created = false;
                if (mat == null)
                {
                    mat = twoD ? (UnityEngine.Object)new PhysicsMaterial2D() : new PhysicsMaterial();
                    AssetDatabase.CreateAsset(mat, path);
                    created = true;
                }
                Undo.RecordObject(mat, "cliext.physics material");
                var done = Animation.AnimatorEditing.SetMembers(mat, set);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssetIfDirty(mat);
                object describe = mat is PhysicsMaterial m3
                    ? (object)new { path, created, m3.dynamicFriction, m3.staticFriction, m3.bounciness, frictionCombine = m3.frictionCombine.ToString(), bounceCombine = m3.bounceCombine.ToString(), set = done }
                    : new { path, created, ((PhysicsMaterial2D)mat).friction, ((PhysicsMaterial2D)mat).bounciness, set = done };
                cmd.Result.Write(describe);
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand("cliext.physics", "Cast a ray in the open scenes (Edit or Play mode): --from [x,y,z] --direction [x,y,z] (or --to [x,y,z]), --distance, --layers (names separated by ';'), --all true for every hit. " +
                                           "Returns each hit's object, collider, point, normal, distance and layer.",
            Name = "raycast", Tags = new[] { "cliext", "physics" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Raycast(
            CommandContext cmd,
            [CliArg("from", "Origin [x,y,z]", Required = true)] string from,
            [CliArg("direction", "Direction [x,y,z]")] string direction = "",
            [CliArg("to", "A target point instead of a direction")] string to = "",
            [CliArg("distance", "Max distance")] float distance = 1000,
            [CliArg("layers", "Layer names separated by ';' (default: all)")] string layers = "",
            [CliArg("all", "Every hit, not just the first")] bool all = false)
        {
            try
            {
                var origin = (Vector3)ValueText.Parse(from, typeof(Vector3));
                Vector3 dir;
                if (!string.IsNullOrWhiteSpace(to)) { var target = (Vector3)ValueText.Parse(to, typeof(Vector3)); dir = target - origin; if (distance == 1000) distance = dir.magnitude; }
                else dir = (Vector3)ValueText.Parse(string.IsNullOrWhiteSpace(direction) ? "[0,-1,0]" : direction, typeof(Vector3));
                int mask = string.IsNullOrWhiteSpace(layers) ? Physics.DefaultRaycastLayers : layers.Split(';').Select(l => 1 << Layer(l.Trim())).Aggregate(0, (x, y) => x | y);
                Physics.SyncTransforms();
                var hits = all ? Physics.RaycastAll(origin, dir.normalized, distance, mask, QueryTriggerInteraction.Collide).OrderBy(h => h.distance).ToArray()
                               : (Physics.Raycast(origin, dir.normalized, out RaycastHit h1, distance, mask, QueryTriggerInteraction.Collide) ? new[] { h1 } : Array.Empty<RaycastHit>());
                foreach (var h in hits)
                    cmd.Result.WriteRecord(new { gameObject = h.collider.gameObject.name, collider = h.collider.GetType().Name, trigger = h.collider.isTrigger ? true : (bool?)null, point = ValueText.Format(h.point), normal = ValueText.Format(h.normal), distance = Math.Round(h.distance, 4), layer = LayerMask.LayerToName(h.collider.gameObject.layer) });
                if (hits.Length == 0) cmd.Log.Info("No hit.");
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // NavMesh

        private const string NavAsset = "ProjectSettings/NavMeshAreas.asset";

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
                agents = Enumerable.Range(0, NavMesh.GetSettingsCount()).Select(i => NavMesh.GetSettingsByIndex(i)).Select(s => new
                {
                    id = s.agentTypeID, name = NavMesh.GetSettingsNameFromID(s.agentTypeID),
                    radius = s.agentRadius, height = s.agentHeight, maxSlope = s.agentSlope, stepHeight = s.agentClimb, voxelSize = s.overrideVoxelSize ? (float?)s.voxelSize : null
                }).ToArray()
            });
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.navmesh", "Name an area and set its cost (--index 3-31 for user areas; 0-2 are Walkable, Not Walkable, Jump), or change an agent type (--agent name or id, --set \"agentRadius=0.4;agentHeight=1.8;agentSlope=45;agentClimb=0.4\").",
            Name = "set", Tags = new[] { "cliext", "navmesh" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode NavSet(
            CommandContext cmd,
            [CliArg("index", "Area index")] int index = -1,
            [CliArg("name", "Area name")] string name = "",
            [CliArg("cost", "Area cost")] float cost = -1,
            [CliArg("agent", "Agent type name or id")] string agent = "",
            [CliArg("set", "Agent settings: name=value;...")] string set = "")
        {
            try
            {
                var so = SerializedText.Settings(NavAsset);
                var changes = new List<string>();
                if (index >= 0)
                {
                    var areas = so.FindProperty("areas");
                    if (index >= areas.arraySize) throw new ArgumentException($"Areas are 0-{areas.arraySize - 1}.");
                    var a = areas.GetArrayElementAtIndex(index);
                    if (!string.IsNullOrEmpty(name)) { if (index < 3) throw new ArgumentException("Areas 0-2 are built in; their names can't change."); a.FindPropertyRelative("name").stringValue = name; changes.Add($"area {index} name: {name}"); }
                    if (cost >= 0) { a.FindPropertyRelative("cost").floatValue = cost; changes.Add($"area {index} cost: {cost}"); }
                }
                if (!string.IsNullOrWhiteSpace(agent))
                {
                    var settings = so.FindProperty("m_Settings");
                    var names = so.FindProperty("m_SettingNames");
                    int found = -1;
                    for (int i = 0; i < settings.arraySize; i++)
                    {
                        var s = settings.GetArrayElementAtIndex(i);
                        string n = i < names.arraySize ? names.GetArrayElementAtIndex(i).stringValue : "";
                        if (string.Equals(n, agent, StringComparison.OrdinalIgnoreCase) || s.FindPropertyRelative("agentTypeID").intValue.ToString() == agent) found = i;
                    }
                    if (found < 0) throw new ArgumentException($"No agent type '{agent}'.");
                    changes.AddRange(SerializedText.SetFields(settings.GetArrayElementAtIndex(found), set));
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { changes });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand("cliext.navmesh", "Query the baked NavMesh in the open scenes: a path from --from to --to (status, corners, length), or with --sample [x,y,z] the nearest NavMesh point within --distance. " +
                                           "--agent picks the agent type, --areas the walkable areas (names separated by ';').",
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
                int areaMask = string.IsNullOrWhiteSpace(areas) ? NavMesh.AllAreas
                    : areas.Split(';').Select(a => NavMesh.GetAreaFromName(a.Trim())).Select(i => i < 0 ? throw new ArgumentException("Unknown area in --areas.") : 1 << i).Aggregate(0, (x, y) => x | y);
                var filter = new NavMeshQueryFilter { areaMask = areaMask, agentTypeID = AgentId(agent) };
                if (!string.IsNullOrWhiteSpace(sample))
                {
                    var p = (Vector3)ValueText.Parse(sample, typeof(Vector3));
                    bool ok = NavMesh.SamplePosition(p, out NavMeshHit hit, distance, filter);
                    cmd.Result.Write(new { found = ok, position = ok ? ValueText.Format(hit.position) : null, distance = ok ? Math.Round(hit.distance, 4) : (double?)null, area = ok ? AreaName(hit.mask) : null });
                    return ok ? ExitCode.Success : ExitCode.NotFound;
                }
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) throw new ArgumentException("Pass --from and --to, or --sample.");
                var a = (Vector3)ValueText.Parse(from, typeof(Vector3));
                var b = (Vector3)ValueText.Parse(to, typeof(Vector3));
                // Snap both ends to the NavMesh first; CalculatePath fails on points off it.
                bool sa = NavMesh.SamplePosition(a, out NavMeshHit ha, distance, filter), sb = NavMesh.SamplePosition(b, out NavMeshHit hb, distance, filter);
                if (!sa || !sb) { cmd.Result.Write(new { status = "PathInvalid", detail = $"{(!sa ? "from" : "to")} isn't within {distance} of the NavMesh." }); return ExitCode.NotFound; }
                var path = new NavMeshPath();
                NavMesh.CalculatePath(ha.position, hb.position, filter, path);
                float length = 0;
                for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                cmd.Result.Write(new { status = path.status.ToString(), length = Math.Round(length, 3), corners = path.corners.Select(c => ValueText.Format(c)).ToArray(), from = ValueText.Format(ha.position), to = ValueText.Format(hb.position) });
                return path.status == NavMeshPathStatus.PathComplete ? ExitCode.Success : ExitCode.Failed;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        private static int AgentId(string agent)
        {
            if (string.IsNullOrWhiteSpace(agent)) return NavMesh.GetSettingsByIndex(0).agentTypeID;
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                if (string.Equals(NavMesh.GetSettingsNameFromID(s.agentTypeID), agent, StringComparison.OrdinalIgnoreCase) || s.agentTypeID.ToString() == agent) return s.agentTypeID;
            }
            throw new ArgumentException($"No agent type '{agent}'.");
        }

        private static string AreaName(int mask)
        {
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { var names = NavMesh.GetAreaNames(); return names.FirstOrDefault(n => NavMesh.GetAreaFromName(n) == i) ?? i.ToString(); }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Quality and graphics

        private const string QualityAsset = "ProjectSettings/QualitySettings.asset";

        [CliGroupCommand("cliext.quality", "Every quality level with all its settings (shadows, distances, anti-aliasing, vSync, LOD, textures, render pipeline asset, the platforms it is excluded from), " +
                                           "the current level, and each platform's default level. --level narrows to one.",
            Name = "levels", Tags = new[] { "cliext", "quality" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Levels(CommandContext cmd, [CliArg("level", "One level (name or index)")] string level = "")
        {
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
                levels = Enumerable.Range(0, levels.arraySize).Where(i => level == "" || level == i.ToString() || string.Equals(QualitySettings.names[i], level, StringComparison.OrdinalIgnoreCase))
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

        [CliGroupCommand("cliext.quality", "Change a quality level (--level name or index): --set \"shadowDistance=40;vSyncCount=0;antiAliasing=2;lodBias=1;customRenderPipeline=Assets/Settings/Low.asset\" (names as quality levels lists them), " +
                                           "--exclude \"Android;iPhone\" to set the platforms it is left out on. Or --platform Android --default Low for a platform's default level.",
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
                var so = SerializedText.Settings(QualityAsset);
                var levels = so.FindProperty("m_QualitySettings");
                int Index(string l)
                {
                    int i = int.TryParse(l, out int n) ? n : Array.FindIndex(QualitySettings.names, x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase));
                    if (i < 0 || i >= levels.arraySize) throw new ArgumentException($"No quality level '{l}'. Levels: {string.Join(", ", QualitySettings.names)}");
                    return i;
                }
                var changes = new List<string>();
                if (!string.IsNullOrWhiteSpace(level))
                {
                    var l = levels.GetArrayElementAtIndex(Index(level));
                    changes.AddRange(SerializedText.SetFields(l, set));
                    if (!string.IsNullOrWhiteSpace(exclude))
                    {
                        var ex = l.FindPropertyRelative("excludedTargetPlatforms");
                        var list = exclude == "none" ? new string[0] : exclude.Split(';').Select(x => x.Trim()).ToArray();
                        ex.arraySize = list.Length;
                        for (int k = 0; k < list.Length; k++) ex.GetArrayElementAtIndex(k).stringValue = list[k];
                        changes.Add($"excluded: {string.Join(", ", list)}");
                    }
                }
                if (!string.IsNullOrWhiteSpace(platform))
                {
                    var defaults = so.FindProperty("m_PerPlatformDefaultQuality");
                    int target = Index(defaultLevel);
                    int at = -1;
                    for (int i = 0; i < defaults.arraySize; i++) if (defaults.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue == platform) at = i;
                    if (at < 0) { at = defaults.arraySize; defaults.arraySize++; defaults.GetArrayElementAtIndex(at).FindPropertyRelative("first").stringValue = platform; }
                    defaults.GetArrayElementAtIndex(at).FindPropertyRelative("second").intValue = target;
                    changes.Add($"{platform} default: {QualitySettings.names[target]}");
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                cmd.Result.Write(new { changes });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand("cliext.quality", "The graphics APIs a platform builds with (--platform StandaloneWindows64, Android, iOS, WebGL, ...), and with --set \"Vulkan;OpenGLES3\" the list to use (--auto true goes back to Unity's choice). " +
                                           "Also lists the render pipeline asset in Graphics settings and each quality level's override.",
            Name = "graphics", Tags = new[] { "cliext", "quality" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Graphics(
            CommandContext cmd,
            [CliArg("platform", "The build target")] string platform = "",
            [CliArg("set", "Graphics APIs in order, separated by ';'")] string set = "",
            [CliArg("auto", "Use Unity's default APIs for the platform")] bool auto = false)
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(platform) ? EditorUserBuildSettings.activeBuildTarget : (BuildTarget)Enum.Parse(typeof(BuildTarget), platform, true);
                if (auto) PlayerSettings.SetUseDefaultGraphicsAPIs(target, true);
                else if (!string.IsNullOrWhiteSpace(set))
                {
                    var apis = set.Split(';').Select(a => (GraphicsDeviceType)Enum.Parse(typeof(GraphicsDeviceType), a.Trim(), true)).ToArray();
                    PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                    PlayerSettings.SetGraphicsAPIs(target, apis);
                }
                if (auto || !string.IsNullOrWhiteSpace(set)) AssetDatabase.SaveAssets();
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
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }
    }
}

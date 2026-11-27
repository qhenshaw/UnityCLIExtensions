using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Mathematics;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

namespace UnityCLIExtensions.Splines
{
    [Serializable]
    internal class SplineOp
    {
        /// <summary>add_knot, set_knot, remove_knot, set_closed, set_tangent_mode, add_spline, remove_spline.</summary>
        public string op;
        /// <summary>Which spline in the container (default 0).</summary>
        public int spline;
        /// <summary>The knot (for add_knot, where to insert; -1 appends).</summary>
        public int index = -1;
        /// <summary>[x,y,z] in the container's local space (or world space with world: true).</summary>
        public string position;
        public string tangentIn;
        public string tangentOut;
        /// <summary>Euler angles [x,y,z].</summary>
        public string rotation;
        /// <summary>Tangent mode: AutoSmooth, Linear, Mirrored, Continuous, Broken.</summary>
        public string mode;
        public string value;
        public bool world;
        /// <summary>For add_spline: knot positions "[x,y,z];[x,y,z];...".</summary>
        public string knots;
    }

    [Serializable]
    internal class SplineOpList
    {
        public SplineOp[] ops;
    }

    /// <summary><c>cliext.splines</c>: list, read, create and edit SplineContainers and their knots, and sample splines.</summary>
    internal static class SplineCommands
    {
        private const string Group = "cliext.splines";

        private static SplineContainer Find(string obj)
        {
            var all = UnityEngine.Object.FindObjectsByType<SplineContainer>(FindObjectsInactive.Include);
            return all.FirstOrDefault(c => string.Equals(c.name, obj, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No SplineContainer on '{obj}'. Containers: {string.Join(", ", all.Select(c => c.name))}");
        }

        private static string V(float3 v) => ValueText.Format((Vector3)v);
        private static float3 P(string text) => (Vector3)ValueText.Parse(text, typeof(Vector3));

        private static object DescribeSpline(SplineContainer c, int i, bool knots)
        {
            var s = c.Splines[i];
            var bounds = s.GetBounds(c.transform.localToWorldMatrix);
            return new
            {
                index = i,
                knots = s.Count,
                closed = s.Closed,
                length = Math.Round(s.CalculateLength(c.transform.localToWorldMatrix), 3),
                boundsCenter = ValueText.Format(bounds.center),
                boundsSize = ValueText.Format(bounds.size),
                points = knots ? s.Knots.Select((k, n) => new
                {
                    index = n,
                    position = V(k.Position),
                    world = ValueText.Format(c.transform.TransformPoint(k.Position)),
                    tangentIn = V(k.TangentIn),
                    tangentOut = V(k.TangentOut),
                    rotation = ValueText.Format(((Quaternion)k.Rotation).eulerAngles),
                    mode = s.GetTangentMode(n).ToString()
                }).ToArray() : null
            };
        }

        [CliGroupCommand(Group, "The SplineContainers in the open scenes, each spline's knot count, closed flag, world length and bounds.",
            Name = "list", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            foreach (var c in UnityEngine.Object.FindObjectsByType<SplineContainer>(FindObjectsInactive.Include))
                cmd.Result.WriteRecord(new { name = c.name, scene = c.gameObject.scene.name, splines = Enumerable.Range(0, c.Splines.Count).Select(i => DescribeSpline(c, i, false)).ToArray() });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "A SplineContainer's splines with every knot: local and world position, tangents, rotation and tangent mode.",
            Name = "inspect", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("object", "The GameObject with the SplineContainer", Required = true)] string obj)
        {
            try
            {
                var c = Find(obj);
                cmd.Result.Write(new { name = c.name, splines = Enumerable.Range(0, c.Splines.Count).Select(i => DescribeSpline(c, i, true)).ToArray() });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.NotFound; }
        }

        [CliGroupCommand(Group, "Create a GameObject with a SplineContainer and one spline through --knots \"[x,y,z];[x,y,z];...\" (local to --position), with --mode for the tangents and --closed.",
            Name = "create", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("name", "The GameObject's name", Required = true)] string name,
            [CliArg("knots", "Knot positions \"[x,y,z];[x,y,z]\"", Required = true)] string knots,
            [CliArg("mode", "Tangent mode: AutoSmooth, Linear, Mirrored, Continuous, Broken")] string mode = "AutoSmooth",
            [CliArg("closed", "Close the loop")] bool closed = false,
            [CliArg("position", "The GameObject's world position [x,y,z]")] string position = "")
        {
            try
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "cliext.splines create");
                if (!string.IsNullOrWhiteSpace(position)) go.transform.position = (Vector3)ValueText.Parse(position, typeof(Vector3));
                var c = go.AddComponent<SplineContainer>();
                c.Spline = BuildSpline(knots, mode, closed);
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(go.scene);
                cmd.Result.Write(DescribeSpline(c, 0, true));
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Fail(e.Message); return ExitCode.Failed; }
        }

        private static Spline BuildSpline(string knots, string mode, bool closed)
        {
            var tangentMode = (TangentMode)Enum.Parse(typeof(TangentMode), string.IsNullOrEmpty(mode) ? "AutoSmooth" : mode, true);
            var spline = new Spline();
            foreach (string k in (knots ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) spline.Add(P(k), tangentMode);
            if (spline.Count < 2) throw new ArgumentException("A spline needs at least two knots.");
            spline.Closed = closed;
            return spline;
        }

        [CliGroupCommand(Group, "Edit a SplineContainer with a JSON list of ops: add_knot (position, index, mode), set_knot (index, position, tangentIn, tangentOut, rotation, mode; world: true for world positions), " +
                                "remove_knot, set_closed (value), set_tangent_mode (mode, index or all knots), add_spline (knots, mode), remove_spline. Undoable as one step; marks the scene dirty. " +
                                "An op that fails saves nothing: the splines are left as they were.",
            Name = "edit", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("object", "The GameObject with the SplineContainer", Required = true)] string obj,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            SplineOpList list;
            try { string s = json.Trim(); list = JsonUtility.FromJson<SplineOpList>(s.StartsWith("[") ? "{\"ops\":" + s + "}" : s); }
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given."); return ExitCode.InvalidUsage; }

            SplineContainer c;
            try { c = Find(obj); } catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.NotFound; }
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.RecordObject(c, "cliext.splines edit");
            var results = new List<object>();
            for (int i = 0; i < list.ops.Length; i++)
            {
                try { results.Add(Apply(c, list.ops[i])); }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is ArgumentOutOfRangeException)
                {
                    // All or nothing: undo the ops before this one.
                    Undo.RevertAllDownToGroup(undoGroup);
                    cmd.Result.Write(new { obj, applied = 0, failedAt = i, error = e.Message, saved = false });
                    cmd.Fail($"Op {i} ({list.ops[i].op}) failed: {e.Message} Nothing was saved.");
                    return ExitCode.Failed;
                }
            }
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(c);
            if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
            cmd.Result.Write(new { obj, applied = results.Count, results, splines = Enumerable.Range(0, c.Splines.Count).Select(n => DescribeSpline(c, n, false)).ToArray() });
            return ExitCode.Success;
        }

        private static object Apply(SplineContainer c, SplineOp op)
        {
            Spline S()
            {
                if (op.spline < 0 || op.spline >= c.Splines.Count) throw new ArgumentException($"No spline {op.spline}; the container has {c.Splines.Count}.");
                return c.Splines[op.spline];
            }
            float3 Local(string text) => op.world ? (float3)c.transform.InverseTransformPoint(P(text)) : P(text);
            int Knot(Spline s)
            {
                if (op.index < 0 || op.index >= s.Count) throw new ArgumentException($"No knot {op.index}; the spline has {s.Count}.");
                return op.index;
            }
            TangentMode Mode(string m) => (TangentMode)Enum.Parse(typeof(TangentMode), m, true);

            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add_knot":
                {
                    var s = S();
                    var mode = string.IsNullOrEmpty(op.mode) ? TangentMode.AutoSmooth : Mode(op.mode);
                    if (op.index < 0 || op.index >= s.Count) s.Add(Local(op.position), mode); else s.Insert(op.index, Local(op.position), mode);
                    return new { op = "add_knot", spline = op.spline, knots = s.Count };
                }
                case "set_knot":
                {
                    var s = S();
                    int i = Knot(s);
                    if (!string.IsNullOrEmpty(op.mode)) s.SetTangentMode(i, Mode(op.mode));
                    var k = s[i];
                    if (!string.IsNullOrEmpty(op.position)) k.Position = Local(op.position);
                    if (!string.IsNullOrEmpty(op.tangentIn)) k.TangentIn = P(op.tangentIn);
                    if (!string.IsNullOrEmpty(op.tangentOut)) k.TangentOut = P(op.tangentOut);
                    if (!string.IsNullOrEmpty(op.rotation)) k.Rotation = Quaternion.Euler((Vector3)ValueText.Parse(op.rotation, typeof(Vector3)));
                    s.SetKnot(i, k);
                    return new { op = "set_knot", spline = op.spline, index = i, position = V(s[i].Position), mode = s.GetTangentMode(i).ToString() };
                }
                case "remove_knot":
                {
                    var s = S();
                    s.RemoveAt(Knot(s));
                    return new { op = "remove_knot", spline = op.spline, knots = s.Count };
                }
                case "set_closed":
                {
                    var s = S();
                    s.Closed = string.IsNullOrEmpty(op.value) || (bool)ValueText.Parse(op.value, typeof(bool));
                    return new { op = "set_closed", spline = op.spline, closed = s.Closed };
                }
                case "set_tangent_mode":
                {
                    var s = S();
                    var mode = Mode(op.mode);
                    if (op.index >= 0) s.SetTangentMode(Knot(s), mode); else s.SetTangentMode(mode);
                    return new { op = "set_tangent_mode", spline = op.spline, mode = mode.ToString() };
                }
                case "add_spline":
                {
                    var list = c.Splines.ToList();
                    list.Add(BuildSpline(op.knots, op.mode, op.value == "true"));
                    c.Splines = list;
                    return new { op = "add_spline", index = list.Count - 1 };
                }
                case "remove_spline":
                {
                    var list = c.Splines.ToList();
                    S();
                    list.RemoveAt(op.spline);
                    c.Splines = list;
                    return new { op = "remove_spline", index = op.spline, left = list.Count };
                }
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: add_knot, set_knot, remove_knot, set_closed, set_tangent_mode, add_spline, remove_spline.");
            }
        }

        [CliGroupCommand(Group, "Sample a spline in world space: at --t (0-1 along it) or --distance (world units from the start), the position, tangent (direction) and up; " +
                                "or with --near [x,y,z], the nearest point on the spline to that world point, its t and the distance.",
            Name = "sample", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Sample(
            CommandContext cmd,
            [CliArg("object", "The GameObject with the SplineContainer", Required = true)] string obj,
            [CliArg("spline", "Which spline")] int spline = 0,
            [CliArg("t", "Normalized position along the spline, 0 to 1 (default -1: not used)")] float t = -1,
            [CliArg("distance", "World distance from the start (default -1: not used)")] float distance = -1,
            [CliArg("near", "A world point [x,y,z]")] string near = "",
            [CliArg("count", "With no t, distance or near: this many evenly spaced samples")] int count = 0)
        {
            try
            {
                var c = Find(obj);
                if (spline < 0 || spline >= c.Splines.Count) throw new ArgumentException($"No spline {spline}.");
                var s = c.Splines[spline];
                float4x4 m = c.transform.localToWorldMatrix;
                object At(float tt)
                {
                    c.Evaluate(spline, tt, out float3 pos, out float3 tan, out float3 up);
                    return new { t = Math.Round(tt, 4), position = V(pos), tangent = V(math.normalizesafe(tan)), up = V(up) };
                }
                if (!string.IsNullOrWhiteSpace(near))
                {
                    float3 world = P(near);
                    float3 local = c.transform.InverseTransformPoint(world);
                    float d = SplineUtility.GetNearestPoint(s, local, out float3 nearest, out float nt);
                    float3 nearestWorld = c.transform.TransformPoint(nearest);
                    cmd.Result.Write(new { near = V(world), nearest = V(nearestWorld), t = Math.Round(nt, 4), distance = Math.Round(math.distance(world, nearestWorld), 4) });
                    return ExitCode.Success;
                }
                if (distance >= 0)
                {
                    float length = s.CalculateLength(m);
                    t = length > 0 ? Mathf.Clamp01(distance / length) : 0;
                }
                if (t >= 0) { cmd.Result.Write(At(Mathf.Clamp01(t))); return ExitCode.Success; }
                int n = Math.Max(2, count == 0 ? 5 : count);
                for (int i = 0; i < n; i++) cmd.Result.WriteRecord(At(i / (float)(n - 1)));
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Fail(e.Message); return ExitCode.Failed; }
        }
    }
}

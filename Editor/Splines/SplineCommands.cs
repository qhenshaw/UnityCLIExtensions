using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
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

        internal static readonly string[] Ops = { "add_knot", "set_knot", "remove_knot", "set_closed", "set_tangent_mode", "add_spline", "remove_spline" };

        /// <summary>The SplineContainer on a GameObject named (or at the hierarchy path) <paramref name="obj"/>: exit 13 with the fix when there is none.</summary>
        internal static SplineContainer Find(string obj)
        {
            string n = (obj ?? "").Trim();
            var all = UnityEngine.Object.FindObjectsByType<SplineContainer>(FindObjectsInactive.Include);
            var c = all.FirstOrDefault(x => PathOf(x.transform) == n) ?? all.FirstOrDefault(x => string.Equals(x.name, n, StringComparison.OrdinalIgnoreCase));
            if (c != null) return c;
            var go = string.IsNullOrEmpty(n) ? null : GameObject.Find(n);
            if (all.Length == 0)
                throw new AnimNotFoundException($"{(go != null ? $"'{go.name}' has no SplineContainer" : $"No SplineContainer on '{n}'")}: the open scenes have none. cliext.splines create makes one.");
            string hint = AnimationErrors.Hint(n, all.Select(x => x.name), "Containers", "cliext.splines list");
            throw new AnimNotFoundException(go != null ? $"'{go.name}' has no SplineContainer.{hint}" : $"No SplineContainer on '{n}' in the open scenes.{hint}");
        }

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        private static string V(float3 v) => ValueText.Format((Vector3)v);
        private static float3 P(string text) => (Vector3)ValueText.Parse(text, typeof(Vector3));

        /// <summary>A command argument's vector: a bad one is invalid usage (exit 2).</summary>
        private static Vector3 ArgVector(string text, string arg)
        {
            try { return (Vector3)ValueText.Parse(text, typeof(Vector3)); }
            catch (ArgumentException e) { throw new AnimUsageException($"{arg}: {e.Message}"); }
        }

        /// <summary>
        /// The world bounds of the curve itself: each Bezier segment's end points plus its extremes on every axis (where
        /// the derivative is zero). Spline.GetBounds is the hull of the knots and tangent handles, which is larger than the
        /// curve wherever a tangent points outward (a 6-knot circle of radius 5 gets 10.10 for a 9.74 curve).
        /// </summary>
        internal static Bounds CurveBounds(Spline s, Matrix4x4 toWorld)
        {
            int curves = s.Closed ? s.Count : s.Count - 1;
            if (s.Count == 0) return new Bounds(toWorld.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
            var b = new Bounds(toWorld.MultiplyPoint3x4((Vector3)s[0].Position), Vector3.zero);
            for (int k = 0; k < curves; k++)
            {
                var curve = s.GetCurve(k);
                // An affine transform maps a Bezier curve to the Bezier curve of the mapped control points.
                Vector3 p0 = toWorld.MultiplyPoint3x4((Vector3)curve.P0), p1 = toWorld.MultiplyPoint3x4((Vector3)curve.P1),
                        p2 = toWorld.MultiplyPoint3x4((Vector3)curve.P2), p3 = toWorld.MultiplyPoint3x4((Vector3)curve.P3);
                b.Encapsulate(p0);
                b.Encapsulate(p3);
                for (int axis = 0; axis < 3; axis++)
                    foreach (double t in DerivativeRoots(p0[axis], p1[axis], p2[axis], p3[axis]))
                    {
                        double u = 1 - t;
                        b.Encapsulate((float)(u * u * u) * p0 + (float)(3 * u * u * t) * p1 + (float)(3 * u * t * t) * p2 + (float)(t * t * t) * p3);
                    }
            }
            return b;
        }

        /// <summary>The t in (0, 1) where a cubic Bezier coordinate's derivative is zero.</summary>
        private static IEnumerable<double> DerivativeRoots(double a, double b, double c, double d)
        {
            // B'(t)/3 = (b-a)(1-t)^2 + 2(c-b)(1-t)t + (d-c)t^2 = A t^2 + B t + C
            double qa = (b - a) - 2 * (c - b) + (d - c), qb = 2 * ((c - b) - (b - a)), qc = b - a;
            var roots = new List<double>();
            if (Math.Abs(qa) < 1e-12) { if (Math.Abs(qb) > 1e-12) roots.Add(-qc / qb); }
            else
            {
                double disc = qb * qb - 4 * qa * qc;
                if (disc >= 0)
                {
                    double r = Math.Sqrt(disc);
                    roots.Add((-qb + r) / (2 * qa));
                    roots.Add((-qb - r) / (2 * qa));
                }
            }
            return roots.Where(t => t > 0 && t < 1);
        }

        internal static object DescribeSpline(SplineContainer c, int i, bool knots)
        {
            var s = c.Splines[i];
            var bounds = CurveBounds(s, c.transform.localToWorldMatrix);
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

        [CliGroupCommand(Group, "The SplineContainers in the open scenes, each spline's knot count, closed flag, world length and world bounds of the curve itself (boundsCenter, boundsSize; not the tangent handles).",
            Name = "list", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            foreach (var c in UnityEngine.Object.FindObjectsByType<SplineContainer>(FindObjectsInactive.Include))
                cmd.Result.WriteRecord(new { name = c.name, scene = c.gameObject.scene.name, splines = Enumerable.Range(0, c.Splines.Count).Select(i => DescribeSpline(c, i, false)).ToArray() });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "A SplineContainer's splines with every knot: local and world position, tangents, rotation and tangent mode. An unknown object exits 13 (cliext.splines list names the containers).",
            Name = "inspect", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("object", "The GameObject with the SplineContainer (name or path)", Required = true)] string obj)
        {
            try
            {
                var c = Find(obj);
                cmd.Result.Write(new { name = c.name, splines = Enumerable.Range(0, c.Splines.Count).Select(i => DescribeSpline(c, i, true)).ToArray() });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        [CliGroupCommand(Group, "Create a GameObject with a SplineContainer and one spline through --knots \"[x,y,z];[x,y,z];...\" (at least two, local to --position), with --mode for the tangents and --closed. " +
                                "Everything is checked before anything is made: bad knots, --mode or --position exit 2; a container of that name already existing exits 6 (change it with cliext.splines edit).",
            Name = "create", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("name", "The GameObject's name", Required = true)] string name,
            [CliArg("knots", "Knot positions \"[x,y,z];[x,y,z]\", at least two", Required = true)] string knots,
            [CliArg("mode", "Tangent mode: AutoSmooth, Linear, Mirrored, Continuous, Broken")] string mode = "AutoSmooth",
            [CliArg("closed", "Close the loop")] bool closed = false,
            [CliArg("position", "The GameObject's world position [x,y,z]")] string position = "")
        {
            try
            {
                // Build and check everything first, so a bad argument makes nothing.
                name = (name ?? "").Trim();
                if (name.Length == 0) throw new AnimUsageException("--name is empty: give the GameObject a name.");
                Vector3? pos = string.IsNullOrWhiteSpace(position) ? (Vector3?)null : ArgVector(position, "--position");
                Spline spline;
                try { spline = BuildSpline(knots, mode, closed); }
                catch (ArgumentException e) when (!(e is AnimUsageException)) { throw new AnimUsageException(e.Message); }
                var existing = UnityEngine.Object.FindObjectsByType<SplineContainer>(FindObjectsInactive.Include)
                    .FirstOrDefault(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    throw new ArgumentException($"A SplineContainer named '{existing.name}' already exists: change it with cliext.splines edit --object {existing.name}, or pick another --name.");

                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "cliext.splines create");
                if (pos.HasValue) go.transform.position = pos.Value;
                var c = go.AddComponent<SplineContainer>();
                c.Spline = spline;
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(go.scene);
                cmd.Result.Write(DescribeSpline(c, 0, true));
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        internal static TangentMode ParseMode(string mode) => AnimationErrors.ParseEnum<TangentMode>(mode, "tangent mode");

        internal static Spline BuildSpline(string knots, string mode, bool closed)
        {
            var tangentMode = string.IsNullOrWhiteSpace(mode) ? TangentMode.AutoSmooth : ParseMode(mode);
            var parts = (knots ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Where(k => k.Trim().Length > 0).ToArray();
            if (parts.Length < 2) throw new ArgumentException($"A spline needs at least two knots: give \"[x,y,z];[x,y,z]\"{(parts.Length == 1 ? $", not just {parts[0].Trim()}" : "")}.");
            var spline = new Spline();
            for (int i = 0; i < parts.Length; i++)
            {
                float3 p;
                try { p = P(parts[i]); }
                catch (ArgumentException e) { throw new ArgumentException($"Knot {i}: {e.Message}"); }
                spline.Add(p, tangentMode);
            }
            spline.Closed = closed;
            return spline;
        }

        [CliGroupCommand(Group, "Edit a SplineContainer with a JSON list of ops: add_knot (position, index to insert at or -1 to append, mode), " +
                                "set_knot (index, and position, tangentIn, tangentOut, rotation and/or mode; world: true for a world position), remove_knot (index), set_closed (value, default true), " +
                                "set_tangent_mode (mode, index or all knots), add_spline (knots, mode, value true to close it), remove_spline. Every op takes spline (default 0). " +
                                "Undoable as one step; marks the scene dirty. An op that fails saves nothing (an unknown op, spline or knot, a bad value, or one that would change nothing): " +
                                "it is reported as failedAt with its error, exit 6, and the splines and the Undo history are left as they were. An unknown object exits 13; bad JSON or no ops exits 2.",
            Name = "edit", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("object", "The GameObject with the SplineContainer (name or path)", Required = true)] string obj,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            SplineOpList list;
            try { string s = json.Trim(); list = JsonUtility.FromJson<SplineOpList>(s.StartsWith("[") ? "{\"ops\":" + s + "}" : s); }
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given: pass a JSON array such as [{\"op\":\"add_knot\",\"position\":\"[0,0,1]\"}]."); return ExitCode.InvalidUsage; }

            SplineContainer c;
            try { c = Find(obj); } catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            var results = new List<object>();
            if (!TryApplyAll(c, list.ops, results, out int failedAt, out string error))
            {
                cmd.Result.Write(new { obj = c.name, applied = 0, failedAt, error, saved = false });
                cmd.Fail($"Op {failedAt} ({list.ops[failedAt].op}) failed: {error} Nothing was changed.");
                return ExitCode.Failed;
            }
            if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
            cmd.Result.Write(new { obj = c.name, applied = results.Count, results, splines = Enumerable.Range(0, c.Splines.Count).Select(n => DescribeSpline(c, n, false)).ToArray() });
            return ExitCode.Success;
        }

        /// <summary>
        /// Applies <paramref name="ops"/> to <paramref name="c"/> in one Undo group. When one fails, the ones before it
        /// are reverted (the Undo group is dropped, leaving no redo) and false is returned with its index and message.
        /// </summary>
        internal static bool TryApplyAll(SplineContainer c, IList<SplineOp> ops, List<object> results, out int failedAt, out string error)
        {
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("cliext.splines edit");
            Undo.RecordObject(c, "cliext.splines edit");
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(Apply(c, ops[i])); }
                catch (Exception e)
                {
                    // All or nothing: undo the ops before this one.
                    Undo.RevertAllDownToGroup(undoGroup);
                    results.Clear();
                    failedAt = i;
                    error = AnimationErrors.Message(e);
                    return false;
                }
            }
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(c);
            failedAt = -1;
            error = null;
            return true;
        }

        internal static object Apply(SplineContainer c, SplineOp op)
        {
            string name = (op.op ?? "").Trim().ToLowerInvariant();
            Spline S()
            {
                if (op.spline < 0 || op.spline >= c.Splines.Count)
                    throw new ArgumentException($"No spline {op.spline}: {c.name} has {c.Splines.Count} ({(c.Splines.Count == 0 ? "add_spline adds one" : $"0 to {c.Splines.Count - 1}")}). cliext.splines inspect lists them.");
                return c.Splines[op.spline];
            }
            float3 Vec(string text, string field)
            {
                try { return P(text); }
                catch (ArgumentException e) { throw new ArgumentException($"{field}: {e.Message}"); }
            }
            float3 Local(string text, string field) => op.world ? (float3)c.transform.InverseTransformPoint(Vec(text, field)) : Vec(text, field);
            int Knot(Spline s)
            {
                if (op.index < 0) throw new ArgumentException($"{name} needs an index: the knot, 0 to {s.Count - 1}.");
                if (op.index >= s.Count) throw new ArgumentException($"No knot {op.index}: spline {op.spline} has {s.Count} (0 to {s.Count - 1}). cliext.splines inspect lists them.");
                return op.index;
            }
            bool Bool(string text, string field)
            {
                if (string.IsNullOrWhiteSpace(text)) return true;
                try { return (bool)ValueText.Parse(text, typeof(bool)); }
                catch (ArgumentException e) { throw new ArgumentException($"{field}: {e.Message}"); }
            }

            switch (name)
            {
                case "add_knot":
                {
                    var s = S();
                    if (string.IsNullOrWhiteSpace(op.position)) throw new ArgumentException("add_knot needs a position [x,y,z].");
                    var mode = string.IsNullOrWhiteSpace(op.mode) ? TangentMode.AutoSmooth : ParseMode(op.mode);
                    var p = Local(op.position, "position");
                    if (op.index > s.Count) throw new ArgumentException($"add_knot index {op.index} is past the end: spline {op.spline} has {s.Count} knots (insert at 0 to {s.Count}, or -1 to append).");
                    if (op.index < 0 || op.index == s.Count) s.Add(p, mode); else s.Insert(op.index, p, mode);
                    return new { op = "add_knot", spline = op.spline, knots = s.Count };
                }
                case "set_knot":
                {
                    var s = S();
                    int i = Knot(s);
                    if (string.IsNullOrWhiteSpace(op.position) && string.IsNullOrWhiteSpace(op.tangentIn) && string.IsNullOrWhiteSpace(op.tangentOut) &&
                        string.IsNullOrWhiteSpace(op.rotation) && string.IsNullOrWhiteSpace(op.mode))
                        throw new ArgumentException("set_knot needs position, tangentIn, tangentOut, rotation or mode: it would change nothing.");
                    var before = s[i];
                    var beforeMode = s.GetTangentMode(i);
                    var k = before;
                    if (!string.IsNullOrWhiteSpace(op.position)) k.Position = Local(op.position, "position");
                    if (!string.IsNullOrWhiteSpace(op.tangentIn)) k.TangentIn = Vec(op.tangentIn, "tangentIn");
                    if (!string.IsNullOrWhiteSpace(op.tangentOut)) k.TangentOut = Vec(op.tangentOut, "tangentOut");
                    if (!string.IsNullOrWhiteSpace(op.rotation)) k.Rotation = Quaternion.Euler((Vector3)Vec(op.rotation, "rotation"));
                    var mode = string.IsNullOrWhiteSpace(op.mode) ? beforeMode : ParseMode(op.mode);
                    if (mode == beforeMode && Same(k, before))
                        throw new ArgumentException($"Knot {i} already has those values: set_knot would change nothing.");
                    if (mode != beforeMode) s.SetTangentMode(i, mode);
                    // Keep the tangents the op gave even when the mode recomputes them; otherwise keep what the mode made.
                    var now = s[i];
                    if (!string.IsNullOrWhiteSpace(op.position)) now.Position = k.Position;
                    if (!string.IsNullOrWhiteSpace(op.tangentIn)) now.TangentIn = k.TangentIn;
                    if (!string.IsNullOrWhiteSpace(op.tangentOut)) now.TangentOut = k.TangentOut;
                    if (!string.IsNullOrWhiteSpace(op.rotation)) now.Rotation = k.Rotation;
                    s.SetKnot(i, now);
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
                    bool closed = Bool(op.value, "value");
                    if (s.Closed == closed) throw new ArgumentException($"Spline {op.spline} is already {(closed ? "closed" : "open")}: set_closed would change nothing.");
                    s.Closed = closed;
                    return new { op = "set_closed", spline = op.spline, closed = s.Closed };
                }
                case "set_tangent_mode":
                {
                    var s = S();
                    if (string.IsNullOrWhiteSpace(op.mode)) throw new ArgumentException($"set_tangent_mode needs a mode: {string.Join(", ", Enum.GetNames(typeof(TangentMode)))}.");
                    var mode = ParseMode(op.mode);
                    if (op.index >= 0)
                    {
                        int i = Knot(s);
                        if (s.GetTangentMode(i) == mode) throw new ArgumentException($"Knot {i} is already {mode}: set_tangent_mode would change nothing.");
                        s.SetTangentMode(i, mode);
                    }
                    else
                    {
                        if (Enumerable.Range(0, s.Count).All(i => s.GetTangentMode(i) == mode))
                            throw new ArgumentException($"Every knot of spline {op.spline} is already {mode}: set_tangent_mode would change nothing.");
                        s.SetTangentMode(mode);
                    }
                    return new { op = "set_tangent_mode", spline = op.spline, mode = mode.ToString() };
                }
                case "add_spline":
                {
                    var spline = BuildSpline(op.knots, op.mode, !string.IsNullOrWhiteSpace(op.value) && Bool(op.value, "value"));
                    var list = c.Splines.ToList();
                    list.Add(spline);
                    c.Splines = list;
                    return new { op = "add_spline", index = list.Count - 1 };
                }
                case "remove_spline":
                {
                    S();
                    var list = c.Splines.ToList();
                    list.RemoveAt(op.spline);
                    c.Splines = list;
                    return new { op = "remove_spline", index = op.spline, left = list.Count };
                }
                default:
                {
                    var close = AnimationErrors.Close(name, Ops);
                    throw new ArgumentException($"Unknown op '{op.op}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Ops: {string.Join(", ", Ops)}.");
                }
            }
        }

        private static bool Same(BezierKnot a, BezierKnot b) =>
            math.all(math.abs(a.Position - b.Position) < 1e-5f) && math.all(math.abs(a.TangentIn - b.TangentIn) < 1e-5f) &&
            math.all(math.abs(a.TangentOut - b.TangentOut) < 1e-5f) && Quaternion.Angle(a.Rotation, b.Rotation) < 1e-3f;

        [CliGroupCommand(Group, "Sample a spline in world space: at --t (0-1 along it) or --distance (world units from the start), the position, tangent (direction) and up; " +
                                "or with --near [x,y,z], the nearest point on the spline to that world point, its t and the distance; or with none of them, --count N evenly spaced samples (default 5): on an open spline t = k/(N-1), so both ends are included; " +
                                "on a closed spline t = k/N (k = 0 to N-1), N different points, as t = 1 is the start again. " +
                                "Give only one of --t, --distance, --near and --count. An unknown object or spline exits 13; a --t outside 0-1, a --distance past the end, a bad --near or --count exits 2.",
            Name = "sample", Tags = new[] { "cliext", "splines" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Sample(
            CommandContext cmd,
            [CliArg("object", "The GameObject with the SplineContainer (name or path)", Required = true)] string obj,
            [CliArg("spline", "Which spline")] int spline = 0,
            [CliArg("t", "Normalized position along the spline, 0 to 1; -1 means not used")] float t = -1,
            [CliArg("distance", "World distance from the start, 0 to the spline's length; -1 means not used")] float distance = -1,
            [CliArg("near", "A world point [x,y,z]")] string near = "",
            [CliArg("count", "With no t, distance or near: this many evenly spaced samples, 2 or more; 0 means 5")] int count = 0)
        {
            try
            {
                bool useT = t != -1, useD = distance != -1, useNear = !string.IsNullOrWhiteSpace(near), useCount = count != 0;
                int given = (useT ? 1 : 0) + (useD ? 1 : 0) + (useNear ? 1 : 0) + (useCount ? 1 : 0);
                if (given > 1) throw new AnimUsageException("Give only one of --t, --distance, --near and --count.");
                if (useT && (float.IsNaN(t) || t < 0 || t > 1)) throw new AnimUsageException($"--t {ValueText.Format(t)} is outside 0-1: give 0 (the start) to 1 (the end), or use --distance for world units.");
                if (useD && (float.IsNaN(distance) || distance < 0)) throw new AnimUsageException($"--distance {ValueText.Format(distance)} is negative: give world units from the start, 0 or more.");
                if (useCount && count < 2) throw new AnimUsageException($"--count {count}: give 2 or more samples.");
                float3 world = useNear ? (float3)ArgVector(near, "--near") : default;

                var c = Find(obj);
                if (spline < 0 || spline >= c.Splines.Count)
                    throw new AnimNotFoundException($"No spline {spline} on {c.name}: it has {c.Splines.Count}{(c.Splines.Count > 0 ? $" (0 to {c.Splines.Count - 1})" : "")}. cliext.splines inspect lists them.");
                var s = c.Splines[spline];
                if (s.Count == 0) throw new ArgumentException($"Spline {spline} on {c.name} has no knots to sample: add some with cliext.splines edit add_knot.");
                float4x4 m = c.transform.localToWorldMatrix;
                object At(float tt)
                {
                    c.Evaluate(spline, tt, out float3 pos, out float3 tan, out float3 up);
                    return new { t = Math.Round(tt, 4), position = V(pos), tangent = V(math.normalizesafe(tan)), up = V(up) };
                }
                if (useNear)
                {
                    float3 local = c.transform.InverseTransformPoint(world);
                    SplineUtility.GetNearestPoint(s, local, out float3 nearest, out float nt);
                    float3 nearestWorld = c.transform.TransformPoint(nearest);
                    cmd.Result.Write(new { near = V(world), nearest = V(nearestWorld), t = Math.Round(nt, 4), distance = Math.Round(math.distance(world, nearestWorld), 4) });
                    return ExitCode.Success;
                }
                if (useD)
                {
                    float length = s.CalculateLength(m);
                    if (distance > length + 1e-4f)
                        throw new AnimUsageException($"--distance {ValueText.Format(distance)} is past the end of spline {spline} ({ValueText.Format((float)Math.Round(length, 3))} long).");
                    t = length > 0 ? Mathf.Clamp01(distance / length) : 0;
                    useT = true;
                }
                if (useT) { cmd.Result.Write(At(t)); return ExitCode.Success; }
                foreach (float st in SampleTs(count == 0 ? 5 : count, s.Closed)) cmd.Result.WriteRecord(At(st));
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        /// <summary>
        /// The t of <paramref name="n"/> evenly spaced samples: k/(n-1) on an open spline (both ends included), k/n on a
        /// closed one (t = 1 is the start again, so it is left out and the n points are all different).
        /// </summary>
        internal static float[] SampleTs(int n, bool closed) =>
            Enumerable.Range(0, n).Select(k => closed ? k / (float)n : k / (float)(n - 1)).ToArray();
    }
}

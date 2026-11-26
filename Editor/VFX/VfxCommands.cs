using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor.VFX;
using UnityEngine;

namespace UnityCLIExtensions.VFX
{
    /// <summary>
    /// <c>cliext.vfx</c>: read and edit VFX Graphs from the command line: list what can be added, inspect a graph with
    /// ids for every context, block, operator and parameter, and add, remove, set, link and unlink, one at a time or
    /// many in one <c>edit</c> call (one save and one compile). Built when the project has the Visual Effect Graph package.
    /// </summary>
    internal static class VfxCommands
    {
        private const string Group = "cliext.vfx";
        private static readonly string[] Tags = { "cliext", "vfx" };

        [CliGroupCommand(Group, "What can go in a VFX Graph: contexts, blocks, operators and parameter types, with the key to pass to add (category/name). " +
                                "--kind narrows to context, block, operator or parameter; --filter matches part of the key.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Types(
            CommandContext cmd,
            [CliArg("kind", "context, block, operator or parameter (default: all)")] string kind = "",
            [CliArg("filter", "Only entries whose category/name contains this")] string filter = "")
        {
            foreach (var e in VfxGraphDocument.Library(kind).Where(e => string.IsNullOrEmpty(filter) || e.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                cmd.Result.WriteRecord(new { e.kind, key = e.Key, name = e.DisplayName, category = e.DisplayCategory, modelType = e.modelType?.Name });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Describe a VFX Graph: its contexts (with flow links and blocks), operators and parameters, each with an id, its settings and its input slots " +
                                "(values or links). Pass --id for one model. Ids are what add, set, link and remove take.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset path or GUID", Required = true)] string path,
            [CliArg("id", "Only this model")] string id = "",
            [CliArg("details", "Include settings and slots (false: just the structure)")] bool details = true)
        {
            return Guard(cmd, () =>
            {
                var doc = VfxGraphDocument.Open(path);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    cmd.Result.Write(VfxGraphOps.Describe(doc.Find(id), details));
                    return ExitCode.Success;
                }
                var graph = doc.graph;
                VfxGraphCheck.Result check = VfxGraphCheck.Run(doc);
                cmd.Result.Write(new
                {
                    path = doc.path,
                    contexts = graph.children.OfType<VFXContext>().Select(c => VfxGraphOps.Describe(c, details)).ToArray(),
                    operators = graph.children.Where(m => !(m is VFXContext) && !(m is VFXParameter)).Select(m => VfxGraphOps.Describe(m, details)).ToArray(),
                    parameters = graph.children.OfType<VFXParameter>().Select(p => VfxGraphOps.Describe(p, details)).ToArray(),
                    warnings = check.warnings.ToArray(),
                    eventAttributes = check.eventAttributes.ToArray()
                });
                return ExitCode.Success;
            });
        }

        [CliGroupCommand(Group, "Check a VFX Graph for mistakes that compile but show nothing (a burst with Count 0, a spawner with no burst or rate, capacity 0, " +
                                "a flipbook output with no texture) plus VFX Graph's own warnings, and list the attributes each event's systems read from script " +
                                "(scale is read as scaleX, scaleY, scaleZ). Exit 6 on errors; with --strict true also on warnings.",
            Name = "check", Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Check(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset path or GUID", Required = true)] string path,
            [CliArg("strict", "Also fail on warnings, not only errors")] bool strict = false)
        {
            return Guard(cmd, () =>
            {
                VfxGraphDocument doc = VfxGraphDocument.Open(path);
                VfxGraphCheck.Result check = VfxGraphCheck.Run(doc);
                cmd.Result.Write(new { path = doc.path, warnings = check.warnings.ToArray(), eventAttributes = check.eventAttributes.ToArray() });
                if (check.HasErrors || (strict && check.HasWarnings))
                {
                    cmd.Log.Error($"{doc.path}: {check.warnings.Count(w => w.severity == VfxGraphCheck.Error)} error(s), {check.warnings.Count(w => w.severity == VfxGraphCheck.Warning)} warning(s).");
                    return ExitCode.Failed;
                }
                return ExitCode.Success;
            });
        }

        [CliGroupCommand(Group, "Create an empty VFX Graph (no systems) at --path. Add systems with cliext.vfx add or edit.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("path", "Where to create it, such as Assets/VFX/Spark.vfx", Required = true)] string path)
        {
            return Guard(cmd, () =>
            {
                var doc = VfxGraphDocument.Create(path);
                cmd.Result.Write(new { path = doc.path, created = true });
                return ExitCode.Success;
            });
        }

        [CliGroupCommand(Group, "Add a context, block, operator or parameter to a VFX Graph, then save and compile it. Returns the new model's id. " +
                                "--type is a key from cliext.vfx types (category/name), a name, or a class name. A block needs --context.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Add(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset", Required = true)] string path,
            [CliArg("kind", "context, block, operator or parameter", Required = true)] string kind,
            [CliArg("type", "What to add (see cliext.vfx types)", Required = true)] string type,
            [CliArg("context", "For a block: the context's id")] string context = "",
            [CliArg("index", "For a block: its position in the context (-1: last)")] int index = -1,
            [CliArg("x", "Graph view position")] float x = float.NaN,
            [CliArg("y", "Graph view position")] float y = float.NaN)
            => RunOps(cmd, path, new[] { new VfxOp { op = "add", kind = kind, type = type, context = context, index = index, x = x, y = y } });

        [CliGroupCommand(Group, "Remove a context (with its blocks), block, operator or parameter, unlinking it first; then save and compile.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Remove(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset", Required = true)] string path,
            [CliArg("id", "The model's id", Required = true)] string id)
            => RunOps(cmd, path, new[] { new VfxOp { op = "remove", id = id } });

        [CliGroupCommand(Group, "Set a model's setting (--setting, such as blendMode or capacity) or an input slot's value (--slot, such as Rate or velocity.y; --space Local|World), " +
                                "then save and compile. Values: numbers, true/false, enum names, [x,y,z] vectors and colours, asset paths, [[t,v],...] curves, [[t,r,g,b],...] gradients, JSON.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Set(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset", Required = true)] string path,
            [CliArg("id", "The model's id", Required = true)] string id,
            [CliArg("setting", "A setting's name")] string setting = "",
            [CliArg("slot", "An input slot's name or path")] string slot = "",
            [CliArg("value", "The value")] string value = null,
            [CliArg("space", "For a slot: Local, World or None")] string space = "")
            => RunOps(cmd, path, new[] { new VfxOp { op = "set", id = id, setting = setting, slot = slot, value = value, space = space } });

        [CliGroupCommand(Group, "Link two contexts (flow: --from and --to are context ids) or an output slot to an input slot (data: <id>:<slot>), then save and compile.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Link(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset", Required = true)] string path,
            [CliArg("from", "Context id, or <id>:<output slot>", Required = true)] string from,
            [CliArg("to", "Context id, or <id>:<input slot>", Required = true)] string to,
            [CliArg("from-index", "Flow output index (such as a spawner's start/stop)")] int fromIndex = 0,
            [CliArg("to-index", "Flow input index")] int toIndex = 0)
            => RunOps(cmd, path, new[] { new VfxOp { op = "link", from = from, to = to, fromIndex = fromIndex, toIndex = toIndex } });

        [CliGroupCommand(Group, "Remove a flow or data link, as cliext.vfx link takes it, then save and compile.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Unlink(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset", Required = true)] string path,
            [CliArg("from", "Context id, or <id>:<output slot>", Required = true)] string from,
            [CliArg("to", "Context id, or <id>:<input slot>", Required = true)] string to,
            [CliArg("from-index", "Flow output index")] int fromIndex = 0,
            [CliArg("to-index", "Flow input index")] int toIndex = 0)
            => RunOps(cmd, path, new[] { new VfxOp { op = "unlink", from = from, to = to, fromIndex = fromIndex, toIndex = toIndex } });

        [CliGroupCommand(Group, "Apply many edits in order, then save and compile once. --ops (or --ops-file) is a JSON array of ops: " +
                                "{\"op\":\"add\",\"kind\":\"context\",\"type\":\"Spawn\",\"as\":\"spawn\"}, then refer to it as \"$spawn\". " +
                                "Ops: add, remove, set, link, unlink, move, rename, expose, with the fields of the single commands (from-index is fromIndex). " +
                                "An op that fails stops the run; the ops before it are kept and saved.",
            Tags = new[] { "cliext", "vfx" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "The .vfx asset", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array, for long lists or awkward shell quoting")] string opsFile = "")
        {
            string json;
            try { json = !string.IsNullOrWhiteSpace(opsFile) ? File.ReadAllText(opsFile) : ops; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { cmd.Log.Error($"Can't read --ops-file: {e.Message}"); return ExitCode.NotFound; }
            if (string.IsNullOrWhiteSpace(json)) { cmd.Log.Error("Pass --ops or --ops-file."); return ExitCode.Failed; }
            VfxOpList list;
            try
            {
                string trimmed = json.Trim();
                list = JsonUtility.FromJson<VfxOpList>(trimmed.StartsWith("[") ? "{\"ops\":" + trimmed + "}" : trimmed);
            }
            catch (Exception e) { cmd.Log.Error($"The ops aren't valid JSON: {e.Message}"); return ExitCode.Failed; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Log.Error("No ops given."); return ExitCode.Failed; }
            return RunOps(cmd, path, list.ops);
        }

        // ---------------------------------------------------------------------------------------------------------

        private static ExitCode RunOps(CommandContext cmd, string path, IReadOnlyList<VfxOp> ops)
        {
            VfxGraphDocument doc;
            try { doc = VfxGraphDocument.Open(path); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }

            var aliases = new Dictionary<string, VFXModel>();
            var results = new List<object>();
            string failure = null;
            int failedAt = -1;
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(VfxGraphOps.Apply(doc, ops[i], aliases)); }
                catch (Exception e)
                {
                    failure = e is ArgumentException ? e.Message : e.ToString();
                    failedAt = i;
                    break;
                }
            }

            List<string> compileMessages = results.Count > 0 ? doc.Save() : new List<string>();
            VfxGraphCheck.Result check = VfxGraphCheck.Run(doc);
            var shaped = results.Select(r => r is AddResult a
                ? (object)new { op = "add", id = VfxGraphDocument.IdOf(a.model), alias = a.alias, entry = a.entry, kind = VfxGraphDocument.KindOf(a.model), type = a.model.GetType().Name }
                : r).ToArray();
            cmd.Result.Write(new
            {
                path = doc.path,
                applied = results.Count,
                total = ops.Count,
                results = shaped,
                failedAt = failedAt >= 0 ? (int?)failedAt : null,
                error = failure,
                compile = compileMessages.ToArray(),
                warnings = check.warnings.ToArray(),
                eventAttributes = check.eventAttributes.ToArray()
            });
            if (failure == null && check.HasWarnings)
                cmd.Log.Warning($"The graph has {check.warnings.Count(w => w.severity != VfxGraphCheck.Info)} warning(s); see warnings.");
            if (failure != null)
            {
                cmd.Log.Error($"Op {failedAt} ({ops[failedAt].op}) failed: {failure}" + (results.Count > 0 ? $" The {results.Count} op(s) before it were saved." : ""));
                return ExitCode.Failed;
            }
            if (compileMessages.Any(m => m.StartsWith("Error") || m.StartsWith("Exception")))
                cmd.Log.Warning($"The graph saved, but compiling it logged {compileMessages.Count} message(s); see compile.");
            return ExitCode.Success;
        }

        private static ExitCode Guard(CommandContext cmd, Func<ExitCode> body)
        {
            try { return body(); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace UnityCLIExtensions.ShaderGraph
{
    /// <summary>
    /// <c>cliext.shadergraph</c>: read and edit Shader Graphs from the command line, with the shader's compile errors
    /// returned as data after every edit. Built when the project has the Shader Graph package.
    /// </summary>
    internal static class ShaderGraphCommands
    {
        private const string Group = "cliext.shadergraph";

        [CliGroupCommand(Group, "The nodes a Shader Graph can hold, by their menu path (Math/Basic/Add); --filter matches part of it. Also lists the targets for create.",
            Name = "types", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Types(
            CommandContext cmd,
            [CliArg("filter", "Only nodes whose path contains this")] string filter = "",
            [CliArg("targets", "List the render pipeline targets (and their materials) instead")] bool targets = false)
        {
            if (targets)
            {
                var graph = new GraphData();
                foreach (var t in graph.allPotentialTargets)
                    cmd.Result.WriteRecord(new { target = t.displayName, materials = ShaderGraphDocument.SubTargetsOf(t).Select(s => s.displayName).ToArray() });
                return ExitCode.Success;
            }
            foreach (var e in ShaderGraphDocument.NodeTypes().OrderBy(e => e.key)
                         .Where(e => string.IsNullOrEmpty(filter) || e.key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                cmd.Result.WriteRecord(new { key = e.key, name = e.name, nodeType = e.type.Name });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Describe a Shader Graph: its targets, blocks (vertex and fragment), nodes with their slots (values or links), properties, and the shader's compile errors. " +
                                "Every node and property has an id that the edit commands take.",
            Name = "inspect", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset path or GUID", Required = true)] string path,
            [CliArg("id", "Only this node")] string id = "",
            [CliArg("details", "Include slots (false: just the structure)")] bool details = true)
        {
            try { return InspectBody(cmd, path, id, details); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            catch (Exception e) { cmd.Log.Error(e.ToString()); return ExitCode.Failed; }
        }

        private static ExitCode InspectBody(CommandContext cmd, string path, string id, bool details)
        {
            var doc = ShaderGraphDocument.Open(path);
            var graph = doc.graph;
            if (!string.IsNullOrWhiteSpace(id))
            {
                try { cmd.Result.Write(ShaderGraphOps.DescribeNode(graph, doc.FindNode(id), details)); }
                catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
                return ExitCode.Success;
            }
            var blocks = new HashSet<string>(graph.vertexContext.blocks.Concat(graph.fragmentContext.blocks).Select(b => b.value.objectId));
            cmd.Result.Write(new
            {
                path = doc.path,
                subGraph = graph.isSubGraph,
                targets = graph.activeTargets.Select(t => new { target = t.displayName, material = t.activeSubTarget?.displayName }).ToArray(),
                vertex = graph.vertexContext.blocks.Select(b => ShaderGraphOps.DescribeNode(graph, b.value, details)).ToArray(),
                fragment = graph.fragmentContext.blocks.Select(b => ShaderGraphOps.DescribeNode(graph, b.value, details)).ToArray(),
                blocksAvailable = ShaderGraphOps.AvailableBlocks(graph).Select(d => d.displayName).ToArray(),
                nodes = graph.GetNodes<AbstractMaterialNode>().Where(n => !blocks.Contains(n.objectId)).Select(n => ShaderGraphOps.DescribeNode(graph, n, details)).ToArray(),
                properties = graph.properties.Select(ShaderGraphOps.DescribeProperty).ToArray(),
                keywords = graph.keywords.Select(k => k.displayName).ToArray(),
                compile = ShaderGraphDocument.Compile(doc.path, graph)
            });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Create a Shader Graph for a target (--target HDRP, Universal, Built-In, ...; --material Lit, Unlit, ...) with that material's default blocks, " +
                                "or copy one (--from). See cliext.shadergraph types --targets true.",
            Name = "create", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("path", "Where to create it, such as Assets/Shaders/Glow.shadergraph", Required = true)] string path,
            [CliArg("target", "The render pipeline target")] string target = "",
            [CliArg("material", "The target's material type (sub-target)")] string material = "",
            [CliArg("from", "Copy this graph instead")] string from = "")
        {
            try
            {
                var doc = ShaderGraphDocument.Create(path, target, material, from);
                cmd.Result.Write(new
                {
                    path = doc.path,
                    targets = doc.graph.activeTargets.Select(t => new { target = t.displayName, material = t.activeSubTarget?.displayName }).ToArray(),
                    compile = ShaderGraphDocument.Compile(doc.path, doc.graph)
                });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Add a node (--type, see types), then save, compile and return its id and the compile errors.",
            Name = "add", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Add(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("type", "The node (menu path, name or class)", Required = true)] string type,
            [CliArg("x", "Graph position")] float x = float.NaN,
            [CliArg("y", "Graph position")] float y = float.NaN)
            => RunOps(cmd, path, new[] { new ShaderGraphOp { op = "add_node", type = type, x = x, y = y } }, false);

        [CliGroupCommand(Group, "Add a property (--type float, vector2-4, color, texture2d, cubemap, boolean, ...) with a name, reference, default value and exposed flag; then save and compile.",
            Name = "add_property", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode AddProperty(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("type", "The property type", Required = true)] string type,
            [CliArg("name", "Display name")] string name = "",
            [CliArg("reference", "Reference name, such as _BaseColor")] string reference = "",
            [CliArg("value", "Default value")] string value = null)
            => RunOps(cmd, path, new[] { new ShaderGraphOp { op = "add_property", type = type, name = name, reference = reference, value = value } }, false);

        [CliGroupCommand(Group, "Remove a node, block or property (with its links); then save and compile.",
            Name = "remove", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Remove(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("id", "Node id, or property id or name", Required = true)] string id)
            => RunOps(cmd, path, new[] { new ShaderGraphOp { op = "remove", id = id } }, false);

        [CliGroupCommand(Group, "Set a node's input slot value (--id node --slot name) or a property's default (--id property); then save and compile. " +
                                "Values: numbers, true/false, [x,y,z,w] vectors and colours (or #RRGGBB), texture asset paths, enum names.",
            Name = "set", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Set(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("id", "Node id, or property id or name", Required = true)] string id,
            [CliArg("slot", "A node's input slot (id, display name or shader name)")] string slot = "",
            [CliArg("setting", "A node's own setting, such as a Custom Function's functionBody (see inspect --id)")] string setting = "",
            [CliArg("value", "The value", Required = true)] string value = null)
            => RunOps(cmd, path, new[] { new ShaderGraphOp { op = "set", id = id, slot = slot, setting = setting, value = value } }, false);

        [CliGroupCommand(Group, "Link an output slot (<node>:<slot>) to an input slot, or a property (its name or id) to an input slot through a new property node; then save and compile.",
            Name = "link", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Link(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("from", "<node id>:<output slot>, or a property", Required = true)] string from,
            [CliArg("to", "<node id>:<input slot>", Required = true)] string to)
            => RunOps(cmd, path, new[] { new ShaderGraphOp { op = "link", from = from, to = to } }, false);

        [CliGroupCommand(Group, "Remove the links into an input slot (--to), or only the one from --from; then save and compile.",
            Name = "unlink", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Unlink(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("to", "<node id>:<input slot>", Required = true)] string to,
            [CliArg("from", "<node id>:<output slot> (default: every link into to)")] string from = "")
            => RunOps(cmd, path, new[] { new ShaderGraphOp { op = "unlink", from = from, to = to } }, false);

        [CliGroupCommand(Group, "Apply many edits in order, then save and compile once. --ops (or --ops-file) is a JSON array: " +
                                "{\"op\":\"add_node\",\"type\":\"Math/Basic/Multiply\",\"as\":\"mul\"}, then \"$mul\". Ops: add_node, add_property, add_block, add_port, remove_port, remove, set (slot, setting or property), link, unlink, move, rename, expose. " +
                                "An op that fails stops the run and nothing is saved. Refuses while the graph is open in a Shader Graph window (--force true to edit anyway).",
            Name = "edit", Tags = new[] { "cliext", "shadergraph" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "The .shadergraph asset", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "",
            [CliArg("force", "Edit even while a Shader Graph window has it open (the window's copy may overwrite the edit)")] bool force = false)
        {
            string json = !string.IsNullOrWhiteSpace(opsFile) ? File.ReadAllText(opsFile) : ops;
            if (string.IsNullOrWhiteSpace(json)) { cmd.Log.Error("Pass --ops or --ops-file."); return ExitCode.Failed; }
            ShaderGraphOpList list;
            try
            {
                string trimmed = json.Trim();
                list = JsonUtility.FromJson<ShaderGraphOpList>(trimmed.StartsWith("[") ? "{\"ops\":" + trimmed + "}" : trimmed);
            }
            catch (Exception e) { cmd.Log.Error($"The ops aren't valid JSON: {e.Message}"); return ExitCode.Failed; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Log.Error("No ops given."); return ExitCode.Failed; }
            return RunOps(cmd, path, list.ops, force);
        }

        // ---------------------------------------------------------------------------------------------------------

        private static ExitCode RunOps(CommandContext cmd, string path, IReadOnlyList<ShaderGraphOp> ops, bool force)
        {
            ShaderGraphDocument doc;
            try { doc = ShaderGraphDocument.Open(path); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            if (!force && ShaderGraphDocument.IsOpenInWindow(doc.path))
            {
                cmd.Log.Error($"{doc.path} is open in a Shader Graph window, whose own copy would overwrite this edit (and may prompt about the file changing). Close it, or pass --force true.");
                return ExitCode.Failed;
            }

            // Ops run on an in-memory copy loaded from the file, so a failed op leaves the asset untouched.
            var aliases = new Dictionary<string, string>();
            var results = new List<object>();
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(ShaderGraphOps.Apply(doc, ops[i], aliases)); }
                catch (Exception e)
                {
                    string message = e is ArgumentException ? e.Message : e.ToString();
                    cmd.Result.Write(new { path = doc.path, applied = 0, total = ops.Count, failedAt = i, error = message, saved = false });
                    cmd.Log.Error($"Op {i} ({ops[i].op}) failed: {message} Nothing was saved.");
                    return ExitCode.Failed;
                }
            }
            var report = doc.Save();
            cmd.Result.Write(new { path = doc.path, applied = results.Count, total = ops.Count, results, compile = report });
            if (report.hasErrors)
            {
                cmd.Log.Warning($"Saved, but the shader has {report.shaderErrors.Count} compile error(s) and {report.nodeErrors.Count} node error(s); see compile.");
                return ExitCode.Failed;
            }
            return ExitCode.Success;
        }
    }
}

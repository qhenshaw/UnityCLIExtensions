using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

namespace UnityCLIExtensions.ShaderGraph
{
    /// <summary>One edit, as given to <c>cliext.shadergraph edit --ops</c> (a JSON array of these).</summary>
    [Serializable]
    internal class ShaderGraphOp
    {
        /// <summary>add_node, add_property, add_block, remove, set, link, unlink, move, rename, expose.</summary>
        public string op;
        /// <summary>For add_node: the node (see cliext.shadergraph types). For add_property: its type (float, color, texture2d, ...).</summary>
        public string type;
        /// <summary>For add_block: the block's name (Base Color, Normal (Tangent Space), Alpha, ...).</summary>
        public string block;
        /// <summary>The node or property the op works on: an id, a property's name, or $name from an earlier "as".</summary>
        public string id;
        public string @as;
        /// <summary>For set: an input slot of a node (id, display name or shader name). Without it, set changes a property's default.</summary>
        public string slot;
        /// <summary>For set: a node's own setting (a Custom Function's functionBody, a texture type, ...; see inspect --id).</summary>
        public string setting;
        public string value;
        /// <summary>For link/unlink: &lt;node&gt;:&lt;slot&gt;. For link, from may also be a property (its name or id) to drag in a property node.</summary>
        public string from;
        public string to;
        public float x = float.NaN;
        public float y = float.NaN;
        /// <summary>For add_property and rename: the property's display name. For expose: true/false in value.</summary>
        public string name;
        /// <summary>For add_property: the reference name (_BaseMap).</summary>
        public string reference;
    }

    [Serializable]
    internal class ShaderGraphOpList
    {
        public ShaderGraphOp[] ops;
    }

    internal sealed class AddedItem
    {
        public string op;
        public string id;
        public string alias;
        public string type;
        public string name;
    }

    internal static class ShaderGraphOps
    {
        public static object Apply(ShaderGraphDocument doc, ShaderGraphOp op, Dictionary<string, string> aliases)
        {
            var graph = doc.graph;
            string Id(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException($"'{op.op}' needs an id.");
                id = id.Trim();
                if (!id.StartsWith("$")) return id;
                if (aliases.TryGetValue(id.Substring(1), out string real)) return real;
                throw new ArgumentException($"No earlier op added '{id}' (use \"as\").");
            }
            (AbstractMaterialNode node, MaterialSlot slot) Slot(string reference, bool output)
            {
                int colon = reference?.LastIndexOf(':') ?? -1;
                if (colon <= 0) throw new ArgumentException($"'{reference}' isn't a slot: use <node id>:<slot>.");
                var node = doc.FindNode(Id(reference.Substring(0, colon)));
                return (node, ShaderGraphDocument.FindSlot(node, reference.Substring(colon + 1), output));
            }
            Rect At(Vector2 fallback) => new Rect(float.IsNaN(op.x) ? fallback.x : op.x, float.IsNaN(op.y) ? fallback.y : op.y, 0, 0);

            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add_node":
                {
                    if (string.IsNullOrWhiteSpace(op.type)) throw new ArgumentException("add_node needs a type (see cliext.shadergraph types).");
                    var entry = ShaderGraphDocument.FindNodeType(op.type);
                    var node = (AbstractMaterialNode)Activator.CreateInstance(entry.type);
                    node.drawState = new DrawState { expanded = true, position = At(NextPosition(graph)) };
                    graph.AddNode(node);
                    if (!string.IsNullOrEmpty(op.@as)) aliases[op.@as] = node.objectId;
                    return new AddedItem { op = "add_node", id = node.objectId, alias = op.@as, type = entry.key, name = node.name };
                }
                case "add_property":
                {
                    var property = ShaderGraphDocument.NewProperty(op.type);
                    graph.AddGraphInput(property);
                    property.SetDisplayNameAndSanitizeForGraph(graph, string.IsNullOrWhiteSpace(op.name) ? property.displayName : op.name);
                    property.SetReferenceNameAndSanitizeForGraph(graph, string.IsNullOrWhiteSpace(op.reference) ? null : op.reference);
                    if (op.value != null) ShaderGraphDocument.WriteValue(property, op.value);
                    if (!string.IsNullOrEmpty(op.@as)) aliases[op.@as] = property.objectId;
                    return new AddedItem { op = "add_property", id = property.objectId, alias = op.@as, type = property.GetType().Name, name = property.displayName };
                }
                case "add_block":
                {
                    if (string.IsNullOrWhiteSpace(op.block)) throw new ArgumentException("add_block needs a block name (see cliext.shadergraph inspect, blocksAvailable).");
                    var descriptor = AvailableBlocks(graph).FirstOrDefault(d => Matches(d, op.block))
                                     ?? throw new ArgumentException($"No block '{op.block}' for this graph's targets. Available: {string.Join(", ", AvailableBlocks(graph).Select(d => d.displayName))}");
                    var context = descriptor.shaderStage == ShaderStage.Fragment ? graph.fragmentContext : graph.vertexContext;
                    var existing = context.blocks.Select(b => b.value).FirstOrDefault(b => b.descriptor == descriptor);
                    if (existing != null && !string.IsNullOrEmpty(op.@as)) aliases[op.@as] = existing.objectId;
                    if (existing != null) return new AddedItem { op = "add_block", id = existing.objectId, alias = op.@as, type = "BlockNode", name = descriptor.displayName };
                    var block = (BlockNode)Activator.CreateInstance(typeof(BlockNode));
                    block.Init(descriptor);
                    graph.AddBlock(block, context, context.blocks.Count);
                    if (!string.IsNullOrEmpty(op.@as)) aliases[op.@as] = block.objectId;
                    return new AddedItem { op = "add_block", id = block.objectId, alias = op.@as, type = "BlockNode", name = descriptor.displayName };
                }
                case "remove":
                {
                    string id = Id(op.id);
                    var node = graph.GetNodeFromId(id);
                    if (node != null)
                    {
                        if (!node.canDeleteNode && !(node is BlockNode)) throw new ArgumentException($"{node.name} can't be removed.");
                        graph.RemoveNode(node);
                        return new { op = "remove", id, removed = node.name };
                    }
                    if (!doc.TryFindProperty(id, out var property))
                        throw new ArgumentException($"No node or property '{op.id}'. See cliext.shadergraph inspect for the ids" +
                                                    (graph.properties.Any() ? $" (properties: {string.Join(", ", graph.properties.Select(x => x.displayName))})." : "."));
                    graph.RemoveGraphInput(property);
                    return new { op = "remove", id, removed = property.displayName };
                }
                case "set":
                {
                    if (op.value == null) throw new ArgumentException("set needs a value (a number, true/false, [x,y,z,w], #RRGGBB, a texture path or an enum name).");
                    if (!string.IsNullOrEmpty(op.setting))
                    {
                        var node = doc.FindNode(Id(op.id));
                        var member = Settings(node).FirstOrDefault(p => string.Equals(p.Name, op.setting, StringComparison.OrdinalIgnoreCase))
                                     ?? throw new ArgumentException($"{node.name} has no setting '{op.setting}'. It has: {string.Join(", ", Settings(node).Select(p => p.Name))}");
                        member.SetValue(node, ValueText.Parse(op.value, member.PropertyType));
                        node.Dirty(ModificationScope.Topological);
                        return new { op = "set", id = op.id, setting = member.Name, value = ValueText.Format(member.GetValue(node)) };
                    }
                    if (!string.IsNullOrEmpty(op.slot))
                    {
                        var node = doc.FindNode(Id(op.id));
                        var slot = ShaderGraphDocument.FindSlot(node, op.slot, output: false);
                        if (graph.GetEdges(slot.slotReference).Any()) throw new ArgumentException($"Slot '{slot.RawDisplayName()}' is linked; unlink it before setting a value.");
                        ShaderGraphDocument.WriteValue(slot, op.value);
                        node.Dirty(ModificationScope.Node);
                        return new { op = "set", id = op.id, slot = slot.RawDisplayName(), value = ValueText.Format(ShaderGraphDocument.ReadValue(slot)) };
                    }
                    var property = doc.FindProperty(Id(op.id));
                    ShaderGraphDocument.WriteValue(property, op.value);
                    return new { op = "set", id = op.id, property = property.displayName, value = ValueText.Format(ShaderGraphDocument.ReadValue(property)) };
                }
                case "link":
                {
                    var (toNode, toSlot) = Slot(op.to, output: false);
                    SlotReference fromRef;
                    int colon = op.from?.LastIndexOf(':') ?? -1;
                    if (colon <= 0)
                    {
                        // A property: drop a property node for it next to the target.
                        var property = doc.FindProperty(Id(op.from));
                        var propertyNode = new PropertyNode();
                        propertyNode.drawState = new DrawState { expanded = true, position = new Rect(toNode.drawState.position.x - 250, toNode.drawState.position.y, 0, 0) };
                        graph.AddNode(propertyNode);
                        propertyNode.property = property;
                        fromRef = propertyNode.GetOutputSlots<MaterialSlot>().First().slotReference;
                    }
                    else
                    {
                        var (_, fromSlot) = Slot(op.from, output: true);
                        fromRef = fromSlot.slotReference;
                    }
                    // GraphData.Connect doesn't check types (the window does, before it calls it).
                    var fromMaterialSlot = fromRef.slot;
                    if (fromMaterialSlot != null && !toSlot.IsCompatibleWith(fromMaterialSlot))
                        throw new ArgumentException($"Can't link {fromMaterialSlot.concreteValueType} '{fromMaterialSlot.RawDisplayName()}' to {toSlot.concreteValueType} '{toSlot.RawDisplayName()}'.");
                    var edge = graph.Connect(fromRef, toSlot.slotReference);
                    if (edge == null) throw new ArgumentException($"Shader Graph refused to link {op.from} to {op.to} (types don't fit, or it would make a loop).");
                    return new { op = "link", from = op.from, to = op.to, fromNode = fromRef.node.objectId };
                }
                case "unlink":
                {
                    var (_, toSlot) = Slot(op.to, output: false);
                    var edges = graph.GetEdges(toSlot.slotReference).ToList();
                    if (!string.IsNullOrWhiteSpace(op.from))
                    {
                        var (_, fromSlot) = Slot(op.from, output: true);
                        edges = edges.Where(e => e.outputSlot.Equals(fromSlot.slotReference)).ToList();
                    }
                    // Unlinking what isn't linked would save an unchanged graph: say so instead.
                    if (edges.Count == 0)
                        throw new ArgumentException($"No link into {op.to}{(string.IsNullOrWhiteSpace(op.from) ? "" : $" from {op.from}")}. See cliext.shadergraph inspect for the slot's links.");
                    foreach (var e in edges) graph.RemoveEdge(e);
                    return new { op = "unlink", to = op.to, removed = edges.Count };
                }
                case "add_port":
                case "remove_port":
                {
                    // Ports on nodes whose slots are user-defined, such as a Custom Function.
                    var node = doc.FindNode(Id(op.id));
                    if (!(node is CustomFunctionNode)) throw new ArgumentException($"{node.name} has fixed ports; add_port works on Custom Function nodes.");
                    if (op.op.Trim().Equals("remove_port", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isOutput = string.Equals(op.value, "out", StringComparison.OrdinalIgnoreCase) || string.Equals(op.value, "output", StringComparison.OrdinalIgnoreCase);
                        var slot = ShaderGraphDocument.FindSlot(node, op.name, isOutput);
                        foreach (var e in graph.GetEdges(slot.slotReference).ToList()) graph.RemoveEdge(e);
                        node.RemoveSlot(slot.id);
                        node.Dirty(ModificationScope.Topological);
                        return new { op = "remove_port", id = op.id, port = op.name };
                    }
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("add_port needs a name.");
                    bool output = string.Equals(op.value, "out", StringComparison.OrdinalIgnoreCase) || string.Equals(op.value, "output", StringComparison.OrdinalIgnoreCase);
                    var valueType = ParseSlotType(op.type);
                    var existing = new List<MaterialSlot>(); node.GetSlots(existing);
                    int id = existing.Count == 0 ? 0 : existing.Max(s => s.id) + 1;
                    var slotNew = MaterialSlot.CreateMaterialSlot(valueType, id, op.name, NodeUtils.GetHLSLSafeName(op.name), output ? SlotType.Output : SlotType.Input, Vector4.zero);
                    node.AddSlot(slotNew);
                    node.Dirty(ModificationScope.Topological);
                    return new { op = "add_port", id = op.id, port = op.name, slotId = id, direction = output ? "out" : "in", type = valueType.ToString() };
                }
                case "move":
                {
                    if (float.IsNaN(op.x) && float.IsNaN(op.y)) throw new ArgumentException("move needs x and/or y.");
                    var node = doc.FindNode(Id(op.id));
                    var ds = node.drawState;
                    ds.position = new Rect(float.IsNaN(op.x) ? ds.position.x : op.x, float.IsNaN(op.y) ? ds.position.y : op.y, ds.position.width, ds.position.height);
                    node.drawState = ds;
                    return new { op = "move", id = op.id, x = ds.position.x, y = ds.position.y };
                }
                case "rename":
                {
                    if (string.IsNullOrWhiteSpace(op.name) && string.IsNullOrWhiteSpace(op.reference)) throw new ArgumentException("rename needs a name and/or a reference.");
                    var property = doc.FindProperty(Id(op.id));
                    if (!string.IsNullOrWhiteSpace(op.name)) property.SetDisplayNameAndSanitizeForGraph(graph, op.name);
                    if (!string.IsNullOrWhiteSpace(op.reference)) property.SetReferenceNameAndSanitizeForGraph(graph, op.reference);
                    return new { op = "rename", id = op.id, name = property.displayName, reference = property.referenceName };
                }
                case "expose":
                {
                    var property = doc.FindProperty(Id(op.id));
                    property.generatePropertyBlock = string.IsNullOrEmpty(op.value) || ValueText.Parse(op.value, typeof(bool)) is true;
                    return new { op = "expose", id = op.id, exposed = property.generatePropertyBlock };
                }
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: add_node, add_property, add_block, add_port, remove_port, remove, set, link, unlink, move, rename, expose.");
            }
        }

        /// <summary>
        /// A node's own settings: the writable properties its type declares (not AbstractMaterialNode's), of a type
        /// text can set: strings (a Custom Function's body and name), enums (texture type, space, ...), numbers, bools.
        /// </summary>
        public static IEnumerable<System.Reflection.PropertyInfo> Settings(AbstractMaterialNode node)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var skip = new HashSet<string> { "name", "drawState", "owner", "previewExpanded", "precision", "group", "isActive", "canSetPrecision" };
            for (var t = node.GetType(); t != null && t != typeof(AbstractMaterialNode); t = t.BaseType)
                foreach (var p in t.GetProperties(flags | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || skip.Contains(p.Name)) continue;
                    var pt = p.PropertyType;
                    if (pt == typeof(string) || pt.IsEnum || pt == typeof(bool) || pt == typeof(int) || pt == typeof(float) || pt == typeof(Vector2) || pt == typeof(Vector3) || pt == typeof(Vector4) || pt == typeof(Color))
                        yield return p;
                }
        }

        private static SlotValueType ParseSlotType(string type)
        {
            switch ((type ?? "float").Trim().ToLowerInvariant())
            {
                case "float": case "vector1": return SlotValueType.Vector1;
                case "vector2": return SlotValueType.Vector2;
                case "vector3": return SlotValueType.Vector3;
                case "vector4": case "color": return SlotValueType.Vector4;
                case "bool": case "boolean": return SlotValueType.Boolean;
                case "texture2d": return SlotValueType.Texture2D;
                case "texture2darray": return SlotValueType.Texture2DArray;
                case "texture3d": return SlotValueType.Texture3D;
                case "cubemap": return SlotValueType.Cubemap;
                case "samplerstate": return SlotValueType.SamplerState;
                case "gradient": return SlotValueType.Gradient;
                case "matrix2": return SlotValueType.Matrix2;
                case "matrix3": return SlotValueType.Matrix3;
                case "matrix4": return SlotValueType.Matrix4;
                default: throw new ArgumentException($"Unknown port type '{type}': float, vector2, vector3, vector4, bool, texture2d, texture2darray, texture3d, cubemap, samplerstate, gradient, matrix2-4.");
            }
        }

        public static IEnumerable<BlockFieldDescriptor> AvailableBlocks(GraphData graph)
        {
            // Every block the graph's targets know, active or not (the ones the current settings use come first).
            var active = graph.GetActiveBlocksForAllActiveTargets();
            return active.Distinct();
        }

        private static bool Matches(BlockFieldDescriptor d, string name) =>
            string.Equals(d.displayName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{d.tag}.{d.name}", name, StringComparison.OrdinalIgnoreCase);

        private static Vector2 NextPosition(GraphData graph)
        {
            var nodes = graph.GetNodes<AbstractMaterialNode>().Where(n => !(n is BlockNode)).ToList();
            if (nodes.Count == 0) return new Vector2(-400, 0);
            return new Vector2(nodes.Min(n => n.drawState.position.x), nodes.Max(n => n.drawState.position.y) + 160);
        }

        // ---------------------------------------------------------------------------------------------------------

        public static object DescribeNode(GraphData graph, AbstractMaterialNode node, bool details)
        {
            var inputs = new List<MaterialSlot>(); node.GetInputSlots(inputs);
            var outputs = new List<MaterialSlot>(); node.GetOutputSlots(outputs);
            object Slot(MaterialSlot s, bool input)
            {
                var links = graph.GetEdges(s.slotReference)
                    .Select(e => input ? $"{e.outputSlot.node.objectId}:{e.outputSlot.slotId}" : $"{e.inputSlot.node.objectId}:{e.inputSlot.slotId}").ToArray();
                string value = null;
                if (input && links.Length == 0) { try { value = ValueText.Format(ShaderGraphDocument.ReadValue(s)); } catch { } }
                return new { id = s.id, name = s.RawDisplayName(), type = s.concreteValueType.ToString(), value, links = links.Length > 0 ? links : null };
            }
            return new
            {
                id = node.objectId,
                type = node is BlockNode ? "Block" : node.GetType().Name,
                name = node is BlockNode b ? b.descriptor?.displayName ?? node.name : node.name,
                property = node is PropertyNode pn ? pn.property?.displayName : null,
                position = new[] { node.drawState.position.x, node.drawState.position.y },
                hasError = node.hasError ? true : (bool?)null,
                settings = details && !(node is BlockNode) ? Settings(node).Select(p => new { name = p.Name, type = p.PropertyType.Name, value = SafeFormat(() => p.GetValue(node)) }).ToArray() : null,
                inputs = details ? inputs.Select(s => Slot(s, true)).ToArray() : null,
                outputs = details ? outputs.Select(s => Slot(s, false)).ToArray() : null
            };
        }

        private static string SafeFormat(Func<object> get)
        {
            try { string s = ValueText.Format(get()); return s != null && s.Length > 300 ? s.Substring(0, 300) + "…" : s; }
            catch (Exception e) { return $"<{e.GetType().Name}>"; }
        }

        public static object DescribeProperty(AbstractShaderProperty p)
        {
            object value = null;
            try { value = ValueText.Format(ShaderGraphDocument.ReadValue(p)); } catch { }
            return new { id = p.objectId, name = p.displayName, reference = p.referenceName, type = p.propertyType.ToString(), value, exposed = p.generatePropertyBlock };
        }
    }
}

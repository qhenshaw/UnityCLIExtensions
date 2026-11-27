using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;

namespace UnityCLIExtensions.VFX
{
    /// <summary>One edit, as given to <c>cliext.vfx edit --ops</c> (a JSON array of these).</summary>
    [Serializable]
    internal class VfxOp
    {
        /// <summary>add, remove, set, link, unlink, move, rename, expose.</summary>
        public string op;
        /// <summary>For add: context, block, operator or parameter.</summary>
        public string kind;
        /// <summary>For add: the library entry (category/name, name or class name; see cliext.vfx types).</summary>
        public string type;
        /// <summary>The model the op works on: an id, or $name of a model an earlier op added with "as".</summary>
        public string id;
        /// <summary>For add of a block: the context it goes in.</summary>
        public string context;
        /// <summary>For add of a block: where in the context (-1: at the end).</summary>
        public int index = -1;
        /// <summary>For add: a name later ops use as $name.</summary>
        public string @as;
        /// <summary>For set: a setting's name.</summary>
        public string setting;
        /// <summary>For set: an input slot's name or path.</summary>
        public string slot;
        /// <summary>For set: the value, as text (see VfxGraphDocument.Parse).</summary>
        public string value;
        /// <summary>For set of a slot: Local, World or None.</summary>
        public string space;
        /// <summary>For link/unlink: a context id (flow) or model:slot (data).</summary>
        public string from;
        public string to;
        /// <summary>For link/unlink of contexts: flow slot indexes (spawner start/stop, GPU event outputs, ...).</summary>
        public int fromIndex;
        public int toIndex;
        /// <summary>For add/move: the node's position in the graph view.</summary>
        public float x = float.NaN;
        public float y = float.NaN;
        /// <summary>For rename: the context label or parameter name. For expose: true/false.</summary>
        public string name;
    }

    [Serializable]
    internal class VfxOpList
    {
        public VfxOp[] ops;
    }

    /// <summary>What an add returns; its id is known only once the graph is saved.</summary>
    internal sealed class AddResult
    {
        public VFXModel model;
        public string entry;
        public string alias;
    }

    /// <summary>Applies <see cref="VfxOp"/>s to a graph and describes its models.</summary>
    internal static class VfxGraphOps
    {
        /// <summary>Applies ops in order, sharing their "as" names, without saving. Throws on the first op that fails.</summary>
        public static void ApplyAll(VfxGraphDocument doc, IEnumerable<VfxOp> ops)
        {
            var aliases = new Dictionary<string, VFXModel>();
            foreach (VfxOp op in ops)
                Apply(doc, op, aliases);
        }

        /// <summary>
        /// Applies ops in order, all or nothing, without saving. On the first op that fails the graph is put back as it
        /// was, in memory and on disk, and <paramref name="failedAt"/> and <paramref name="error"/> say which op and why.
        /// </summary>
        public static bool TryApplyAll(VfxGraphDocument doc, IReadOnlyList<VfxOp> ops, List<object> results, out int failedAt, out string error)
        {
            failedAt = -1;
            error = null;
            using var rollback = AssetRollback.Begin(new[] { doc.path });
            var aliases = new Dictionary<string, VFXModel>();
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(Apply(doc, ops[i], aliases)); }
                catch (Exception e)
                {
                    failedAt = i;
                    error = e is ArgumentException ? e.Message : e.ToString();
                    results.Clear();
                    rollback.Rollback();
                    // A structure change (remove, or a setting that rebuilds slots) re-registers the graph's sub-assets
                    // in memory at once, so a removed model stops being part of the asset and unloading can't bring it
                    // back: the next save would drop it. Forget the in-memory resource, as the VFX Graph window's
                    // Discard Changes does, so the next access loads the graph from the file the rollback restored.
                    VisualEffectResource.ForgetAtPath(doc.path);
                    return false;
                }
            }
            return true;
        }

        public static object Apply(VfxGraphDocument doc, VfxOp op, Dictionary<string, VFXModel> aliases)
        {
            VFXModel Resolve(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException($"'{op.op}' needs a model id.");
                id = id.Trim();
                if (id.StartsWith("$"))
                {
                    if (aliases.TryGetValue(id.Substring(1), out VFXModel m)) return m;
                    throw new ArgumentException($"No earlier op added '{id}' (use \"as\": \"{id.Substring(1)}\").");
                }
                return doc.Find(id);
            }

            VFXSlot ResolveSlot(string reference, bool output, out VFXModel owner)
            {
                int colon = reference?.IndexOf(':') ?? -1;
                if (colon <= 0) throw new ArgumentException($"'{reference}' isn't a slot: use <model id>:<slot name>.");
                owner = Resolve(reference.Substring(0, colon));
                return VfxGraphDocument.FindSlot(owner, reference.Substring(colon + 1), output);
            }

            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add":
                {
                    if (string.IsNullOrWhiteSpace(op.kind)) throw new ArgumentException("add needs a kind: context, block, operator or parameter.");
                    if (!VfxGraphDocument.IsKind(op.kind)) throw new ArgumentException($"Unknown kind '{op.kind}': use context, block, operator or parameter.");
                    if (string.IsNullOrWhiteSpace(op.type)) throw new ArgumentException("add needs a type (see cliext.vfx types).");
                    var entry = VfxGraphDocument.FindInLibrary(op.kind, op.type);
                    VFXModel model = entry.descriptor.CreateInstance();
                    if (model is VFXBlock block)
                    {
                        if (string.IsNullOrWhiteSpace(op.context))
                            throw new ArgumentException($"{entry.Key} is a block, and a block goes in a context: pass the context's id (--context, or \"context\" in an op; see cliext.vfx inspect).");
                        if (!(Resolve(op.context) is VFXContext context)) throw new ArgumentException("A block goes in a context: pass its id as context.");
                        if (!context.AcceptChild(block, op.index))
                            throw new ArgumentException($"{entry.Key} can't go in a {context.contextType} context (it fits {block.compatibleContexts}).");
                        context.AddChild(block, op.index);
                    }
                    else
                    {
                        doc.graph.AddChild(model);
                        if (!float.IsNaN(op.x) || !float.IsNaN(op.y))
                            model.position = new Vector2(float.IsNaN(op.x) ? 0 : op.x, float.IsNaN(op.y) ? 0 : op.y);
                        else
                            model.position = NextPosition(doc, model);
                    }
                    if (!string.IsNullOrEmpty(op.@as)) aliases[op.@as] = model;
                    return new AddResult { model = model, entry = entry.Key, alias = op.@as };
                }
                case "remove":
                {
                    VFXModel model = Resolve(op.id);
                    Remove(model);
                    return new { op = "remove", id = op.id, type = model.GetType().Name };
                }
                case "set":
                {
                    VFXModel model = Resolve(op.id);
                    if (!string.IsNullOrEmpty(op.setting))
                    {
                        VFXSetting setting = model.GetSetting(op.setting);
                        if (!setting.valid)
                        {
                            // Some settings are private fields with an m_ prefix.
                            var alt = model.GetSettings(true).FirstOrDefault(s => string.Equals(s.name, op.setting, StringComparison.OrdinalIgnoreCase) ||
                                                                                 string.Equals(s.name, "m_" + op.setting, StringComparison.OrdinalIgnoreCase));
                            if (!alt.valid)
                                throw new ArgumentException($"{model.GetType().Name} has no setting '{op.setting}'. It has: " +
                                                            string.Join(", ", model.GetSettings(false, VFXSettingAttribute.VisibleFlags.InInspector).Select(s => s.name)));
                            setting = alt;
                        }
                        if (op.value == null) throw new ArgumentException($"set of setting '{setting.name}' needs a value (--value, or \"value\" in an op).");
                        object parsed = VfxGraphDocument.Parse(op.value, setting.field.FieldType, setting.value);
                        model.SetSettingValue(setting.name, parsed);
                        return new { op = "set", id = op.id, setting = setting.name, value = VfxGraphDocument.Format(model.GetSetting(setting.name).value) };
                    }
                    if (model is VFXParameter parameter && (string.IsNullOrEmpty(op.slot) || op.slot.Equals("value", StringComparison.OrdinalIgnoreCase)))
                    {
                        // A parameter's default value (what the VisualEffect uses unless it overrides it).
                        if (op.value == null) throw new ArgumentException("set on a parameter needs a value.");
                        parameter.value = VfxGraphDocument.Parse(op.value, parameter.type, parameter.value);
                        return new { op = "set", id = op.id, value = VfxGraphDocument.Format(parameter.value) };
                    }
                    if (!string.IsNullOrEmpty(op.slot))
                    {
                        VFXSlot slot = VfxGraphDocument.FindSlot(model, op.slot, output: false);
                        if (op.value == null && string.IsNullOrEmpty(op.space))
                            throw new ArgumentException($"set of slot '{slot.path}' needs a value (--value, or \"value\" in an op), or a space (--space Local|World).");
                        if (!string.IsNullOrEmpty(op.space))
                        {
                            if (!slot.spaceable) throw new ArgumentException($"Slot '{slot.name}' has no space.");
                            // VFXSpace is internal to the engine's VFX module, so set it by reflection.
                            var spaceProperty = typeof(VFXSlot).GetProperty("space");
                            spaceProperty.SetValue(slot, Enum.Parse(spaceProperty.PropertyType, op.space, true));
                        }
                        if (op.value != null)
                        {
                            if (slot.HasLink()) throw new ArgumentException($"Slot '{slot.path}' is linked; unlink it before setting a value.");
                            slot.value = VfxGraphDocument.Parse(op.value, slot.property.type, slot.value);
                        }
                        return new { op = "set", id = op.id, slot = slot.path, value = VfxGraphDocument.Format(slot.value), space = slot.spaceable ? slot.space.ToString() : null };
                    }
                    throw new ArgumentException("set needs a setting or a slot.");
                }
                case "link":
                case "unlink":
                {
                    bool link = op.op.Trim().Equals("link", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(op.from) || string.IsNullOrWhiteSpace(op.to)) throw new ArgumentException($"{op.op} needs from and to.");
                    if (!op.from.Contains(":") && !op.to.Contains(":"))
                    {
                        if (!(Resolve(op.from) is VFXContext from) || !(Resolve(op.to) is VFXContext to))
                            throw new ArgumentException("A flow link joins two contexts; for data, use <model id>:<slot>.");
                        if (link)
                        {
                            if (!VFXContext.CanLink(from, to, op.fromIndex, op.toIndex))
                                throw new ArgumentException($"A {from.contextType} context can't flow into a {to.contextType} context there (slots {op.fromIndex} -> {op.toIndex}).");
                            from.LinkTo(to, op.fromIndex, op.toIndex);
                        }
                        else
                        {
                            bool linked = op.fromIndex >= 0 && op.fromIndex < from.outputFlowSlot.Length &&
                                          from.outputFlowSlot[op.fromIndex].link.Any(l => l.context == to && l.slotIndex == op.toIndex);
                            if (!linked)
                                throw new ArgumentException($"There's no flow link from {op.from} (output {op.fromIndex}) to {op.to} (input {op.toIndex}). " +
                                                            $"See flowTo in cliext.vfx inspect --id {op.from}.");
                            from.UnlinkTo(to, op.fromIndex, op.toIndex);
                        }
                        return new { op = op.op, flow = true, from = op.from, to = op.to };
                    }
                    VFXSlot output = ResolveSlot(op.from, true, out _);
                    VFXSlot input = ResolveSlot(op.to, false, out _);
                    if (link)
                    {
                        if (!input.CanLink(output) && !output.CanLink(input))
                            throw new ArgumentException($"Can't link {output.property.type.Name} '{output.path}' to {input.property.type.Name} '{input.path}'.");
                        if (!output.Link(input)) throw new ArgumentException($"VFX Graph refused to link '{output.path}' to '{input.path}'.");
                    }
                    else
                    {
                        if (!input.LinkedSlots.Contains(output))
                            throw new ArgumentException($"'{op.from}' isn't linked to '{op.to}'. See the input's links in cliext.vfx inspect.");
                        output.Unlink(input);
                    }
                    return new { op = op.op, flow = false, from = op.from, to = op.to };
                }
                case "move":
                {
                    VFXModel model = Resolve(op.id);
                    model.position = new Vector2(float.IsNaN(op.x) ? model.position.x : op.x, float.IsNaN(op.y) ? model.position.y : op.y);
                    return new { op = "move", id = op.id, x = model.position.x, y = model.position.y };
                }
                case "rename":
                {
                    VFXModel model = Resolve(op.id);
                    switch (model)
                    {
                        case VFXContext c: c.label = op.name ?? ""; break;
                        case VFXParameter p: p.SetSettingValue("m_ExposedName", op.name ?? ""); break;
                        default: throw new ArgumentException("Only contexts (label) and parameters (name) can be renamed.");
                    }
                    return new { op = "rename", id = op.id, name = op.name };
                }
                case "expose":
                {
                    if (!(Resolve(op.id) is VFXParameter p)) throw new ArgumentException("expose works on a parameter.");
                    bool exposed = string.IsNullOrEmpty(op.value) || VfxGraphDocument.Parse(op.value, typeof(bool)) is true;
                    p.SetSettingValue("m_Exposed", exposed);
                    return new { op = "expose", id = op.id, exposed };
                }
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: add, remove, set, link, unlink, move, rename, expose.");
            }
        }

        public static void Remove(VFXModel model)
        {
            if (model is IVFXSlotContainer container)
            {
                foreach (var s in container.inputSlots.ToList()) s.UnlinkAll(true);
                foreach (var s in container.outputSlots.ToList()) s.UnlinkAll(true);
            }
            if (model is VFXContext context)
            {
                foreach (VFXBlock b in context.children.ToList())
                    foreach (var s in ((IVFXSlotContainer)b).inputSlots.ToList()) s.UnlinkAll(true);
                for (int i = 0; i < context.inputFlowSlot.Length; i++)
                    foreach (var l in context.inputFlowSlot[i].link.ToList()) l.context.UnlinkTo(context, l.slotIndex, i);
                for (int i = 0; i < context.outputFlowSlot.Length; i++)
                    foreach (var l in context.outputFlowSlot[i].link.ToList()) context.UnlinkTo(l.context, i, l.slotIndex);
            }
            model.Detach();
        }

        private static Vector2 NextPosition(VfxGraphDocument doc, VFXModel added)
        {
            var others = doc.graph.children.Where(m => m != added).ToList();
            if (added is VFXContext)
            {
                var contexts = others.OfType<VFXContext>().ToList();
                // Systems read top to bottom: put a new context under the lowest one.
                return contexts.Count == 0 ? Vector2.zero : new Vector2(contexts.OrderByDescending(c => c.position.y).First().position.x, contexts.Max(c => c.position.y) + 350);
            }
            var nodes = others.Where(m => !(m is VFXContext)).ToList();
            return nodes.Count == 0 ? new Vector2(-400, 0) : new Vector2(-400, nodes.Max(n => n.position.y) + 150);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Describing

        public static object Describe(VFXModel model, bool details = true)
        {
            string id = VfxGraphDocument.IdOf(model);
            var container = model as IVFXSlotContainer;
            object settings = details
                ? model.GetSettings(false, VFXSettingAttribute.VisibleFlags.InInspector).Select(s => new { name = s.name, type = s.field.FieldType.Name, value = SafeFormat(() => s.value) }).ToArray()
                : null;
            object inputs = details && container != null ? container.inputSlots.Select(s => DescribeSlot(s)).ToArray() : null;
            object outputs = details && container != null && !(model is VFXContext) && !(model is VFXBlock)
                ? container.outputSlots.Select(s => DescribeSlot(s)).ToArray() : null;

            switch (model)
            {
                case VFXContext c:
                    return new
                    {
                        id, kind = "context", type = c.GetType().Name, contextType = c.contextType.ToString(), label = c.label, position = Pos(c),
                        flowFrom = c.inputFlowSlot.SelectMany((f, i) => f.link.Select(l => new { id = VfxGraphDocument.IdOf(l.context), fromIndex = l.slotIndex, toIndex = i })).ToArray(),
                        flowTo = c.outputFlowSlot.SelectMany((f, i) => f.link.Select(l => new { id = VfxGraphDocument.IdOf(l.context), fromIndex = i, toIndex = l.slotIndex })).ToArray(),
                        settings, inputs,
                        blocks = c.children.Select(b => Describe(b, details)).ToArray()
                    };
                case VFXParameter p:
                    return new
                    {
                        id, kind = "parameter", type = p.type?.Name, name = p.exposedName, exposed = p.exposed, position = Pos(p),
                        value = SafeFormat(() => p.value), outputs
                    };
                case VFXBlock b:
                    return new { id, kind = "block", type = b.GetType().Name, name = VfxGraphDocument.Clean(b.name), enabled = b.enabled, settings, inputs };
                default:
                    return new { id, kind = VfxGraphDocument.KindOf(model), type = model.GetType().Name, name = VfxGraphDocument.Clean(model.name), position = Pos(model), settings, inputs, outputs };
            }
        }

        private static object DescribeSlot(VFXSlot slot)
        {
            var links = slot.LinkedSlots.Select(l => $"{VfxGraphDocument.IdOf(l.owner as VFXModel)}:{l.path}").ToList();
            // A link to a sub-field (position.x) is on the child slot.
            foreach (VFXSlot child in AllChildren(slot))
                foreach (var l in child.LinkedSlots)
                    links.Add($"{child.path} <- {VfxGraphDocument.IdOf(l.owner as VFXModel)}:{l.path}");
            return new
            {
                name = slot.name,
                type = slot.property.type?.Name,
                value = slot.HasLink() ? null : SafeFormat(() => slot.value),
                space = slot.spaceable ? slot.space.ToString() : null,
                links = links.Count > 0 ? links.ToArray() : null
            };
        }

        private static IEnumerable<VFXSlot> AllChildren(VFXSlot slot)
        {
            foreach (VFXSlot c in slot.children)
            {
                yield return c;
                foreach (var cc in AllChildren(c)) yield return cc;
            }
        }

        private static float[] Pos(VFXModel m) => new[] { m.position.x, m.position.y };

        private static string SafeFormat(Func<object> get)
        {
            try { return VfxGraphDocument.Format(get()); }
            catch (Exception e) { return $"<{e.GetType().Name}>"; }
        }
    }
}

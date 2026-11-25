using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;

namespace UnityCLIExtensions.ShaderGraph
{
    /// <summary>
    /// Reads and edits a Shader Graph (.shadergraph or .shadersubgraph) through Shader Graph's editor model, which has
    /// no public authoring API. This assembly is named so that Shader Graph's <c>InternalsVisibleTo</c> lets it in.
    /// Nodes and properties are addressed by their object id (stable, saved in the file); a slot is
    /// <c>&lt;node id&gt;:&lt;slot&gt;</c>, by slot id, display name or shader output name.
    /// </summary>
    internal sealed class ShaderGraphDocument
    {
        public readonly string path;
        public readonly GraphData graph;

        private ShaderGraphDocument(string path, GraphData graph)
        {
            this.path = path;
            this.graph = graph;
        }

        public static ShaderGraphDocument Open(string path)
        {
            path = Normalize(path);
            if (!File.Exists(path)) throw new ArgumentException($"No Shader Graph at '{path}'.");
            string guid = AssetDatabase.AssetPathToGUID(path);
            var graph = new GraphData
            {
                assetGuid = guid,
                isSubGraph = path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase),
                messageManager = new MessageManager()
            };
            MultiJson.Deserialize(graph, File.ReadAllText(path, Encoding.UTF8));
            graph.OnEnable();
            graph.ValidateGraph();
            foreach (var t in graph.activeTargets) LoadSubTargetData(t);
            return new ShaderGraphDocument(path, graph);
        }

        private static string Normalize(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            if (path.Length == 32 && path.All(Uri.IsHexDigit)) path = AssetDatabase.GUIDToAssetPath(path);
            return path;
        }

        /// <summary>
        /// Creates a graph for a render pipeline target ("HDRP", "Universal", "Built-In", ...; see cliext.shadergraph
        /// targets) and, optionally, a material type (its sub-target, such as Lit or Unlit), with the target's default
        /// blocks, or copies <paramref name="from"/>.
        /// </summary>
        public static ShaderGraphDocument Create(string path, string target, string subTarget, string from)
        {
            path = Normalize(path);
            if (!path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)) path += ".shadergraph";
            if (File.Exists(path)) throw new ArgumentException($"'{path}' already exists.");
            string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder)) CreateFolders(folder);

            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!AssetDatabase.CopyAsset(Normalize(from), path)) throw new ArgumentException($"Couldn't copy '{from}' to '{path}'.");
                return Open(path);
            }

            var graph = new GraphData();
            graph.AddContexts();
            Target chosen = null;
            if (!string.IsNullOrWhiteSpace(target))
            {
                chosen = FindTarget(graph, target);
                if (!string.IsNullOrWhiteSpace(subTarget))
                {
                    var subs = SubTargetsOf(chosen);
                    var sub = subs.FirstOrDefault(s => string.Equals(s.displayName, subTarget, StringComparison.OrdinalIgnoreCase))
                              ?? subs.FirstOrDefault(s => s.displayName.IndexOf(subTarget, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (sub == null) throw new ArgumentException($"{chosen.displayName} has no material '{subTarget}'. It has: {string.Join(", ", subs.Select(s => s.displayName))}");
                    WireSubTarget(chosen, sub);
                }
            }
            graph.InitializeOutputs(chosen != null ? new[] { chosen } : null, null);
            // The target's own default blocks (Base Color, Normal, ... for Lit), as the Create menu adds them.
            foreach (var descriptor in graph.GetActiveBlocksForAllActiveTargets())
            {
                var context = descriptor.shaderStage == ShaderStage.Fragment ? graph.fragmentContext : graph.vertexContext;
                if (context.blocks.Any(b => b.value.descriptor == descriptor)) continue;
                var block = (BlockNode)Activator.CreateInstance(typeof(BlockNode));
                block.Init(descriptor);
                graph.AddBlock(block, context, context.blocks.Count);
            }
            graph.AddCategory(CategoryData.DefaultCategory());
            graph.path = "Shader Graphs";
            FileUtilities.WriteShaderGraphToDisk(path, graph);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Open(path);
        }

        /// <summary>The material types (sub-targets) of a target. TargetUtils.GetSubTargets matches on its type argument, so call it with the concrete type.</summary>
        public static List<SubTarget> SubTargetsOf(Target target)
        {
            var method = typeof(TargetUtils).GetMethod("GetSubTargets", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return (List<SubTarget>)method.MakeGenericMethod(target.GetType()).Invoke(null, new object[] { target });
        }

        /// <summary>
        /// Makes <paramref name="sub"/> the target's material type through the target's own TrySetActiveSubTarget
        /// (HDRP and URP have one), which also creates the data the sub-target reads (HDRP Lit's litData, ...);
        /// the window does this when it opens a graph, and a graph loaded without it throws on its blocks.
        /// </summary>
        public static void WireSubTarget(Target target, SubTarget sub)
        {
            if (target == null || sub == null) return;
            var trySet = target.GetType().GetMethod("TrySetActiveSubTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Type) }, null);
            if (trySet != null) trySet.Invoke(target, new object[] { sub.GetType() });
            else target.activeSubTarget = sub;
        }

        /// <summary>
        /// Gives a loaded graph's material type the data objects it reads, as the target does in its own Setup
        /// (ProcessSubTargetDatas, on HDRP and URP targets), without replacing the loaded sub-target.
        /// </summary>
        public static void LoadSubTargetData(Target target)
        {
            var sub = target?.activeSubTarget;
            if (sub == null) return;
            sub.target = target;
            var process = target.GetType().GetMethod("ProcessSubTargetDatas", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(SubTarget) }, null);
            process?.Invoke(target, new object[] { sub });
        }

        public static Target FindTarget(GraphData graph, string name)
        {
            var all = graph.allPotentialTargets.ToList();
            return all.FirstOrDefault(t => string.Equals(t.displayName, name, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(t => t.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? throw new ArgumentException($"No target '{name}'. Targets: {string.Join(", ", all.Select(t => t.displayName))}");
        }

        private static void CreateFolders(string folder)
        {
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || !(parent == "Assets" || parent.StartsWith("Assets/"))) throw new ArgumentException($"'{folder}' isn't under Assets/.");
            if (!AssetDatabase.IsValidFolder(parent)) CreateFolders(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>
        /// Writes the graph, reimports it, and returns the shader's compile errors and warnings and the nodes'
        /// own errors. Refuses when a Shader Graph window has this graph open (its copy would overwrite the edit).
        /// </summary>
        public CompileReport Save()
        {
            graph.ValidateGraph();
            FileUtilities.WriteShaderGraphToDisk(path, graph);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Compile(path, graph);
        }

        [Serializable]
        public sealed class CompileReport
        {
            public bool hasErrors;
            public List<string> shaderErrors = new List<string>();
            public List<string> shaderWarnings = new List<string>();
            public List<string> nodeErrors = new List<string>();
        }

        public static CompileReport Compile(string path, GraphData graph)
        {
            var report = new CompileReport();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null)
            {
                // HLSL errors (a Custom Function's body, ...) only appear once a pass is compiled, which the Editor
                // otherwise does later, in the background. Compile each pass now, for the current platform.
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    for (int pass = 0; pass < material.passCount; pass++) ShaderUtil.CompilePass(material, pass, true);
                }
                catch (Exception e) { report.shaderWarnings.Add($"Couldn't compile the passes: {e.Message}"); }
                finally { UnityEngine.Object.DestroyImmediate(material); }
                // The same error repeats once per pass (at a different line of the generated shader): report it once.
                foreach (var group in ShaderUtil.GetShaderMessages(shader).GroupBy(m => (m.message, m.severity, m.platform)))
                {
                    var first = group.First();
                    int passes = group.Count();
                    string line = $"{first.message} [{first.platform}]" + (string.IsNullOrEmpty(first.file) ? "" : $" (generated line {first.line}{(passes > 1 ? $", in {passes} passes" : "")})");
                    if (first.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) report.shaderErrors.Add(line); else report.shaderWarnings.Add(line);
                }
                report.shaderWarnings = report.shaderWarnings.Take(20).ToList();
            }
            if (graph?.messageManager != null)
                foreach (var kv in graph.messageManager.GetNodeMessages())
                    foreach (var m in kv.Value)
                        report.nodeErrors.Add($"{kv.Key}: {m.message}");
            foreach (var node in graph?.GetNodes<AbstractMaterialNode>() ?? Enumerable.Empty<AbstractMaterialNode>())
                if (node.hasError && !report.nodeErrors.Any(e => e.StartsWith(node.objectId)))
                    report.nodeErrors.Add($"{node.objectId}: {node.name} has an error");
            report.hasErrors = report.shaderErrors.Count > 0 || report.nodeErrors.Count > 0;
            return report;
        }

        /// <summary>True when an open Shader Graph window is editing this asset.</summary>
        public static bool IsOpenInWindow(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(Normalize(path));
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Name != "MaterialGraphEditWindow") continue;
                var prop = window.GetType().GetProperty("selectedGuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop?.GetValue(window) as string == guid) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Lookups

        public AbstractMaterialNode FindNode(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("No node id given.");
            var node = graph.GetNodeFromId(id.Trim());
            if (node == null) throw new ArgumentException($"No node with id '{id}'. See cliext.shadergraph inspect.");
            return node;
        }

        public AbstractShaderProperty FindProperty(string idOrName)
        {
            string s = (idOrName ?? "").Trim();
            var p = graph.properties.FirstOrDefault(x => x.objectId == s)
                    ?? graph.properties.FirstOrDefault(x => string.Equals(x.referenceName, s, StringComparison.Ordinal))
                    ?? graph.properties.FirstOrDefault(x => string.Equals(x.displayName, s, StringComparison.OrdinalIgnoreCase));
            if (p == null) throw new ArgumentException($"No property '{idOrName}'. Properties: {string.Join(", ", graph.properties.Select(x => x.displayName))}");
            return p;
        }

        public static MaterialSlot FindSlot(AbstractMaterialNode node, string slot, bool output)
        {
            var slots = new List<MaterialSlot>();
            if (output) node.GetOutputSlots(slots); else node.GetInputSlots(slots);
            string s = (slot ?? "").Trim();
            MaterialSlot found = int.TryParse(s, out int id) ? slots.FirstOrDefault(x => x.id == id) : null;
            found ??= slots.FirstOrDefault(x => string.Equals(x.RawDisplayName(), s, StringComparison.OrdinalIgnoreCase))
                      ?? slots.FirstOrDefault(x => string.Equals(x.shaderOutputName, s, StringComparison.OrdinalIgnoreCase))
                      ?? slots.FirstOrDefault(x => string.Equals(x.displayName, s, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new ArgumentException($"{node.name} has no {(output ? "output" : "input")} slot '{slot}'. It has: " +
                                            string.Join(", ", slots.Select(x => $"{x.id} {x.RawDisplayName()}")));
            return found;
        }

        // ---------------------------------------------------------------------------------------------------------
        // The node library

        internal sealed class NodeEntry
        {
            public string key;
            public string name;
            public Type type;
        }

        public static IEnumerable<NodeEntry> NodeTypes()
        {
            foreach (var type in NodeClassCache.knownNodeTypes)
            {
                if (type.IsAbstract) continue;
                var title = NodeClassCache.GetAttributeOnNodeType<TitleAttribute>(type);
                if (title?.title == null || title.title.Length == 0) continue;
                yield return new NodeEntry { key = string.Join("/", title.title), name = title.title.Last(), type = type };
            }
        }

        public static NodeEntry FindNodeType(string name)
        {
            string t = name.Trim();
            var all = NodeTypes().ToList();
            var m = all.Where(e => string.Equals(e.key, t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.Count == 0) m = all.Where(e => string.Equals(e.name, t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.Count == 0) m = all.Where(e => string.Equals(e.type.Name, t, StringComparison.OrdinalIgnoreCase) || string.Equals(e.type.Name, t + "Node", StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.Count == 1) return m[0];
            if (m.Count > 1) throw new ArgumentException($"'{name}' matches {m.Count} nodes: {string.Join(", ", m.Take(10).Select(e => e.key))}. Pass the full path.");
            var close = all.Where(e => e.key.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).Take(10).Select(e => e.key).ToList();
            throw new ArgumentException($"No node called '{name}'." + (close.Count > 0 ? " Did you mean: " + string.Join(", ", close) : " See cliext.shadergraph types."));
        }

        /// <summary>Property types by name: float, vector2-4, color, texture2d, texture2darray, texture3d, cubemap, boolean, matrix2-4, gradient, samplerstate.</summary>
        public static AbstractShaderProperty NewProperty(string type)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "float": case "vector1": return new Vector1ShaderProperty();
                case "vector2": return new Vector2ShaderProperty();
                case "vector3": return new Vector3ShaderProperty();
                case "vector4": return new Vector4ShaderProperty();
                case "color": return new ColorShaderProperty();
                case "texture2d": case "texture": return new Texture2DShaderProperty();
                case "texture2darray": return new Texture2DArrayShaderProperty();
                case "texture3d": return new Texture3DShaderProperty();
                case "cubemap": return new CubemapShaderProperty();
                case "boolean": case "bool": return new BooleanShaderProperty();
                case "matrix2": return new Matrix2ShaderProperty();
                case "matrix3": return new Matrix3ShaderProperty();
                case "matrix4": return new Matrix4ShaderProperty();
                case "gradient": return new GradientShaderProperty();
                case "samplerstate": return new SamplerStateShaderProperty();
                default: throw new ArgumentException($"Unknown property type '{type}': float, vector2, vector3, vector4, color, texture2d, texture2darray, texture3d, cubemap, boolean, matrix2-4, gradient, samplerstate.");
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Values on slots and properties

        /// <summary>The settable value member of a slot or property: value, texture, cubemap, ...</summary>
        public static PropertyInfo ValueMember(object target)
        {
            var type = target.GetType();
            foreach (string name in new[] { "value", "texture", "textureArray", "cubemap" })
            {
                var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.CanRead) return p;
            }
            return null;
        }

        public static object ReadValue(object target)
        {
            var member = ValueMember(target);
            if (member == null) return null;
            object v = member.GetValue(target);
            return v is SerializableTexture st ? st.texture : v is SerializableCubemap sc ? sc.cubemap : v is SerializableTextureArray sa ? sa.textureArray : v;
        }

        public static void WriteValue(object target, string text)
        {
            var member = ValueMember(target) ?? throw new ArgumentException($"{target.GetType().Name} has no value to set.");
            Type t = member.PropertyType;
            object parsed;
            if (t == typeof(SerializableTexture)) parsed = new SerializableTexture { texture = (Texture)ValueText.Parse(text, typeof(Texture)) };
            else if (t == typeof(SerializableCubemap)) parsed = new SerializableCubemap { cubemap = (Cubemap)ValueText.Parse(text, typeof(Cubemap)) };
            else if (t == typeof(SerializableTextureArray)) parsed = new SerializableTextureArray { textureArray = (Texture2DArray)ValueText.Parse(text, typeof(Texture2DArray)) };
            else parsed = ValueText.Parse(text, t);
            member.SetValue(target, parsed);
        }
    }
}

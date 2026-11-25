using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace UnityCLIExtensions.VFX
{
    /// <summary>
    /// Reads and edits a VFX Graph (.vfx or .vfxoperator / .vfxblock subgraph) through VFX Graph's editor model, which
    /// has no public authoring API. This assembly is named so that VFX Graph's <c>InternalsVisibleTo</c> lets it in.
    /// </summary>
    /// <remarks>
    /// Models are addressed by an id: their local file id inside the asset (stable across reloads), or
    /// <c>~&lt;instance id&gt;</c> before the asset has been saved. A slot is <c>&lt;model id&gt;:&lt;slot&gt;</c>, where
    /// the slot is its name (a leading underscore is optional), a dotted path into its fields (<c>position.x</c>), or
    /// <c>#index</c>.
    /// </remarks>
    internal sealed class VfxGraphDocument
    {
        public readonly string path;
        public readonly VisualEffectResource resource;
        public readonly VFXGraph graph;

        private VfxGraphDocument(string path, VisualEffectResource resource)
        {
            this.path = path;
            this.resource = resource;
            graph = resource.GetOrCreateGraph();
        }

        public static VfxGraphDocument Open(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            if (path.Length == 32 && path.All(Uri.IsHexDigit)) path = AssetDatabase.GUIDToAssetPath(path);
            var resource = VisualEffectResource.GetResourceAtPath(path);
            if (resource == null) throw new ArgumentException($"No VFX Graph at '{path}'. Create one with cliext.vfx create.");
            return new VfxGraphDocument(path, resource);
        }

        /// <summary>Creates an empty .vfx (no systems) at <paramref name="path"/>.</summary>
        public static VfxGraphDocument Create(string path)
        {
            path = path.Trim().Replace('\\', '/');
            if (!path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase)) path += ".vfx";
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) throw new ArgumentException($"'{path}' already exists.");
            string folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder)) CreateFolders(folder);
            VisualEffectAssetEditorUtility.CreateNewAsset(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Open(path);
        }

        private static void CreateFolders(string folder)
        {
            string parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || !(parent == "Assets" || parent.StartsWith("Assets/")) && !parent.StartsWith("Packages/"))
                throw new ArgumentException($"'{folder}' isn't under Assets/.");
            if (!AssetDatabase.IsValidFolder(parent)) CreateFolders(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }

        /// <summary>Writes the graph and reimports it, which compiles it. Returns the errors and warnings logged meanwhile.</summary>
        public List<string> Save()
        {
            var messages = new List<string>();
            void OnLog(string condition, string stackTrace, LogType type)
            {
                if (type != LogType.Log) messages.Add($"{type}: {condition}");
            }
            Application.logMessageReceived += OnLog;
            try
            {
                graph.SetExpressionGraphDirty();
                resource.WriteAssetWithSubAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            return messages;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Models and ids

        public static string IdOf(VFXModel model)
        {
            if (model == null) return null;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(model, out string _, out long local) && local != 0)
                return local.ToString(CultureInfo.InvariantCulture);
            return "~" + model.GetEntityId().ToString();
        }

        /// <summary>Every model in the graph: contexts, their blocks, operators and parameters.</summary>
        public IEnumerable<VFXModel> AllModels()
        {
            foreach (VFXModel child in graph.children)
            {
                yield return child;
                if (child is VFXContext context)
                    foreach (VFXBlock block in context.children)
                        yield return block;
            }
        }

        public VFXModel Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("No model id given.");
            id = id.Trim();
            VFXModel found = AllModels().FirstOrDefault(m => IdOf(m) == id);
            if (found == null) throw new ArgumentException($"No model with id '{id}' in {path}. See cliext.vfx inspect.");
            return found;
        }

        public static string KindOf(VFXModel model) => model switch
        {
            VFXContext _ => "context",
            VFXBlock _ => "block",
            VFXParameter _ => "parameter",
            VFXOperator _ => "operator",
            _ => model.GetType().Name
        };

        // ---------------------------------------------------------------------------------------------------------
        // Slots

        /// <summary>Resolves <c>&lt;model id&gt;:&lt;slot&gt;</c>. <paramref name="output"/> picks output slots, else inputs.</summary>
        public VFXSlot FindSlot(string reference, bool output, out VFXModel owner)
        {
            int colon = reference?.IndexOf(':') ?? -1;
            if (colon <= 0) throw new ArgumentException($"'{reference}' isn't a slot: use <model id>:<slot name>.");
            owner = Find(reference.Substring(0, colon));
            return FindSlot(owner, reference.Substring(colon + 1), output);
        }

        public static VFXSlot FindSlot(VFXModel owner, string slotPath, bool output)
        {
            if (!(owner is IVFXSlotContainer container)) throw new ArgumentException($"{owner.GetType().Name} has no slots.");
            var slots = output ? container.outputSlots : container.inputSlots;
            string[] parts = slotPath.Trim().Split('.');
            VFXSlot slot = Match(slots, parts[0]);
            if (slot == null)
                throw new ArgumentException($"{owner.GetType().Name} has no {(output ? "output" : "input")} slot '{parts[0]}'. It has: " +
                                            string.Join(", ", slots.Select(s => s.name)));
            for (int i = 1; i < parts.Length; i++)
            {
                VFXSlot child = Match(slot.children.Cast<VFXSlot>().ToList(), parts[i]);
                if (child == null)
                    throw new ArgumentException($"Slot '{slot.name}' has no field '{parts[i]}'. It has: " + string.Join(", ", slot.children.Cast<VFXSlot>().Select(s => s.name)));
                slot = child;
            }
            return slot;
        }

        private static VFXSlot Match(IList<VFXSlot> slots, string name)
        {
            if (name.StartsWith("#") && int.TryParse(name.Substring(1), out int index))
                return index >= 0 && index < slots.Count ? slots[index] : null;
            return slots.FirstOrDefault(s => string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase))
                   ?? slots.FirstOrDefault(s => string.Equals(s.name.TrimStart('_'), name.TrimStart('_'), StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------------------------------------------------
        // The library of contexts, blocks, operators and parameter types

        internal sealed class LibraryEntry
        {
            public string kind;
            public string name;
            public string category;
            public Type modelType;
            public IVFXModelDescriptor descriptor;
            /// <summary>The name as the node search shows it: VFX Graph's '|' separators and '_' highlights removed.</summary>
            public string DisplayName => Clean(name);
            /// <summary>Category without the '#0' sort prefixes.</summary>
            public string DisplayCategory => string.IsNullOrEmpty(category) ? null : string.Join("/", category.Split('/').Select(p => SortPrefix.Replace(p, "")).Where(p => p.Length > 0));
            public string Key => string.IsNullOrEmpty(DisplayCategory) ? DisplayName : $"{DisplayCategory}/{DisplayName}";
            public string RawKey => string.IsNullOrEmpty(category) ? name : $"{category}/{name}";
        }

        private static readonly System.Text.RegularExpressions.Regex SortPrefix = new System.Text.RegularExpressions.Regex(@"^#\d+");

        internal static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var parts = name.Split('|').Select(p => p.Trim()).Select(p => p.StartsWith("_") ? p.Substring(1) : p).Where(p => p.Length > 0);
            return string.Join(" ", parts);
        }

        public static IEnumerable<LibraryEntry> Library(string kind)
        {
            IEnumerable<LibraryEntry> Flatten(string k, IEnumerable<IVFXModelDescriptor> descriptors)
            {
                foreach (var d in descriptors)
                {
                    yield return new LibraryEntry { kind = k, name = d.name, category = d.category, modelType = d.modelType, descriptor = d };
                    IVFXModelDescriptor[] variants;
                    try { variants = d.subVariantDescriptors; }
                    catch { variants = Array.Empty<IVFXModelDescriptor>(); }
                    foreach (var v in variants)
                        yield return new LibraryEntry { kind = k, name = v.name, category = v.category, modelType = v.modelType, descriptor = v };
                }
            }

            kind = (kind ?? "").Trim().ToLowerInvariant();
            if (kind == "" || kind == "context") foreach (var e in Flatten("context", VFXLibrary.GetContexts())) yield return e;
            if (kind == "" || kind == "block") foreach (var e in Flatten("block", VFXLibrary.GetBlocks())) yield return e;
            if (kind == "" || kind == "operator") foreach (var e in Flatten("operator", VFXLibrary.GetOperators())) yield return e;
            if (kind == "" || kind == "parameter") foreach (var e in Flatten("parameter", VFXLibrary.GetParameters())) yield return e;
        }

        /// <summary>
        /// Finds a library entry by its key (<c>category/name</c>), its name, or its model type's class name, in that
        /// order. Throws, listing the candidates, when none or several match.
        /// </summary>
        public static LibraryEntry FindInLibrary(string kind, string type)
        {
            var all = Library(kind).ToList();
            string t = type.Trim();
            List<LibraryEntry> Pick(Func<LibraryEntry, bool> p) => all.Where(p).ToList();
            var matches = Pick(e => string.Equals(e.Key, t, StringComparison.OrdinalIgnoreCase) || string.Equals(e.RawKey, t, StringComparison.OrdinalIgnoreCase));
            if (matches.Count == 0) matches = Pick(e => string.Equals(e.DisplayName, t, StringComparison.OrdinalIgnoreCase) || string.Equals(e.name, t, StringComparison.OrdinalIgnoreCase));
            if (matches.Count == 0) matches = Pick(e => string.Equals(e.modelType?.Name, t, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
                throw new ArgumentException($"'{type}' matches {matches.Count} {kind}s: {string.Join(", ", matches.Take(12).Select(m => m.Key))}. Pass the full category/name.");
            var close = all.Where(e => e.Key.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).Take(12).Select(e => e.Key).ToList();
            throw new ArgumentException($"No {kind} called '{type}'." + (close.Count > 0 ? " Did you mean: " + string.Join(", ", close) : " See cliext.vfx types."));
        }

        // ---------------------------------------------------------------------------------------------------------
        // Values

        public static string Format(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable n when value.GetType().IsPrimitive: return n.ToString(null, CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
                case Vector2 v: return Arr(v.x, v.y);
                case Vector3 v: return Arr(v.x, v.y, v.z);
                case Vector4 v: return Arr(v.x, v.y, v.z, v.w);
                case Color c: return Arr(c.r, c.g, c.b, c.a);
                case AnimationCurve curve: return "[" + string.Join(",", curve.keys.Select(k => Arr(k.time, k.value))) + "]";
                case Gradient g: return "[" + string.Join(",", g.colorKeys.Select(k => Arr(k.time, k.color.r, k.color.g, k.color.b))) + "]";
                case Object o: return o == null ? null : (AssetDatabase.GetAssetPath(o) is string p && p.Length > 0 ? p : o.name);
                default:
                    // A VFX wrapper (Position, DirectionType, Vector, ...): show its vector, the form Parse takes.
                    var fields = value.GetType().GetFields();
                    var vectorField = fields.FirstOrDefault(f => f.FieldType == typeof(Vector3) || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector4));
                    if (vectorField != null && fields.Count(f => f.FieldType != typeof(int) && !f.FieldType.IsEnum) == 1)
                        return Format(vectorField.GetValue(value));
                    try { string json = JsonUtility.ToJson(value); if (json != "{}") return json; } catch { }
                    return value.ToString();
            }
        }

        private static string Arr(params float[] values) => "[" + string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

        /// <summary>
        /// Parses <paramref name="text"/> into <paramref name="type"/>. Numbers, true/false, enum names, <c>[x,y,z]</c>
        /// for vectors and colours (also <c>#RRGGBB</c>), asset paths for textures, meshes and other assets,
        /// <c>[[time,value],...]</c> for a curve, <c>[[time,r,g,b],...]</c> for a gradient, and JSON for other structs. A
        /// VFX wrapper type such as Position or DirectionType takes the vector form too.
        /// </summary>
        public static object Parse(string text, Type type, object current = null)
        {
            if (type == null) throw new ArgumentException("Unknown value type.");
            string s = (text ?? "").Trim();
            if (type == typeof(string)) return text ?? "";
            if (type == typeof(bool)) return s == "1" || bool.Parse(s);
            if (type.IsEnum)
            {
                if (int.TryParse(s, out int asInt)) return Enum.ToObject(type, asInt);
                var names = s.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim());
                return Enum.Parse(type, string.Join(", ", names), true);
            }
            if (type == typeof(float)) return float.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(uint)) return uint.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(Vector2)) { var f = Floats(s, 2); return new Vector2(f[0], f[1]); }
            if (type == typeof(Vector3)) { var f = Floats(s, 3); return new Vector3(f[0], f[1], f[2]); }
            if (type == typeof(Vector4)) { var f = Floats(s, 4); return new Vector4(f[0], f[1], f[2], f[3]); }
            if (type == typeof(Color))
            {
                if (s.StartsWith("#") && ColorUtility.TryParseHtmlString(s, out Color html)) return html;
                var f = Floats(s, 3, 4); return new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
            }
            if (type == typeof(AnimationCurve))
            {
                var keys = Rows(s).Select(r => new Keyframe(r[0], r.Length > 1 ? r[1] : 0f)).ToArray();
                var curve = new AnimationCurve(keys);
                for (int i = 0; i < keys.Length; i++) AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                for (int i = 0; i < keys.Length; i++) AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                return curve;
            }
            if (type == typeof(Gradient))
            {
                var rows = Rows(s);
                var g = new Gradient();
                g.SetKeys(rows.Select(r => new GradientColorKey(new Color(r[1], r[2], r[3]), r[0])).ToArray(),
                          rows.Select(r => new GradientAlphaKey(r.Length > 4 ? r[4] : 1f, r[0])).ToArray());
                return g;
            }
            if (typeof(Object).IsAssignableFrom(type))
            {
                if (s == "" || s == "null") return null;
                Object asset = AssetDatabase.LoadAssetAtPath(s, type) ?? AssetDatabase.LoadAllAssetsAtPath(s).FirstOrDefault(type.IsInstanceOfType);
                if (asset == null) throw new ArgumentException($"No {type.Name} at '{s}'.");
                return asset;
            }
            if (type.IsValueType && !type.IsPrimitive)
            {
                // A VFX wrapper (Position, DirectionType, Vector, ...) given as a vector: set its first vector field.
                if (s.StartsWith("[") || !s.StartsWith("{"))
                {
                    var field = type.GetFields().FirstOrDefault(f => f.FieldType == typeof(Vector3) || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector4) || f.FieldType == typeof(float));
                    if (field != null)
                    {
                        object boxed = current != null && current.GetType() == type ? current : Activator.CreateInstance(type);
                        field.SetValue(boxed, Parse(s, field.FieldType));
                        return boxed;
                    }
                }
                object parsed = current != null && current.GetType() == type ? current : Activator.CreateInstance(type);
                string json = s;
                var boxedObj = JsonUtility.FromJson(json, type);
                return boxedObj ?? parsed;
            }
            throw new ArgumentException($"Can't set a value of type {type.Name} from text.");
        }

        private static float[] Floats(string s, int min, int max = -1)
        {
            var parts = s.Trim('[', ']', '(', ')', ' ').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length == 1 && min > 1) parts = Enumerable.Repeat(parts[0], min).ToArray();
            if (parts.Length < min || (max > 0 && parts.Length > max) || (max < 0 && parts.Length > min))
                throw new ArgumentException($"Expected {min}{(max > 0 ? "-" + max : "")} numbers, got '{s}'.");
            return parts;
        }

        private static List<float[]> Rows(string s)
        {
            string inner = s.Trim();
            if (inner.StartsWith("[")) inner = inner.Substring(1);
            if (inner.EndsWith("]")) inner = inner.Substring(0, inner.Length - 1);
            var rows = new List<float[]>();
            foreach (string row in inner.Split(new[] { ']' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string r = row.Trim().TrimStart(',').Trim().TrimStart('[');
                if (r.Length == 0) continue;
                rows.Add(r.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray());
            }
            if (rows.Count == 0) throw new ArgumentException($"Expected [[t,v],...], got '{s}'.");
            return rows;
        }
    }
}

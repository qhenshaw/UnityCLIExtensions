using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Snapshots of a scene or prefab (every GameObject, component and serialized property), and readable diffs between a
    /// snapshot and the current state: objects added, removed, renamed or moved, components added or removed, and
    /// properties with their old and new values. Snapshots are kept in <c>Library/CLIExtensions/snapshots/</c>.
    /// </summary>
    public static class HierarchySnapshot
    {
        [Serializable]
        public class Property { public string path; public string value; }

        [Serializable]
        public class ComponentSnap
        {
            public string id;
            public string type;
            public int index;
            public List<Property> properties = new List<Property>();
        }

        [Serializable]
        public class ObjectSnap
        {
            public string id;
            public string hierarchyPath;
            public List<ComponentSnap> components = new List<ComponentSnap>();
        }

        [Serializable]
        public class Snapshot
        {
            public string name;
            public string assetPath;
            public string kind;
            public string takenUtc;
            public List<ObjectSnap> objects = new List<ObjectSnap>();
        }

        /// <summary>One difference. <c>change</c> is object-added, object-removed, object-renamed, component-added, component-removed or property-changed.</summary>
        [Serializable]
        public class Change
        {
            public string change;
            public string @object;
            public string component;
            public string property;
            public string before;
            public string after;
        }

        private const int MaxValueLength = 200;
        private const int MaxPropertiesPerComponent = 400;

        private static string Folder
        {
            get
            {
                string dir = Path.Combine(CliExtPaths.StateDirectory, "snapshots");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FileFor(string name)
        {
            string safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray());
            return Path.Combine(Folder, safe + ".json");
        }

        /// <summary>Takes a snapshot of a scene (.unity) or prefab (.prefab) and saves it under <paramref name="name"/> (default: the path).</summary>
        public static Snapshot Take(string assetPath, string name = null)
        {
            Snapshot snapshot = Capture(assetPath);
            snapshot.name = string.IsNullOrEmpty(name) ? assetPath : name;
            File.WriteAllText(FileFor(snapshot.name), JsonUtility.ToJson(snapshot));
            return snapshot;
        }

        /// <summary>Loads a saved snapshot, or null.</summary>
        public static Snapshot Load(string name)
        {
            string file = FileFor(name);
            return File.Exists(file) ? JsonUtility.FromJson<Snapshot>(File.ReadAllText(file)) : null;
        }

        /// <summary>Deletes the saved snapshot <paramref name="name"/>. Returns false when there is none.</summary>
        public static bool Delete(string name)
        {
            string file = FileFor(name);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }

        /// <summary>Deletes every saved snapshot. Returns how many.</summary>
        public static int DeleteAll()
        {
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(Folder, "*.json").ToList()) { File.Delete(file); count++; }
            return count;
        }

        /// <summary>The saved snapshots.</summary>
        public static IEnumerable<Snapshot> All() =>
            Directory.EnumerateFiles(Folder, "*.json").Select(f =>
            {
                try { return JsonUtility.FromJson<Snapshot>(File.ReadAllText(f)); }
                catch { return null; }
            }).Where(s => s != null);

        /// <summary>Compares the saved snapshot <paramref name="name"/> with the asset's current state.</summary>
        public static List<Change> CompareWithCurrent(string name, out Snapshot before, out Snapshot after)
        {
            before = Load(name) ?? throw new ArgumentException($"No snapshot named '{name}'. Take one with cliext.diff snapshot.");
            after = Capture(before.assetPath);
            return Diff(before, after);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Capture

        /// <summary>Captures a scene or prefab without saving it. A scene that isn't open is opened additively and closed again.</summary>
        public static Snapshot Capture(string assetPath)
        {
            var snapshot = new Snapshot { assetPath = assetPath, takenUtc = DateTime.UtcNow.ToString("o") };
            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.kind = "scene";
                Scene scene = SceneManager.GetSceneByPath(assetPath);
                bool openedHere = false;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath) == null) throw new ArgumentException($"No scene at '{assetPath}'.");
                    scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                    openedHere = true;
                }
                try
                {
                    foreach (GameObject root in scene.GetRootGameObjects()) Walk(root, root.name, snapshot.objects, persistent: false);
                }
                finally
                {
                    if (openedHere) EditorSceneManager.CloseScene(scene, true);
                }
            }
            else if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.kind = "prefab";
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (root == null) throw new ArgumentException($"No prefab at '{assetPath}'.");
                Walk(root, root.name, snapshot.objects, persistent: true);
            }
            else
            {
                throw new ArgumentException($"'{assetPath}' is not a scene (.unity) or prefab (.prefab).");
            }
            return snapshot;
        }

        private static void Walk(GameObject go, string path, List<ObjectSnap> into, bool persistent)
        {
            var snap = new ObjectSnap { id = IdOf(go, persistent), hierarchyPath = path };
            snap.components.Add(CaptureComponent(go, "GameObject", 0, IdOf(go, persistent)));
            var counts = new Dictionary<string, int>();
            foreach (Component component in go.GetComponents<Component>())
            {
                string type = component == null ? "MissingScript" : component.GetType().Name;
                counts.TryGetValue(type, out int index);
                counts[type] = index + 1;
                snap.components.Add(component == null
                    ? new ComponentSnap { id = "", type = type, index = index }
                    : CaptureComponent(component, type, index, IdOf(component, persistent)));
            }
            into.Add(snap);

            var siblings = new Dictionary<string, int>();
            foreach (Transform child in go.transform)
            {
                // Siblings with the same name get #2, #3... so their paths stay distinct.
                siblings.TryGetValue(child.name, out int n);
                siblings[child.name] = n + 1;
                Walk(child.gameObject, path + "/" + child.name + (n > 0 ? "#" + (n + 1) : ""), into, persistent);
            }
        }

        private static string IdOf(Object obj, bool persistent)
        {
            if (persistent)
                return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string _, out long local) ? local.ToString() : "";
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return id.targetObjectId == 0 ? "" : id.targetObjectId + ":" + id.targetPrefabId;
        }

        private static ComponentSnap CaptureComponent(Object target, string type, int index, string id)
        {
            var snap = new ComponentSnap { id = id, type = type, index = index };
            using var so = new SerializedObject(target);
            SerializedProperty it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter) && snap.properties.Count < MaxPropertiesPerComponent)
            {
                enter = true;
                if (it.propertyPath == "m_Script" || it.propertyPath.StartsWith("m_Component", StringComparison.Ordinal)) { enter = false; continue; }
                if (it.propertyType == SerializedPropertyType.String) { enter = false; snap.properties.Add(new Property { path = it.propertyPath, value = Clip(it.stringValue) }); continue; }
                if (it.hasVisibleChildren && it.propertyType != SerializedPropertyType.ObjectReference) continue;
                snap.properties.Add(new Property { path = it.propertyPath, value = Clip(ValueOf(it)) });
            }
            return snap;
        }

        private static string ValueOf(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                    return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return p.doubleValue.ToString("0.#####", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character: return ((char)p.intValue).ToString();
                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Color: return "#" + ColorUtility.ToHtmlStringRGBA(p.colorValue);
                case SerializedPropertyType.ObjectReference: return Describe(p.objectReferenceValue);
                case SerializedPropertyType.ExposedReference: return Describe(p.exposedReferenceValue);
                case SerializedPropertyType.ManagedReference: return p.managedReferenceFullTypename;
                default: return p.propertyType.ToString();
            }
        }

        private static string Describe(Object obj)
        {
            if (obj == null) return "null";
            string asset = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(asset))
                return AssetDatabase.IsMainAsset(obj) ? asset : $"{asset} ({obj.name}, {obj.GetType().Name})";
            Transform t = obj is Component c ? c.transform : obj is GameObject g ? g.transform : null;
            if (t == null) return $"{obj.name} ({obj.GetType().Name})";
            string path = t.name;
            for (Transform parent = t.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return obj is GameObject ? path : $"{path} ({obj.GetType().Name})";
        }

        private static string Clip(string s) => s == null ? null : s.Length <= MaxValueLength ? s : s.Substring(0, MaxValueLength) + "…";

        // -------------------------------------------------------------------------------------------------------------
        // Diff

        /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>. Public for tests.</summary>
        public static List<Change> Diff(Snapshot before, Snapshot after)
        {
            var changes = new List<Change>();
            var pairs = Match(before.objects, after.objects, o => o.id, o => o.hierarchyPath);

            foreach (var (b, a) in pairs)
            {
                if (a == null) { changes.Add(new Change { change = "object-removed", @object = b.hierarchyPath }); continue; }
                if (b == null) { changes.Add(new Change { change = "object-added", @object = a.hierarchyPath }); continue; }
                if (b.hierarchyPath != a.hierarchyPath)
                    changes.Add(new Change { change = "object-renamed", @object = a.hierarchyPath, before = b.hierarchyPath, after = a.hierarchyPath });

                var componentPairs = Match(b.components, a.components, c => c.id, c => c.type + "#" + c.index);
                foreach (var (bc, ac) in componentPairs)
                {
                    if (ac == null) { changes.Add(new Change { change = "component-removed", @object = a.hierarchyPath, component = bc.type }); continue; }
                    if (bc == null) { changes.Add(new Change { change = "component-added", @object = a.hierarchyPath, component = ac.type }); continue; }
                    var beforeProps = bc.properties.GroupBy(p => p.path).ToDictionary(g => g.Key, g => g.First().value);
                    var afterProps = ac.properties.GroupBy(p => p.path).ToDictionary(g => g.Key, g => g.First().value);
                    foreach (string path in beforeProps.Keys.Union(afterProps.Keys))
                    {
                        beforeProps.TryGetValue(path, out string old);
                        afterProps.TryGetValue(path, out string now);
                        if (old == now) continue;
                        // Renaming an object changes its own m_Name; the object-renamed line already says so.
                        if (ac.type == "GameObject" && path == "m_Name" && b.hierarchyPath != a.hierarchyPath) continue;
                        changes.Add(new Change { change = "property-changed", @object = a.hierarchyPath, component = ac.type, property = path, before = old, after = now });
                    }
                }
            }
            return changes;
        }

        // Pairs items by id first, then the leftovers by a fallback key (an object created since the snapshot has no id
        // until the scene is saved).
        private static List<(T before, T after)> Match<T>(List<T> before, List<T> after, Func<T, string> id, Func<T, string> fallback) where T : class
        {
            var result = new List<(T, T)>();
            var unmatchedAfter = new List<T>(after);
            var unmatchedBefore = new List<T>();
            var afterById = after.Where(x => !string.IsNullOrEmpty(id(x))).GroupBy(id).ToDictionary(g => g.Key, g => g.First());
            foreach (T b in before)
            {
                if (!string.IsNullOrEmpty(id(b)) && afterById.TryGetValue(id(b), out T a) && unmatchedAfter.Remove(a)) result.Add((b, a));
                else unmatchedBefore.Add(b);
            }
            foreach (T b in unmatchedBefore)
            {
                T a = unmatchedAfter.FirstOrDefault(x => fallback(x) == fallback(b));
                if (a != null) unmatchedAfter.Remove(a);
                result.Add((b, a));
            }
            foreach (T a in unmatchedAfter) result.Add((null, a));
            return result;
        }
    }
}

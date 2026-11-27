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
        public static Snapshot Take(string assetPath, string name = null) => Save(Capture(assetPath), name);

        /// <summary>Saves a captured snapshot under <paramref name="name"/> (default: its path).</summary>
        public static Snapshot Save(Snapshot snapshot, string name = null)
        {
            snapshot.name = string.IsNullOrEmpty(name) ? snapshot.assetPath : name;
            File.WriteAllText(FileFor(snapshot.name), JsonUtility.ToJson(snapshot));
            return snapshot;
        }

        /// <summary>
        /// Normalizes a --path for <see cref="Capture"/>: a usage error for a path outside Assets/ and Packages/ or an
        /// asset that isn't a scene or prefab, a not-found error for a missing scene or prefab.
        /// </summary>
        internal static string ResolvePath(string path)
        {
            string p = Animation.AnimationErrors.AssetPath(path);
            if (!p.StartsWith("Assets/", StringComparison.Ordinal) && !p.StartsWith("Packages/", StringComparison.Ordinal))
                throw new Animation.AnimUsageException($"--path '{path}' isn't under Assets/ or Packages/: give a scene or prefab path such as Assets/Scenes/Main.unity.");
            bool scene = p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase), prefab = p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            var main = AssetDatabase.LoadMainAssetAtPath(p);
            if (!scene && !prefab)
            {
                if (main == null && !AssetDatabase.IsValidFolder(p))
                    throw new Animation.AnimUsageException($"--path '{p}' isn't a scene (.unity) or prefab (.prefab) path.");
                string what = AssetDatabase.IsValidFolder(p) ? "a folder" : Animation.AnimationErrors.A(main.GetType().Name);
                throw new Animation.AnimUsageException($"--path '{p}' is {what}, not a scene (.unity) or prefab (.prefab).");
            }
            if (main == null)
                throw new Animation.AnimNotFoundException(scene
                    ? $"--path: no scene at '{p}'. Check the path (asset find --type Scene lists the scenes)."
                    : $"--path: no prefab at '{p}'. Check the path (asset find --type Prefab lists the prefabs).");
            return p;
        }

        /// <summary>The error for an unknown snapshot name, with close names or the list.</summary>
        internal static string NoSnapshot(string name) =>
            $"No snapshot named '{name}'.{Animation.AnimationErrors.Hint(name, All().Select(s => s.name), "Snapshots", "cliext.diff list")} Take one with cliext.diff snapshot.";

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
            before = Load(name) ?? throw new Animation.AnimNotFoundException(NoSnapshot(name));
            if (AssetDatabase.LoadMainAssetAtPath(before.assetPath) == null)
                throw new Animation.AnimNotFoundException($"Snapshot '{name}' is of '{before.assetPath}', which is gone: it can't be compared. Delete it with cliext.diff delete --name \"{name}\".");
            after = Capture(before.assetPath);
            after.name = before.name;
            return Diff(before, after);
        }

        // Set while ChangesOnSave captures: the brain-driven values are kept (they are what a save writes), and the
        // objects with a CinemachineBrain are collected so their changes can be named as the brain's.
        private static HashSet<string> s_BrainPaths;

        /// <summary>One change a save would write, with the component that likely made it when nobody edited it.</summary>
        [Serializable]
        public class SaveChange : Change { public string drivenBy; }

        /// <summary>
        /// What saving the open scene <paramref name="scenePath"/> would write: its file on disk (captured from a temporary
        /// copy, opened additively) against the scene in memory, brain-driven camera values included. Changes to values a
        /// CinemachineBrain writes in Edit mode carry <c>drivenBy = "CinemachineBrain"</c>.
        /// </summary>
        public static List<SaveChange> ChangesOnSave(string scenePath)
        {
            string tempDir = "Assets/__CliextSaveCompare";
            string temp = $"{tempDir}/{Guid.NewGuid():N}.unity";
            var brains = new HashSet<string>();
            s_BrainPaths = brains;
            try
            {
                Directory.CreateDirectory(tempDir);
                File.Copy(scenePath, temp);
                AssetDatabase.ImportAsset(temp, ImportAssetOptions.ForceSynchronousImport);
                Snapshot before = Capture(temp);
                before.assetPath = scenePath;
                Snapshot after = Capture(scenePath);
                return Diff(before, after).Select(c => new SaveChange
                {
                    change = c.change, @object = c.@object, component = c.component, property = c.property, before = c.before, after = c.after,
                    drivenBy = c.change == "property-changed" && brains.Contains(c.@object) && IsBrainProperty(c.component, c.property) ? "CinemachineBrain" : null
                }).ToList();
            }
            finally
            {
                s_BrainPaths = null;
                AssetDatabase.DeleteAsset(temp);
                if (AssetDatabase.IsValidFolder(tempDir) && AssetDatabase.FindAssets("", new[] { tempDir }).Length == 0) AssetDatabase.DeleteAsset(tempDir);
            }
        }

        private static bool IsBrainProperty(string component, string property)
        {
            if (string.IsNullOrEmpty(property)) return false;
            string type = (component ?? "").Split('[', '#', ' ')[0];
            if (type == "Transform") return property.StartsWith("m_LocalPosition", StringComparison.Ordinal) || property.StartsWith("m_LocalRotation", StringComparison.Ordinal);
            if (type == "Camera") return s_BrainCameraProperties.Any(x => property == x || property.StartsWith(x + ".", StringComparison.Ordinal));
            return false;
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
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath) == null) throw new Animation.AnimNotFoundException($"No scene at '{assetPath}'. Check the path (asset find --type Scene lists the scenes).");
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
                if (root == null) throw new Animation.AnimNotFoundException($"No prefab at '{assetPath}'. Check the path (asset find --type Prefab lists the prefabs).");
                Walk(root, root.name, snapshot.objects, persistent: true);
            }
            else
            {
                throw new Animation.AnimUsageException($"'{assetPath}' is not a scene (.unity) or prefab (.prefab).");
            }
            return snapshot;
        }

        private static void Walk(GameObject go, string path, List<ObjectSnap> into, bool persistent)
        {
            var snap = new ObjectSnap { id = IdOf(go, persistent), hierarchyPath = path };
            snap.components.Add(CaptureComponent(go, "GameObject", 0, IdOf(go, persistent)));
            var counts = new Dictionary<string, int>();
            Component[] components = go.GetComponents<Component>();
            bool driven = components.Any(IsCinemachineBrain);
            if (driven) s_BrainPaths?.Add(path);
            foreach (Component component in components)
            {
                string type = component == null ? "MissingScript" : component.GetType().Name;
                counts.TryGetValue(type, out int index);
                counts[type] = index + 1;
                snap.components.Add(component == null
                    ? new ComponentSnap { id = "", type = type, index = index }
                    : CaptureComponent(component, type, index, IdOf(component, persistent), driven && s_BrainPaths == null ? DrivenByBrain(component) : null));
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

        // A CinemachineBrain (matched by name, so the package needn't be installed) writes its camera's transform and
        // lens every frame it updates, in Edit mode too, without dirtying the scene: a snapshot taken after an update
        // and one taken after the scene is reopened differ there though nobody changed anything.
        private static bool IsCinemachineBrain(Component c) =>
            c is Behaviour b && b.enabled && c.GetType().Name == "CinemachineBrain";

        private static readonly string[] s_BrainCameraProperties =
        {
            "field of view", "orthographic size", "near clip plane", "far clip plane", "orthographic", "m_FocalLength", "m_SensorSize", "m_LensShift",
            "m_GateFitMode", "m_Iso", "m_ShutterSpeed", "m_Aperture", "m_FocusDistance", "m_BladeCount", "m_Curvature", "m_BarrelClipping", "m_Anamorphism"
        };

        /// <summary>The properties of <paramref name="c"/> that a CinemachineBrain on its GameObject sets, or null.</summary>
        internal static Func<string, bool> DrivenByBrain(Component c)
        {
            if (c is Transform) return p => p.StartsWith("m_LocalPosition", StringComparison.Ordinal) || p.StartsWith("m_LocalRotation", StringComparison.Ordinal);
            if (c is Camera) return p => s_BrainCameraProperties.Any(x => p == x || p.StartsWith(x + ".", StringComparison.Ordinal));
            return null;
        }

        private static ComponentSnap CaptureComponent(Object target, string type, int index, string id, Func<string, bool> skip = null)
        {
            var snap = new ComponentSnap { id = id, type = type, index = index };
            using var so = new SerializedObject(target);
            SerializedProperty it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter) && snap.properties.Count < MaxPropertiesPerComponent)
            {
                enter = true;
                if (it.propertyPath == "m_Script" || it.propertyPath.StartsWith("m_Component", StringComparison.Ordinal)) { enter = false; continue; }
                if (skip != null && skip(it.propertyPath)) { enter = false; continue; }
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
            // Where each object's path went, so a renamed or moved parent is one change, not one per descendant, and
            // a reference described by the path of an object that moved isn't reported as changed.
            var moved = new Dictionary<string, string>();
            foreach (var (b, a) in pairs) if (b != null && a != null) moved[b.hierarchyPath] = a.hierarchyPath;

            foreach (var (b, a) in pairs)
            {
                if (a == null) { changes.Add(new Change { change = "object-removed", @object = b.hierarchyPath }); continue; }
                if (b == null) { changes.Add(new Change { change = "object-added", @object = a.hierarchyPath }); continue; }
                if (b.hierarchyPath != a.hierarchyPath && !FollowsItsParent(b.hierarchyPath, a.hierarchyPath, moved))
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
                        if (old != null && now != null && MovedReference(old, moved) == now) continue;
                        changes.Add(new Change { change = "property-changed", @object = a.hierarchyPath, component = ac.type, property = path, before = old, after = now });
                    }
                }
            }
            return changes;
        }

        private static string ParentOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? null : path.Substring(0, slash);
        }

        private static string LeafOf(string path) => path.Substring(path.LastIndexOf('/') + 1);

        // True when the object kept its name and parent, and only its parent's path changed.
        private static bool FollowsItsParent(string before, string after, Dictionary<string, string> moved)
        {
            string pb = ParentOf(before), pa = ParentOf(after);
            return pb != null && pa != null && LeafOf(before) == LeafOf(after) && moved.TryGetValue(pb, out string to) && to == pa;
        }

        // A scene object reference ("Path/To/Object" or "Path/To/Object (Type)") with the path of where that object is now.
        private static string MovedReference(string value, Dictionary<string, string> moved)
        {
            if (moved.TryGetValue(value, out string to)) return to;
            int i = value.LastIndexOf(" (", StringComparison.Ordinal);
            if (i > 0 && value.EndsWith(")", StringComparison.Ordinal) && moved.TryGetValue(value.Substring(0, i), out to)) return to + value.Substring(i);
            return value;
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

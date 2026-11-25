using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCLIExtensions
{
    /// <summary>An asset in a dependency answer.</summary>
    [Serializable]
    public class AssetRef
    {
        public string path;
        public string type;
        public string guid;

        internal static AssetRef Of(string path) => new AssetRef
        {
            path = path,
            type = AssetDatabase.GetMainAssetTypeAtPath(path)?.Name,
            guid = AssetDatabase.AssetPathToGUID(path)
        };
    }

    /// <summary>
    /// "What does this asset use" and "what uses this asset", for prefabs, scenes, materials, scripts, ScriptableObjects
    /// and any other asset the AssetDatabase tracks references for. Scripts count: a prefab or scene uses the MonoScript
    /// of each component on it.
    /// </summary>
    public class AssetDependencies : AssetPostprocessor
    {
        // Reverse index: asset path -> the project assets that reference it directly. Built on first use, dropped on
        // any import, delete or move.
        private static Dictionary<string, List<string>> s_UsedBy;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            s_UsedBy = null;
        }

        /// <summary>Accepts an asset path or a GUID; returns the path, or null when there is no such asset.</summary>
        public static string Resolve(string pathOrGuid)
        {
            if (string.IsNullOrWhiteSpace(pathOrGuid)) return null;
            string s = pathOrGuid.Trim().Replace('\\', '/');
            if (s.Length == 32 && s.All(Uri.IsHexDigit))
            {
                string fromGuid = AssetDatabase.GUIDToAssetPath(s);
                return string.IsNullOrEmpty(fromGuid) ? null : fromGuid;
            }
            return string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(s, AssetPathToGUIDOptions.OnlyExistingAssets)) ? null : s;
        }

        /// <summary>
        /// The assets <paramref name="path"/> uses (directly, or through others with <paramref name="recursive"/>), without
        /// itself. Editor-only scripts are left out unless <paramref name="includeEditorScripts"/>: they are tooling
        /// metadata, such as the AssetVersion script HDRP attaches to every material, not something the asset needs at runtime.
        /// </summary>
        public static List<AssetRef> Uses(string path, bool recursive, bool includeEditorScripts = false)
        {
            return AssetDatabase.GetDependencies(path, recursive)
                .Where(p => p != path)
                .Where(p => includeEditorScripts || !IsEditorOnlyScript(p))
                .OrderBy(p => p)
                .Select(AssetRef.Of)
                .ToList();
        }

        /// <summary>True for a C# script that compiles only into Editor assemblies (an Editor folder or an Editor-only .asmdef).</summary>
        public static bool IsEditorOnlyScript(string path)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            string assembly = CompilationPipeline.GetAssemblyNameFromScriptPath(path);
            if (string.IsNullOrEmpty(assembly)) return false;
            assembly = Path.GetFileNameWithoutExtension(assembly);
            s_PlayerAssemblies ??= new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name), StringComparer.Ordinal);
            return !s_PlayerAssemblies.Contains(assembly);
        }

        // Dropped with the domain on every recompile, which is when the assembly list can change.
        private static HashSet<string> s_PlayerAssemblies;

        /// <summary>
        /// The project assets that use <paramref name="path"/>: directly, or through others with <paramref name="recursive"/>.
        /// Only assets under Assets/ are searched (and packages' assets when <paramref name="includePackages"/>).
        /// </summary>
        public static List<AssetRef> UsedBy(string path, bool recursive, bool includePackages = false)
        {
            var index = UsedByIndex(includePackages);
            var found = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(path);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!index.TryGetValue(current, out List<string> users)) continue;
                foreach (string user in users)
                {
                    if (user == path || !found.Add(user)) continue;
                    if (recursive) queue.Enqueue(user);
                }
            }
            return found.OrderBy(p => p).Select(AssetRef.Of).ToList();
        }

        private static bool s_IndexHasPackages;

        private static Dictionary<string, List<string>> UsedByIndex(bool includePackages)
        {
            if (s_UsedBy != null && (s_IndexHasPackages || !includePackages)) return s_UsedBy;
            var index = new Dictionary<string, List<string>>();
            foreach (string asset in AssetDatabase.GetAllAssetPaths())
            {
                bool inAssets = asset.StartsWith("Assets/", StringComparison.Ordinal);
                if (!inAssets && !(includePackages && asset.StartsWith("Packages/", StringComparison.Ordinal))) continue;
                if (AssetDatabase.IsValidFolder(asset)) continue;
                foreach (string dependency in AssetDatabase.GetDependencies(asset, false))
                {
                    if (dependency == asset) continue;
                    if (!index.TryGetValue(dependency, out List<string> users)) index[dependency] = users = new List<string>();
                    users.Add(asset);
                }
            }
            s_IndexHasPackages = includePackages;
            return s_UsedBy = index;
        }
    }
}

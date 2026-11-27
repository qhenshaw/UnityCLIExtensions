using System;
using System.IO;
using UnityEditor;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Makes the folders a new asset goes into, for every cliext command that creates assets. A folder that already
    /// exists on disk but isn't imported yet (made with New-Item or mkdir) is imported rather than created again:
    /// <c>AssetDatabase.CreateFolder</c> would otherwise make a second one beside it, named "&lt;Folder&gt; 1".
    /// </summary>
    public static class ProjectFolders
    {
        /// <summary>True for Assets, Assets/..., or a folder inside a package (Packages/&lt;name&gt;/...).</summary>
        public static bool IsProjectFolder(string folder)
        {
            folder = Normalize(folder);
            return folder == "Assets" || folder.StartsWith("Assets/", StringComparison.Ordinal) ||
                   (folder.StartsWith("Packages/", StringComparison.Ordinal) && folder.IndexOf('/', "Packages/".Length) > 0);
        }

        /// <summary>
        /// Makes <paramref name="folder"/> and any missing parents an imported asset folder. Returns the folders it
        /// made or imported, outermost first (empty when it was already there). Throws <see cref="ArgumentException"/>
        /// for a folder outside Assets/ or a package, or when Unity puts a new folder anywhere but the path asked for.
        /// </summary>
        public static string[] Ensure(string folder)
        {
            folder = Normalize(folder);
            var made = new System.Collections.Generic.List<string>();
            EnsureInto(folder, made);
            return made.ToArray();
        }

        private static void EnsureInto(string folder, System.Collections.Generic.List<string> made)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            if (!IsProjectFolder(folder))
                throw new ArgumentException($"'{folder}' isn't under Assets/ (or inside a package): give a path such as Assets/Effects/Name.");
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) throw new ArgumentException($"'{folder}' isn't a folder path.");
            EnsureInto(parent, made);

            // On disk but not imported: import it (and with it a .meta), so it is the folder the asset goes into.
            if (Directory.Exists(FileUtil.GetPhysicalPath(folder)))
            {
                AssetDatabase.ImportAsset(folder, ImportAssetOptions.ForceSynchronousImport);
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                if (AssetDatabase.IsValidFolder(folder)) { made.Add(folder); return; }
                throw new ArgumentException($"'{folder}' is on disk but Unity didn't import it as a folder. Run asset refresh, then try again.");
            }
            string guid = AssetDatabase.CreateFolder(parent, name);
            string at = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
            if (at != folder)
            {
                if (at.Length > 0) AssetDatabase.DeleteAsset(at);
                throw new ArgumentException($"Couldn't create the folder '{folder}'{(at.Length > 0 ? $" (Unity made '{at}' instead, now removed)" : "")}.");
            }
            made.Add(folder);
        }

        private static string Normalize(string folder) => (folder ?? "").Trim().Replace('\\', '/').TrimEnd('/');
    }
}

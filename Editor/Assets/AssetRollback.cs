using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Makes a batch of edits to asset files all-or-nothing. <see cref="Begin"/> saves any unsaved changes the assets
    /// already have and copies their files (with .meta) aside; <see cref="Rollback"/> discards the in-memory edits (by
    /// unloading the objects, so the next access reloads them from disk), deletes asset files created inside the
    /// watched folders, and restores any file that changed or went missing. <see cref="Dispose"/> drops the copies.
    /// Reimporting is not enough: Unity keeps a loaded native asset's in-memory state across a reimport.
    /// Unloading can't bring back a sub-asset the batch destroyed (an AnimatorController's state, transition or
    /// sub-state machine): pass <c>revertUndo</c> for assets whose editing API records Undo, and the rollback first
    /// reverts the batch's Undo group, which recreates them.
    /// </summary>
    public sealed class AssetRollback : IDisposable
    {
        private readonly string _backupDir;
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(); // asset path -> backup copy
        private readonly List<string> _folders = new List<string>();
        private readonly HashSet<string> _existingInFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _undoGroup = -1;
        private readonly HashSet<string> _idsAtBegin = new HashSet<string>(); // "path:localId" of every object in the files at Begin

        private AssetRollback(string backupDir) { _backupDir = backupDir; }

        /// <summary>
        /// Watches the given asset files, and every asset file inside the given folders (new ones included). With
        /// <paramref name="revertUndo"/>, the edits made after this call go in an Undo group of their own that
        /// <see cref="Rollback"/> reverts.
        /// </summary>
        public static AssetRollback Begin(IEnumerable<string> assetPaths, IEnumerable<string> folders = null, bool revertUndo = false)
        {
            var r = new AssetRollback(Path.Combine("Temp", "CLIExtensions", "rollback", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(r._backupDir);

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in assetPaths ?? Enumerable.Empty<string>()) if (!string.IsNullOrEmpty(p)) paths.Add(Normalize(p));
            foreach (var f in folders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                string folder = Normalize(f);
                r._folders.Add(folder);
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string p = Normalize(file);
                    paths.Add(p);
                    r._existingInFolders.Add(p);
                }
            }

            // Unsaved changes made before the batch are saved first, so a rollback only discards the batch.
            foreach (var p in paths)
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p))
                    if (o != null && EditorUtility.IsDirty(o)) AssetDatabase.SaveAssetIfDirty(o);

            int n = 0;
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                string copy = Path.Combine(r._backupDir, (n++).ToString());
                File.Copy(p, copy, true);
                if (File.Exists(p + ".meta")) File.Copy(p + ".meta", copy + ".meta", true);
                r._files[p] = copy;
            }
            if (revertUndo)
            {
                // Every object in the files is recorded whole, so the revert also undoes changes the editing API
                // doesn't record itself (a state's name set directly).
                Undo.IncrementCurrentGroup();
                r._undoGroup = Undo.GetCurrentGroup();
                foreach (var p in paths) AssetDatabase.LoadAllAssetsAtPath(p);
                var objects = Loaded(paths);
                foreach (var o in objects) r._idsAtBegin.Add(LocalId(o));
                if (objects.Length > 0) Undo.RegisterCompleteObjectUndo(objects, "cliext batch");
            }
            return r;
        }

        /// <summary>Puts the watched assets back as they were at <see cref="Begin"/>, on disk and in memory.</summary>
        public void Rollback()
        {
            // With revertUndo: the batch's Undo group puts the objects back, sub-assets it destroyed (an
            // AnimatorController's states and transitions) included. Unloading instead would lose those: Unity can't
            // reload a sub-asset destroyed in memory from the unchanged file, so the next save would drop it.
            bool reverted = false;
            if (_undoGroup >= 0)
            {
                Undo.RevertAllDownToGroup(_undoGroup);
                _undoGroup = -1;
                reverted = true;
                // Sub-assets the batch added without recording their creation (a TimelineAsset's marker track) are
                // left in the file by the revert, unreferenced, and the next save would write them: destroy them.
                foreach (var o in Loaded(_files.Keys))
                    if (!(o is AssetImporter) && !AssetDatabase.IsMainAsset(o) && !_idsAtBegin.Contains(LocalId(o)))
                        UnityEngine.Object.DestroyImmediate(o, true);
                foreach (var o in Loaded(_files.Keys)) EditorUtility.ClearDirty(o);
            }

            // Asset files the batch created inside the watched folders.
            foreach (var folder in _folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string p = Normalize(file);
                    if (!_existingInFolders.Contains(p)) AssetDatabase.DeleteAsset(p);
                }
            }

            // In-memory edits: unloaded objects reload from disk on their next access, keeping their ids.
            if (!reverted) Unload(_files.Keys);

            // Files the batch changed or deleted.
            var restored = new List<string>();
            foreach (var (p, copy) in _files)
            {
                bool changed = !File.Exists(p) || !SameBytes(p, copy) ||
                               File.Exists(copy + ".meta") && (!File.Exists(p + ".meta") || !SameBytes(p + ".meta", copy + ".meta"));
                if (!changed) continue;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(copy, p, true);
                if (File.Exists(copy + ".meta")) File.Copy(copy + ".meta", p + ".meta", true);
                restored.Add(p);
            }
            if (restored.Count > 0)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                foreach (var p in restored)
                    AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Unload(restored);
            }
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Every loaded object stored in these files, hidden ones included: LoadAllAssetsAtPath leaves out objects such as
        // a VFX Graph's editor model, which would otherwise keep the batch's edits and save them with the next change.
        private static void Unload(IEnumerable<string> paths)
        {
            foreach (var o in Loaded(paths))
            {
                EditorUtility.ClearDirty(o);
                Resources.UnloadAsset(o);
            }
        }

        private static UnityEngine.Object[] Loaded(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return Array.Empty<UnityEngine.Object>();
            return Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .Where(o => o != null && !(o is GameObject) && !(o is Component) && !(o is AssetBundle))
                .Where(o => EditorUtility.IsPersistent(o) && set.Contains(AssetDatabase.GetAssetPath(o)))
                .ToArray();
        }

        private static string LocalId(UnityEngine.Object o) =>
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long id) ? guid + ":" + id : "entity:" + o.GetEntityId();

        private static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        private static string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');
    }
}

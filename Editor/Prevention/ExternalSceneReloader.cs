using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Reloads open scenes whose file changed on disk (a checkout, a pull, or an agent editing the .unity file), in an
    /// Editor started with <c>-automated</c>.
    /// </summary>
    /// <remarks>
    /// With a person at the Editor, Unity asks "The open scene(s) have been modified externally: Reload / Ignore", which
    /// the watchdog answers with Reload. With <c>-automated</c> Unity shows no dialog and silently takes Ignore, so the
    /// Editor keeps the stale scene and the next save overwrites the file on disk. This puts Reload back. A scene with
    /// unsaved changes in the Editor is not reloaded (that would lose them); it is logged instead.
    /// </remarks>
    internal class ExternalSceneReloader : AssetPostprocessor
    {
        private static readonly Dictionary<string, DateTime> s_SavedByEditor = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        /// <summary>True when Unity answers the reload prompt by itself with Ignore: no person at the keyboard.</summary>
        internal static bool UnityIgnoresExternalChanges => !InternalEditorUtility.isHumanControllingUs && !Application.isBatchMode;

        private static void OnSceneSaved(Scene scene) => s_SavedByEditor[scene.path] = DateTime.UtcNow;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!UnityIgnoresExternalChanges || !AgentMode.IsActive) return;
            foreach (string path in imported)
            {
                if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                if (s_SavedByEditor.TryGetValue(path, out DateTime saved) && (DateTime.UtcNow - saved).TotalSeconds < 5) continue;
                Scene scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (s_Pending.Count == 0) EditorDeferred.RunWhenIdle(ReloadPending, label: "Reload scenes changed on disk");
                s_Pending.Add(path);
            }
        }

        // Deferred until the import is over: opening scenes from inside an import is not allowed. (EditorDeferred, not
        // delayCall, which can wait a long time in an unfocused Editor.)
        private static void ReloadPending()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { s_Pending.Clear(); return; }

            string activePath = SceneManager.GetActiveScene().path;
            foreach (string path in s_Pending.ToList())
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (scene.isDirty)
                {
                    Debug.LogWarning($"[CLI Extensions] {path} changed on disk but has unsaved changes in the Editor, so it was not reloaded. " +
                                     "Saving it now would overwrite the file on disk; reopen it to take the file's version.");
                    DialogJournal.Write(new DialogJournalEntry { kind = "escalated", title = "Scene changed on disk", message = path, detail = "unsaved changes in the Editor; not reloaded" });
                    continue;
                }

                if (SceneManager.sceneCount == 1)
                {
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.CloseScene(scene, true);
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                }
                DialogJournal.Write(new DialogJournalEntry { kind = "answered", title = "The open scene(s) have been modified externally", message = path, answer = "Reload", detail = nameof(ExternalSceneReloader) });
                Debug.Log($"[CLI Extensions] Reloaded {path}, which changed on disk.");
            }
            s_Pending.Clear();

            Scene active = SceneManager.GetSceneByPath(activePath);
            if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
        }
    }
}

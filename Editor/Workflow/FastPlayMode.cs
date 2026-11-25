using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Enter Play Mode Options with domain and scene reload off. Entering Play mode then skips reloading every assembly
    /// and reloading the scene, which is most of its time in a large project. The cost: static
    /// fields keep their values and static events their subscribers between Play sessions. See <see cref="StaticStateScanner"/>.
    /// </summary>
    public static class FastPlayMode
    {
        /// <summary>The options fast Play mode sets.</summary>
        public const EnterPlayModeOptions Options = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

        [Serializable]
        private class SavedOptions
        {
            public bool enabled;
            public int options;
        }

        // What the project had before fast Play mode was turned on, so turning it off puts that back.
        private static string SavedFile => Path.Combine(CliExtPaths.StateDirectory, "playmode-before-fast.json");

        /// <summary>True when Enter Play Mode Options are on with both reloads off.</summary>
        public static bool IsEnabled => EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & Options) == Options;

        /// <summary>
        /// Turns fast Play mode on, or off. Turning it on remembers the project's own Enter Play Mode settings; turning it
        /// off puts those back, or Unity's default (reload both) when nothing was remembered. Turning it off when it isn't
        /// on leaves the settings alone. Saved in
        /// ProjectSettings/EditorSettings.asset.
        /// </summary>
        /// <returns>For <paramref name="enabled"/> false: true when the remembered settings were restored.</returns>
        public static bool SetEnabled(bool enabled)
        {
            bool restored = false;
            if (enabled)
            {
                if (!IsEnabled) Remember();
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = Options;
            }
            else if (TryRecall(out SavedOptions saved))
            {
                EditorSettings.enterPlayModeOptionsEnabled = saved.enabled;
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)saved.options;
                restored = true;
            }
            else if (IsEnabled)
            {
                // Fast Play mode was turned on some other way, and nothing was remembered: Unity's default.
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
            }
            if (!enabled) Forget();
            AssetDatabase.SaveAssets();
            return restored;
        }

        private static void Remember()
        {
            try
            {
                var saved = new SavedOptions { enabled = EditorSettings.enterPlayModeOptionsEnabled, options = (int)EditorSettings.enterPlayModeOptions };
                File.WriteAllText(SavedFile, JsonUtility.ToJson(saved));
            }
            catch (IOException e) { Debug.LogWarning($"[CLI Extensions] Could not write {SavedFile}: {e.Message}"); }
        }

        private static bool TryRecall(out SavedOptions saved)
        {
            saved = null;
            try
            {
                if (File.Exists(SavedFile)) saved = JsonUtility.FromJson<SavedOptions>(File.ReadAllText(SavedFile));
            }
            catch (Exception e) { Debug.LogWarning($"[CLI Extensions] Could not read {SavedFile}: {e.Message}"); }
            return saved != null;
        }

        private static void Forget()
        {
            try { if (File.Exists(SavedFile)) File.Delete(SavedFile); }
            catch (IOException) { }
        }
    }
}

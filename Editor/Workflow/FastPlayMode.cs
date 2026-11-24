using UnityEditor;

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

        /// <summary>True when Enter Play Mode Options are on with both reloads off.</summary>
        public static bool IsEnabled => EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & Options) == Options;

        /// <summary>Turns fast Play mode on, or puts Unity's default (reload both) back. Saved in ProjectSettings/EditorSettings.asset.</summary>
        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = Options;
            }
            else
            {
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
            }
            AssetDatabase.SaveAssets();
        }
    }
}

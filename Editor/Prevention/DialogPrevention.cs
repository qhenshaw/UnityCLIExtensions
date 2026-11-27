using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCLIExtensions
{
    /// <summary>
    /// What agent mode changes while the Editor runs, so fewer dialogs come up at all:
    /// <list type="bullet">
    /// <item>Saves the open scenes (those with a file) when entering Play mode, so a later scene switch, build or quit has nothing to prompt about.</item>
    /// <item>Turns <see cref="Application.runInBackground"/> on for the Play session and restores it afterwards, so Play mode keeps ticking while the Editor is unfocused.</item>
    /// <item>Pre-answers dialogs that offer "don't ask again" with their safe choice (see <see cref="KnownOptOuts"/>).</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    public static class DialogPrevention
    {
        private const string RunInBackgroundKey = "CLIExtensions.RunInBackgroundOriginal";

        /// <summary>
        /// Dialogs Unity and its packages open through <c>DisplayDialog</c> with an opt-out key, and the answer agent mode
        /// gives them for the session: (key, answer). The Input System's "native platform backend not enabled" prompt would
        /// otherwise restart the Editor; the setup check <c>input-handling</c> fixes the cause.
        /// </summary>
        public static readonly (string key, bool answer)[] KnownOptOuts =
        {
            ("RestartInstalledInputHandlingWarning", false),
        };

        static DialogPrevention()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AgentMode.Changed -= OnAgentModeChanged;
            AgentMode.Changed += OnAgentModeChanged;
            if (AgentMode.IsActive) ApplySessionOptOuts();
            RestoreRunInBackgroundIfLeftOver();
        }

        /// <summary>Sets the session opt-out decisions in <see cref="KnownOptOuts"/>.</summary>
        public static void ApplySessionOptOuts()
        {
            foreach (var (key, answer) in KnownOptOuts)
                EditorUtility.SetDialogOptOutDecision(DialogOptOutDecisionType.ForThisSession, key, answer);
        }

        /// <summary>Saves every open, dirty scene that has a file. Untitled scenes are left alone (saving them would ask for a path).</summary>
        public static int SaveOpenScenesWithPaths() => SaveOpenScenes(null, false, out _).Count;

        /// <summary>
        /// Saves the open, dirty scenes that have a file (only <paramref name="onlyPath"/> when given) and returns their
        /// paths. With <paramref name="dryRun"/> it saves nothing and returns what it would save.
        /// <paramref name="untitled"/> counts dirty scenes skipped for having no file.
        /// </summary>
        public static System.Collections.Generic.List<string> SaveOpenScenes(string onlyPath, bool dryRun, out int untitled)
        {
            var paths = new System.Collections.Generic.List<string>();
            untitled = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.isDirty) continue;
                if (string.IsNullOrEmpty(scene.path)) { if (onlyPath == null) untitled++; continue; }
                if (onlyPath != null && !SamePath(scene.path, onlyPath)) continue;
                if (dryRun || EditorSceneManager.SaveScene(scene)) paths.Add(scene.path);
            }
            return paths;
        }

        /// <summary>The open scenes' paths (Untitled for one without a file).</summary>
        public static System.Collections.Generic.List<string> OpenScenePaths()
        {
            var paths = new System.Collections.Generic.List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) paths.Add(string.IsNullOrEmpty(scene.path) ? "Untitled" : scene.path);
            }
            return paths;
        }

        internal static bool SamePath(string a, string b) =>
            string.Equals((a ?? "").Replace('\\', '/').Trim(), (b ?? "").Replace('\\', '/').Trim(), System.StringComparison.OrdinalIgnoreCase);

        private static void OnAgentModeChanged(bool active)
        {
            if (active) ApplySessionOptOuts();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            var settings = CliExtSettings.instance;
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    if (AgentMode.IsActive && settings.SaveScenesBeforePlay)
                    {
                        int saved = SaveOpenScenesWithPaths();
                        if (saved > 0) Debug.Log($"[CLI Extensions] Agent mode saved {saved} scene(s) before Play mode.");
                    }
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    if (AgentMode.IsActive && settings.RunInBackgroundInPlayMode && !Application.runInBackground)
                    {
                        SessionState.SetInt(RunInBackgroundKey, 1); // 1 = we turned it on; the project's value is off
                        Application.runInBackground = true;
                    }
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    RestoreRunInBackgroundIfLeftOver();
                    break;
            }
        }

        // Puts the project's own value (off) back after a Play session, including one cut short by a domain reload.
        private static void RestoreRunInBackgroundIfLeftOver()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SessionState.GetInt(RunInBackgroundKey, 0) != 1) return;
            Application.runInBackground = false;
            SessionState.EraseInt(RunInBackgroundKey);
        }
    }
}

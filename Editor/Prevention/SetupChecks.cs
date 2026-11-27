using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>The result of one setup check.</summary>
    [Serializable]
    public class SetupCheckResult
    {
        public string id;
        public string title;
        /// <summary><c>ok</c>, <c>fix-available</c>, <c>manual</c> (needs a person or a different launch), or <c>not-applicable</c>.</summary>
        public string status;
        public string detail;
        /// <summary>True when the check is only applied when asked for by id (it changes how the project behaves).</summary>
        public bool optional;
        public bool needsRestart;
    }

    /// <summary>
    /// One-time project setup that removes the cause of a dialog before it can block an agent: TMP Essentials, the Input
    /// System's input handling, the HDRP wizard popup, the Editor's interaction mode, and the launch flags.
    /// Run them with <c>unity command cliext.setup check</c> / <c>apply</c>, or Tools > CLI Extensions > Run Setup.
    /// </summary>
    public static class SetupChecks
    {
        /// <summary>A check: <c>Check</c> returns (status, detail); <c>Fix</c> returns what it did.</summary>
        public sealed class Check
        {
            public string Id;
            public string Title;
            public bool Optional;
            public bool NeedsRestart;
            /// <summary>The fix finishes after Apply returns (an asset import), so the check still fails right after it.</summary>
            public bool FinishesLater;
            public Func<(string status, string detail)> Run;
            public Func<string> Fix;
        }

        public const string Ok = "ok", FixAvailable = "fix-available", Manual = "manual", NotApplicable = "not-applicable";

        /// <summary>Every check, in the order they run.</summary>
        public static readonly IReadOnlyList<Check> All = new List<Check>
        {
            new Check
            {
                Id = "launch-flags",
                Title = "Editor started with the agent launch flags",
                Run = CheckLaunchFlags,
            },
            new Check
            {
                Id = "tmp-essentials",
                Title = "TextMesh Pro Essential Resources imported",
                FinishesLater = true,
                Run = CheckTmpEssentials,
                Fix = FixTmpEssentials,
            },
            new Check
            {
                Id = "input-handling",
                Title = "Active Input Handling enables the Input System package",
                NeedsRestart = true,
                Run = CheckInputHandling,
                Fix = FixInputHandling,
            },
            new Check
            {
                Id = "hdrp-wizard",
                Title = "HDRP Wizard does not open on startup",
                Run = CheckHdrpWizard,
                Fix = FixHdrpWizard,
            },
            new Check
            {
                Id = "interaction-mode",
                Title = "Editor interaction mode is No Throttling (the Editor keeps updating while unfocused)",
                Run = CheckInteractionMode,
                Fix = FixInteractionMode,
            },
            new Check
            {
                Id = "enter-play-mode-options",
                Title = "Enter Play Mode Options skip the domain and scene reload (faster Play mode)",
                Optional = true,
                Run = CheckEnterPlayMode,
                Fix = FixEnterPlayMode,
            },
        };

        /// <summary>Runs every check (or the ones in <paramref name="ids"/>) without changing anything.</summary>
        public static List<SetupCheckResult> RunAll(IEnumerable<string> ids = null)
        {
            return Select(ids).Select(Evaluate).ToList();
        }

        /// <summary>
        /// Fixes what the checks can fix. Without <paramref name="ids"/> it applies every non-optional check; optional ones
        /// run only when named. Returns the results after fixing, with what each fix did in <c>detail</c>.
        /// </summary>
        public static List<SetupCheckResult> Apply(IEnumerable<string> ids = null)
        {
            var named = ids?.ToList();
            var results = new List<SetupCheckResult>();
            foreach (Check check in Select(named))
            {
                if (check.Optional && (named == null || named.Count == 0)) { results.Add(Evaluate(check)); continue; }
                SetupCheckResult before = Evaluate(check);
                if (before.status != FixAvailable || check.Fix == null) { results.Add(before); continue; }
                string did;
                try { did = check.Fix(); }
                catch (Exception e) { did = "Fix failed: " + e.Message; }
                SetupCheckResult after = Evaluate(check);
                after.detail = did + (after.status == Ok || check.FinishesLater ? "" : " Now: " + after.detail);
                results.Add(after);
            }
            AssetDatabase.SaveAssets();
            return results;
        }

        private static IEnumerable<Check> Select(IEnumerable<string> ids)
        {
            var wanted = ids?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (wanted == null || wanted.Count == 0) return All;
            var unknown = wanted.Where(id => All.All(c => c.Id != id)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown setup check(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", All.Select(c => c.Id))}");
            return All.Where(c => wanted.Contains(c.Id));
        }

        private static SetupCheckResult Evaluate(Check check)
        {
            (string status, string detail) r;
            try { r = check.Run(); }
            catch (Exception e) { r = (Manual, "Check failed: " + e.Message); }
            return new SetupCheckResult { id = check.Id, title = check.Title, status = r.status, detail = r.detail, optional = check.Optional, needsRestart = check.NeedsRestart };
        }

        // ---------------------------------------------------------------------------------------------------------
        // launch-flags

        /// <summary>The Editor arguments that skip startup dialogs, and what each one prevents.</summary>
        public static readonly (string flag, string prevents)[] LaunchFlags =
        {
            ("-automated", "every EditorUtility.DisplayDialog, DisplayDialogComplex and file picker: they return Cancel at once instead of blocking (agent mode answers AgentDialog calls itself, and reloads scenes changed on disk, which Unity would otherwise ignore)"),
            ("-accept-apiupdate", "the Script Updating Consent (API Updater) prompt; scripts using obsolete APIs are updated"),
            ("-skipUpgradeDialogs", "the project upgrade / non-matching Editor version prompts"),
        };

        private static (string, string) CheckLaunchFlags()
        {
            string[] args = Environment.GetCommandLineArgs();
            var missing = LaunchFlags.Where(f => !args.Any(a => string.Equals(a, f.flag, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count == 0) return (Ok, "Started with " + string.Join(" ", LaunchFlags.Select(f => f.flag)) + ".");
            return (Manual, "Not started with " + string.Join(", ", missing.Select(m => $"{m.flag} (prevents {m.prevents})")) +
                            ". Open the project with: unity open <project> --accept-upgrades --args \"-automated -accept-apiupdate\"");
        }

        // ---------------------------------------------------------------------------------------------------------
        // tmp-essentials

        private static string TmpEssentialsPackagePath()
        {
            var ugui = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.ugui");
            if (ugui == null) return null;
            string path = Path.Combine(ugui.resolvedPath, "Package Resources", "TMP Essential Resources.unitypackage");
            return File.Exists(path) ? path : null;
        }

        private static bool TmpSettingsExist()
        {
            return AssetDatabase.FindAssets("t:TMP_Settings").Length > 0
                   || AssetDatabase.FindAssets("\"TMP Settings\"").Any(g => AssetDatabase.GUIDToAssetPath(g).EndsWith("/TMP Settings.asset"));
        }

        private static (string, string) CheckTmpEssentials()
        {
            string package = TmpEssentialsPackagePath();
            if (package == null) return (NotApplicable, "No com.unity.ugui 2.x (TextMesh Pro) in the project.");
            if (TmpSettingsExist()) return (Ok, "TMP Settings found.");
            return (FixAvailable, "TMP Essential Resources are not imported; the TMP Importer window opens the first time a TextMeshPro component is created.");
        }

        private static string FixTmpEssentials()
        {
#if UNITY_6000_6_OR_NEWER
            UnityEditor.AssetPackage.Package.Import(TmpEssentialsPackagePath(), false);
#else
            AssetDatabase.ImportPackage(TmpEssentialsPackagePath(), false);
#endif
            return "Importing TMP Essential Resources without the Import dialog; the import finishes a moment after this returns.";
        }

        // ---------------------------------------------------------------------------------------------------------
        // input-handling

        private static SerializedProperty ActiveInputHandler(out SerializedObject so)
        {
            so = null;
            var playerSettings = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
            if (playerSettings == null) return null;
            so = new SerializedObject(playerSettings);
            return so.FindProperty("activeInputHandler");
        }

        private static bool InputSystemInstalled() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.inputsystem") != null;

        private static (string, string) CheckInputHandling()
        {
            if (!InputSystemInstalled()) return (NotApplicable, "The Input System package is not installed.");
            var property = ActiveInputHandler(out _);
            if (property == null) return (Manual, "Could not read Active Input Handling from Player Settings.");
            return property.intValue switch
            {
                1 => (Ok, "Input System Package (New)."),
                2 => (Ok, "Both."),
                _ => (FixAvailable, "Input Manager (Old) only: the Input System asks to enable its backends and restart the Editor on every start."),
            };
        }

        private static string FixInputHandling()
        {
            var property = ActiveInputHandler(out SerializedObject so);
            property.intValue = 2; // Both: the Input System works and legacy UnityEngine.Input code keeps working.
            so.ApplyModifiedProperties();
            return "Set Active Input Handling to Both. Restart the Editor for it to take effect.";
        }

        // ---------------------------------------------------------------------------------------------------------
        // hdrp-wizard

        private static Type HdUserSettingsType() =>
            Type.GetType("UnityEditor.Rendering.HighDefinition.HDUserSettings, Unity.RenderPipelines.HighDefinition.Editor");

        private static object HdUserSettingsInstance(Type type) =>
            type.BaseType?.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)?.GetValue(null);

        private static FieldInfo WizardField(Type type) =>
            type.GetField("m_WizardPopupAtStart", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static (string, string) CheckHdrpWizard()
        {
            Type type = HdUserSettingsType();
            if (type == null) return (NotApplicable, "HDRP is not installed.");
            object instance = HdUserSettingsInstance(type);
            FieldInfo field = WizardField(type);
            if (instance == null || field == null) return (Manual, "This HDRP version keeps the wizard setting somewhere this check does not know.");
            return (bool)field.GetValue(instance)
                ? (FixAvailable, "The HDRP Wizard opens on every Editor start (Window > Rendering > HDRP Wizard > Show on start).")
                : (Ok, "The HDRP Wizard does not open on start.");
        }

        private static string FixHdrpWizard()
        {
            Type type = HdUserSettingsType();
            object instance = HdUserSettingsInstance(type);
            WizardField(type).SetValue(instance, false);
            type.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null)?.Invoke(instance, null);
            return "Turned off the HDRP Wizard's Show on start (UserSettings/HDRPUserSettings.asset).";
        }

        // ---------------------------------------------------------------------------------------------------------
        // interaction-mode

        /// <summary>The EditorPrefs key behind Preferences > General > Interaction Mode.</summary>
        internal const string InteractionModeKey = "InteractionMode";

        /// <summary>The EditorPrefs value for No Throttling.</summary>
        internal const int InteractionModeNoThrottling = 1;

        private static (string, string) CheckInteractionMode()
        {
            int mode = EditorPrefs.GetInt(InteractionModeKey, 0);
            return mode == InteractionModeNoThrottling
                ? (Ok, "No Throttling.")
                : (FixAvailable, $"Interaction Mode is {mode} (0 = Default): an unfocused Editor updates slowly, so commands and Play mode lag while the agent's terminal has focus.");
        }

        private static string FixInteractionMode()
        {
            EditorPrefs.SetInt(InteractionModeKey, InteractionModeNoThrottling);
            // The Preferences window applies the change with this internal call; without it the Editor reads it on restart.
            var apply = typeof(EditorApplication).GetMethod("UpdateInteractionModeSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            apply?.Invoke(null, null);
            return "Set Preferences > General > Interaction Mode to No Throttling (an Editor preference shared by every project on this machine)" +
                   (apply == null ? "; it takes effect after an Editor restart." : ".");
        }

        // ---------------------------------------------------------------------------------------------------------
        // enter-play-mode-options

        private static string StaticsSummary()
        {
            int unreset = StaticStateScanner.Scan().Count(f => !f.hasResetHook);
            return unreset == 0
                ? "No static state without a [RuntimeInitializeOnLoadMethod] reset was found in the project's runtime code."
                : $"{unreset} static member(s) in the project's runtime code have no [RuntimeInitializeOnLoadMethod] reset and would keep their values between Play sessions (cliext.playmode check_statics lists them).";
        }

        private static (string, string) CheckEnterPlayMode()
        {
            if (FastPlayMode.IsEnabled) return (Ok, "Domain and scene reload are skipped. " + StaticsSummary());
            return (FixAvailable, "Entering Play mode reloads the domain and scene. Skipping them is much faster, but static fields keep their values between Play sessions. " + StaticsSummary());
        }

        private static string FixEnterPlayMode()
        {
            UnityCLIExtensions.FastPlayMode.SetEnabled(true);
            return "Enter Play Mode Options: Disable Domain Reload and Disable Scene Reload (Project Settings > Editor). Undo with cliext.playmode set_fast --enabled false.";
        }
    }
}

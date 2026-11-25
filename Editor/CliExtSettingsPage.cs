using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>Project Settings > CLI Extensions, and the Tools > CLI Extensions menu.</summary>
    internal static class CliExtSettingsPage
    {
        internal const string Path = "Project/CLI Extensions";
        private const string Menu = "Tools/CLI Extensions/";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.Project)
            {
                label = "CLI Extensions",
                guiHandler = _ => Draw(),
                keywords = new[] { "Agent", "CLI", "Pipeline", "Dialog", "Modal", "Watchdog", "Headless", "Automation" }
            };
        }

        private static void Draw()
        {
            var settings = CliExtSettings.instance;
            var so = new SerializedObject(settings);

            EditorGUILayout.HelpBox("Keeps an Editor driven from the command line (unity command) free of blocking dialogs. " +
                                    "These settings are yours, not the project's: they are saved in UserSettings/.", MessageType.Info);

            string reason = AgentMode.Reason;
            EditorGUILayout.LabelField("Agent mode", reason == null ? "Off" : $"On ({reason})", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty("_agentMode"), new GUIContent("Agent Mode (this project)"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("In agent mode", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty("_answerProjectDialogs"), new GUIContent("Answer AgentDialog Calls"));
            EditorGUILayout.PropertyField(so.FindProperty("_saveScenesBeforePlay"), new GUIContent("Save Scenes Before Play"));
            EditorGUILayout.PropertyField(so.FindProperty("_runInBackgroundInPlayMode"), new GUIContent("Run In Background In Play"));
            EditorGUILayout.PropertyField(so.FindProperty("_startWatchdog"), new GUIContent("Start Watchdog"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Watchdog", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty("_watchdogAutoClick"), new GUIContent("Press Safe Defaults"));
            EditorGUILayout.PropertyField(so.FindProperty("_scenePromptAnswer"), new GUIContent("Scene Save Prompt"));
            EditorGUILayout.PropertyField(so.FindProperty("_watchdogRestartOnCrash"), new GUIContent("Reopen After Crash"));
            EditorGUILayout.PropertyField(so.FindProperty("_watchdogKillAfterHungSeconds"), new GUIContent("Kill When Hung (s)"));
            EditorGUILayout.PropertyField(so.FindProperty("_watchdogIntervalSeconds"), new GUIContent("Check Interval (s)"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Agent files", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty("_manageAgentFiles"), new GUIContent("Keep Skill and CLAUDE.md Current"));
            if (GUILayout.Button("Update Agent Files Now", GUILayout.Width(200))) Debug.Log("[CLI Extensions] " + string.Join("; ", AgentSetup.Install(force: true).changes));

            bool wasActive = AgentMode.IsActive;
            if (so.ApplyModifiedProperties())
            {
                settings.SaveSettings();
                if (AgentMode.IsActive != wasActive && AgentMode.IsActive) DialogPrevention.ApplySessionOptOuts();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(Watchdog.IsRunning ? $"Watchdog running (pid {Watchdog.RunningPid})" : "Watchdog not running");
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!Watchdog.IsSupported))
                {
                    if (GUILayout.Button(Watchdog.IsRunning ? "Stop" : "Start", GUILayout.Width(80)))
                        Debug.Log("[CLI Extensions] " + (Watchdog.IsRunning ? Watchdog.Stop() : Watchdog.Start()));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
            foreach (var result in SetupChecks.RunAll())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string icon = result.status switch
                    {
                        SetupChecks.Ok => "TestPassed",
                        SetupChecks.NotApplicable => "TestIgnored",
                        SetupChecks.FixAvailable => "console.warnicon.sml",
                        _ => "console.infoicon.sml"
                    };
                    GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20));
                    GUILayout.Label(new GUIContent(result.title + (result.optional ? " (optional)" : ""), result.detail), GUILayout.ExpandWidth(true));
                    using (new EditorGUI.DisabledScope(result.status != SetupChecks.FixAvailable))
                    {
                        if (GUILayout.Button("Fix", GUILayout.Width(50)))
                            Debug.Log("[CLI Extensions] " + SetupChecks.Apply(new[] { result.id }).First().detail);
                    }
                }
            }
        }

        [MenuItem(Menu + "Settings", priority = 0)]
        private static void OpenSettings() => SettingsService.OpenProjectSettings(Path);

        [MenuItem(Menu + "Run Setup", priority = 20)]
        private static void RunSetup()
        {
            foreach (var r in SetupChecks.Apply())
                Debug.Log($"[CLI Extensions] {r.id}: {r.status}. {r.detail}");
        }

        [MenuItem(Menu + "Start Watchdog", priority = 40)]
        private static void StartWatchdog() => Debug.Log("[CLI Extensions] " + Watchdog.Start());

        [MenuItem(Menu + "Start Watchdog", true)]
        private static bool CanStartWatchdog() => Watchdog.IsSupported && !Watchdog.IsRunning;

        [MenuItem(Menu + "Stop Watchdog", priority = 41)]
        private static void StopWatchdog() => Debug.Log("[CLI Extensions] " + Watchdog.Stop());

        [MenuItem(Menu + "Stop Watchdog", true)]
        private static bool CanStopWatchdog() => Watchdog.IsRunning;

        [MenuItem(Menu + "Scan Scripts for Blocking Dialogs", priority = 60)]
        private static void Scan()
        {
            var calls = DialogScanner.Scan();
            if (calls.Count == 0) Debug.Log("[CLI Extensions] No blocking dialog calls found in the project's scripts.");
            foreach (var c in calls)
                Debug.Log($"[CLI Extensions] {c.path}({c.line}): {c.api}. Use {c.suggestion}.\n{c.code}");
        }
    }
}

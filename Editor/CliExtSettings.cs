using System;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>What the watchdog presses on a "Scene(s) Have Been Modified" save prompt.</summary>
    public enum ScenePromptAnswer
    {
        /// <summary>Leave the dialog open and report it, for the agent or a person to decide.</summary>
        Escalate,
        /// <summary>Press Save.</summary>
        Save,
        /// <summary>Press Don't Save, discarding the unsaved changes.</summary>
        DontSave
    }

    /// <summary>
    /// Per-user, per-project settings, kept in <c>UserSettings/CLIExtensionsSettings.asset</c> (git-ignored by Unity's
    /// default .gitignore), because whether an agent drives this Editor is a fact about this machine, not the project.
    /// </summary>
    [FilePath(FilePathInProject, FilePathAttribute.Location.ProjectFolder)]
    public class CliExtSettings : ScriptableSingleton<CliExtSettings>
    {
        internal const string FilePathInProject = "UserSettings/CLIExtensionsSettings.asset";

        [Tooltip("Turn agent mode on for this project. It is also on when the Editor was started with -cliext-agent or " +
                 "-automated, or with the CLIEXT_AGENT=1 environment variable.")]
        [SerializeField] private bool _agentMode;

        [Tooltip("In agent mode, AgentDialog calls return their headless answer instead of opening a dialog, and log it.")]
        [SerializeField] private bool _answerProjectDialogs = true;

        [Tooltip("In agent mode, save the open scenes (those that have a file) when entering Play mode, so a later scene " +
                 "switch, build or quit has nothing to prompt about.")]
        [SerializeField] private bool _saveScenesBeforePlay = true;

        [Tooltip("In agent mode, keep the game running in Play mode while the Editor is unfocused (Application.runInBackground " +
                 "for the Play session only; the Player Settings value is not changed).")]
        [SerializeField] private bool _runInBackgroundInPlayMode = true;

        [Tooltip("In agent mode, start the out-of-process watchdog with the Editor.")]
        [SerializeField] private bool _startWatchdog = true;

        [Tooltip("Let the watchdog press the safe default on dialogs it knows (see the policy). Off: it only reports them.")]
        [SerializeField] private bool _watchdogAutoClick = true;

        [Tooltip("What the watchdog presses on a scene save prompt.")]
        [SerializeField] private ScenePromptAnswer _scenePromptAnswer = ScenePromptAnswer.Escalate;

        [Tooltip("Reopen the project with 'unity open' when the Editor process ends without quitting normally (a crash).")]
        [SerializeField] private bool _watchdogRestartOnCrash;

        [Tooltip("Kill and reopen the Editor when its window has not responded for this many seconds and no dialog is open. " +
                 "0 = never. Unsaved work is lost, so keep this high (600+) if you use it.")]
        [SerializeField] private int _watchdogKillAfterHungSeconds;

        [Tooltip("Seconds between watchdog checks.")]
        [SerializeField] private float _watchdogIntervalSeconds = 2f;

        [Tooltip("When the package is installed or updated, copy its unity-cliext skill into .claude/skills/ and keep a marked " +
                 "block in the project's CLAUDE.md pointing agents at it. Your other CLAUDE.md content is never touched.")]
        [SerializeField] private bool _manageAgentFiles = true;

        public bool AgentModeSetting { get => _agentMode; set => Set(ref _agentMode, value); }
        public bool AnswerProjectDialogs { get => _answerProjectDialogs; set => Set(ref _answerProjectDialogs, value); }
        public bool SaveScenesBeforePlay { get => _saveScenesBeforePlay; set => Set(ref _saveScenesBeforePlay, value); }
        public bool RunInBackgroundInPlayMode { get => _runInBackgroundInPlayMode; set => Set(ref _runInBackgroundInPlayMode, value); }
        public bool StartWatchdog { get => _startWatchdog; set => Set(ref _startWatchdog, value); }
        public bool WatchdogAutoClick { get => _watchdogAutoClick; set => Set(ref _watchdogAutoClick, value); }
        public ScenePromptAnswer ScenePromptAnswer { get => _scenePromptAnswer; set => Set(ref _scenePromptAnswer, value); }
        public bool WatchdogRestartOnCrash { get => _watchdogRestartOnCrash; set => Set(ref _watchdogRestartOnCrash, value); }
        public int WatchdogKillAfterHungSeconds { get => _watchdogKillAfterHungSeconds; set => Set(ref _watchdogKillAfterHungSeconds, Mathf.Max(0, value)); }
        public float WatchdogIntervalSeconds { get => _watchdogIntervalSeconds; set => Set(ref _watchdogIntervalSeconds, Mathf.Clamp(value, 0.5f, 60f)); }
        public bool ManageAgentFiles { get => _manageAgentFiles; set => Set(ref _manageAgentFiles, value); }

        /// <summary>Raised after any setting changes and is saved.</summary>
        public static event Action Changed;

        private void OnEnable()
        {
            // ScriptableSingleton instances are not editable by default; the settings page needs to edit it.
            hideFlags &= ~HideFlags.NotEditable;
        }

        private void Set<T>(ref T field, T value)
        {
            if (Equals(field, value)) return;
            field = value;
            SaveSettings();
        }

        internal void SaveSettings()
        {
            Save(true);
            Changed?.Invoke();
        }
    }
}

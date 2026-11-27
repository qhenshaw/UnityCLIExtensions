using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>One watchdog rule: which dialog it matches and what to do with it.</summary>
    [Serializable]
    public class WatchdogRule
    {
        /// <summary>Shown in the journal and status as the reason for an action.</summary>
        public string name;
        /// <summary>Regex on the dialog's window title (case-insensitive, as PowerShell's -match).</summary>
        public string title;
        /// <summary>Optional regex on the dialog's message text.</summary>
        public string message;
        /// <summary><c>click</c> (press <see cref="button"/>), <c>close</c> (cancel the window), <c>focus</c> (bring a Unity-drawn modal to the front, so the
        /// Editor's own handler answers it), <c>wait</c> (a progress window: do nothing), or <c>escalate</c> (report it and leave it open).</summary>
        public string action;
        /// <summary>For <c>click</c>: regex on the button label, mnemonic '&amp;' removed.</summary>
        public string button;

        public WatchdogRule() { }

        public WatchdogRule(string name, string title, string action, string button = null, string message = null)
        {
            this.name = name;
            this.title = title;
            this.action = action;
            this.button = button;
            this.message = message;
        }
    }

    /// <summary>
    /// The policy the watchdog reads from <c>Library/CLIExtensions/watchdog-policy.json</c> on every check. Rules are
    /// tried in order and the first match wins; a dialog no rule matches is escalated. Only dialogs whose safe answer
    /// changes nothing on disk, or that the settings opt into, are answered by default.
    /// </summary>
    [Serializable]
    public class WatchdogPolicy
    {
        public bool autoClick = true;
        public bool restartOnCrash;
        public int killAfterHungSeconds;
        public float intervalSeconds = 2f;
        public float staleHeartbeatSeconds = 20f;
        /// <summary>Extra Editor arguments when the watchdog reopens the project.</summary>
        public string relaunchArgs = "-automated -accept-apiupdate -skipUpgradeDialogs";
        public List<WatchdogRule> rules = new List<WatchdogRule>();

        /// <summary>The rule for Unity's "Missing signature" dialog.</summary>
        public const string MissingSignatureRule = "missing-signature";

        /// <summary>Rules for the dialogs Unity 6 is known to block on. See Documentation~/blocking-dialogs.md.</summary>
        public static List<WatchdogRule> DefaultRules(ScenePromptAnswer scenePrompt)
        {
            var rules = new List<WatchdogRule>
            {
                // Progress and busy windows go away by themselves; never press anything on them.
                new WatchdogRule("busy", "^(Hold on|Opening project\\.\\.\\.|Loading|Initializing|Compiling|Importing|Reloading|Progress|Unity is busy|Building|Baking|Package Manager$)", "wait"),

                // The file on disk is what git (or the agent) wrote; Ignore would keep a stale scene that overwrites it.
                new WatchdogRule("scene-modified-externally", "modified externally|changed on disk|have been changed externally", "click", "^Reload"),

                // Changing input handling needs a restart; the setup check fixes it properly. Don't Enable changes nothing.
                new WatchdogRule("input-system-backends", "Input System native platform backend", "click", "^Don't Enable"),

                // Ignore opens the Editor with the compile errors, so the agent can see and fix them. Safe Mode would not
                // load packages' editor code, including the Pipeline server.
                new WatchdogRule("safe-mode", "Enter Safe Mode", "click", "^Ignore"),

                // Addressables' first build asks whether to turn on its build report; Cancel leaves the setting as it is.
                new WatchdogRule("addressables-build-report", "^Enable Build Report$", "click", "^Cancel"),

                // A file picker opened by code waits for a path no agent can type; closing it is Cancel.
                new WatchdogRule("file-picker", "^(Open|Open File|Save|Save As|Save File|Select Folder|Choose Folder|Browse For Folder)$", "close"),

                // A UI Toolkit modal with no Win32 buttons. MissingSignatureAnswer presses Import Anyway from windowFocusChanged,
                // which Unity raises only when the dialog gets focus: one opened with the Editor in the background never does.
                new WatchdogRule(MissingSignatureRule, "^" + MissingSignatureAnswer.DialogTitle + "$", "focus"),

                // These change the project or its setup: leave them for a person.
                new WatchdogRule("api-updater", "Script Updating Consent|API Update", "escalate"),
                new WatchdogRule("package-manager-error", "Package Manager Error", "escalate"),
                new WatchdogRule("editor-version", "Non-Matching Editor|Opening Project in|Project Upgrade|Change Editor version", "escalate"),
                new WatchdogRule("licensing", "Licen[cs]e|Sign in|Unity Hub", "escalate"),
                new WatchdogRule("file-operation-failed", "Moving file failed|Deleting file failed|Copying file failed|Destination path", "escalate"),
            };

            // After scene-modified-externally: "The open scene(s) have been modified externally" also says "modified".
            const string sceneSaveTitle = "^Scene\\(s\\) Have Been Modified|^Save Scene|Unsaved Changes";
            rules.Insert(2, scenePrompt switch
            {
                ScenePromptAnswer.Save => new WatchdogRule("scene-save-prompt", sceneSaveTitle, "click", "^Save$"),
                ScenePromptAnswer.DontSave => new WatchdogRule("scene-save-prompt", sceneSaveTitle, "click", "^Don't Save"),
                _ => new WatchdogRule("scene-save-prompt", sceneSaveTitle, "escalate"),
            });
            return rules;
        }

        /// <summary>
        /// The first rule that matches a dialog, as the watchdog script matches it (case-insensitive regexes), or null
        /// when the dialog would be escalated.
        /// </summary>
        public WatchdogRule Match(string title, string message = "")
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            foreach (WatchdogRule rule in rules)
            {
                if (!string.IsNullOrEmpty(rule.title) && !Regex.IsMatch(title ?? "", rule.title, options)) continue;
                if (!string.IsNullOrEmpty(rule.message) && !Regex.IsMatch(message ?? "", rule.message, options)) continue;
                return rule;
            }
            return null;
        }

        /// <summary>The policy for the current settings.</summary>
        public static WatchdogPolicy FromSettings(CliExtSettings settings)
        {
            return new WatchdogPolicy
            {
                autoClick = settings.WatchdogAutoClick,
                restartOnCrash = settings.WatchdogRestartOnCrash,
                killAfterHungSeconds = settings.WatchdogKillAfterHungSeconds,
                intervalSeconds = settings.WatchdogIntervalSeconds,
                rules = DefaultRules(settings.ScenePromptAnswer)
            };
        }

        /// <summary>Writes the policy for the current settings where the watchdog reads it.</summary>
        public static void WriteForCurrentSettings()
        {
            File.WriteAllText(CliExtPaths.WatchdogPolicyFile, JsonUtility.ToJson(FromSettings(CliExtSettings.instance), true));
        }
    }
}

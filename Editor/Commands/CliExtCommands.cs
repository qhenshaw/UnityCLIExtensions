using System;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace UnityCLIExtensions
{
    /// <summary>
    /// The package's <c>unity command</c> commands. They are thin: the logic lives in the classes they call, so it can be
    /// used and tested without the Pipeline server. Commands that only read files run off the main thread
    /// (<c>MainThreadRequired = false</c>), so they still answer while a dialog blocks the Editor, as long as no other
    /// command is queued ahead of them.
    /// </summary>
    internal static class CliExtCommands
    {
        private const string Group = "cliext";
        private const string SetupGroup = "cliext.setup";
        private const string DialogsGroup = "cliext.dialogs";
        private const string WatchdogGroup = "cliext.watchdog";
        private const string PackageGroup = "cliext.package";
        private const string SceneGroup = "cliext.scene";

        // ---------------------------------------------------------------------------------------------------------
        // cliext

        [CliGroupCommand(Group, "Agent mode, its settings, the watchdog and the setup checks that still need fixing, in one call.", Tags = new[] { "editor", "cliext" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Status(CommandContext cmd)
        {
            var settings = CliExtSettings.instance;
            Heartbeat.Read(out double heartbeatAge);
            cmd.Result.Write(new
            {
                agentMode = AgentMode.IsActive,
                agentModeReason = AgentMode.Reason,
                settings = new
                {
                    answerProjectDialogs = settings.AnswerProjectDialogs,
                    saveScenesBeforePlay = settings.SaveScenesBeforePlay,
                    runInBackgroundInPlayMode = settings.RunInBackgroundInPlayMode,
                    startWatchdog = settings.StartWatchdog,
                    watchdogAutoClick = settings.WatchdogAutoClick,
                    scenePromptAnswer = settings.ScenePromptAnswer.ToString(),
                    watchdogRestartOnCrash = settings.WatchdogRestartOnCrash,
                    watchdogKillAfterHungSeconds = settings.WatchdogKillAfterHungSeconds,
                },
                watchdog = new { supported = Watchdog.IsSupported, running = Watchdog.IsRunning, pid = Watchdog.RunningPid },
                heartbeatAgeSeconds = double.IsInfinity(heartbeatAge) ? (double?)null : Math.Round(heartbeatAge, 1),
                setupToFix = SetupChecks.RunAll().Where(r => r.status == SetupChecks.FixAvailable || r.status == SetupChecks.Manual)
                    .Select(r => new { r.id, r.status, r.optional }).ToArray(),
                stateDirectory = CliExtPaths.StateDirectory,
                // Where the Tools~ scripts are (cliext-wait.ps1, cliext-run.ps1, cliext-test.ps1, cliext-play-capture.ps1, Watchdog/).
                tools = CliExtPaths.PackageRoot != null ? System.IO.Path.Combine(CliExtPaths.PackageRoot, "Tools~") : null,
                version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CliExtPaths).Assembly)?.version,
            });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Turn agent mode on or off for this project (saved in UserSettings/). The -cliext-agent flag and CLIEXT_AGENT=1 turn it on regardless.", Tags = new[] { "editor", "cliext" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode SetAgentMode(CommandContext cmd, [CliArg("enabled", "true to turn agent mode on, false to turn it off")] bool enabled = true)
        {
            AgentMode.SetEnabled(enabled);
            cmd.Result.Write(new { agentMode = AgentMode.IsActive, reason = AgentMode.Reason, setting = enabled });
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.setup

        [CliGroupCommand(SetupGroup, "Check the one-time project setup that prevents startup and first-use dialogs. Changes nothing.", Tags = new[] { "editor", "cliext", "settings" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Check(CommandContext cmd, [CliArg("ids", "Check ids separated by ';' (default: all)")] string ids = "")
        {
            System.Collections.Generic.List<SetupCheckResult> results;
            try { results = SetupChecks.RunAll(SplitIds(ids)); }
            catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.InvalidUsage; }
            foreach (var result in results)
                cmd.Result.WriteRecord(result);
            return ExitCode.Success;
        }

        [CliGroupCommand(SetupGroup, "Fix what the setup checks can fix. Without --ids it applies every check except the optional ones (enter-play-mode-options), which run only when named.", Tags = new[] { "editor", "cliext", "settings" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Apply(CommandContext cmd, [CliArg("ids", "Check ids separated by ';' (default: all non-optional)")] string ids = "")
        {
            System.Collections.Generic.List<SetupCheckResult> results;
            try { results = SetupChecks.Apply(SplitIds(ids)); }
            catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.InvalidUsage; }
            foreach (var result in results)
                cmd.Result.WriteRecord(result);
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.dialogs

        [CliGroupCommand(DialogsGroup, "The dialog journal: dialogs agent mode answered, and dialogs the watchdog saw, clicked or escalated. Oldest first.", Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Log(
            CommandContext cmd,
            [CliArg("tail", "Most recent entries to return")] int tail = 20,
            [CliArg("since", "Only entries at or after this UTC time (ISO 8601)")] string since = "")
        {
            DateTime? sinceUtc = null;
            if (!string.IsNullOrEmpty(since))
            {
                if (!DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed))
                {
                    cmd.Fail($"'{since}' is not a date and time. Use ISO 8601, e.g. 2026-11-24T16:00:00Z.");
                    return ExitCode.InvalidUsage;
                }
                sinceUtc = parsed;
            }
            foreach (var entry in DialogJournal.ReadLast(Math.Max(1, tail), sinceUtc))
                cmd.Result.WriteRecord(entry);
            return ExitCode.Success;
        }

        [CliGroupCommand(DialogsGroup, "Delete the dialog journal.", Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.Destructive)]
        internal static ExitCode ClearLog(CommandContext cmd)
        {
            DialogJournal.Clear();
            cmd.Result.Write(new { cleared = true });
            return ExitCode.Success;
        }

        [CliGroupCommand(DialogsGroup, "Find calls in the project's scripts (Assets/, embedded and local packages) that open a blocking dialog, file picker or progress bar, with what to use instead.", Tags = new[] { "editor", "cliext", "scripts" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Scan(CommandContext cmd, [CliArg("local-packages", "Also scan embedded and file: packages")] bool localPackages = true)
        {
            foreach (var call in DialogScanner.Scan(localPackages))
                cmd.Result.WriteRecord(call);
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.watchdog

        [CliGroupCommand(WatchdogGroup, "Start the out-of-process watchdog for this Editor (Windows). It reports modal dialogs and presses the safe default on the ones its policy knows.", Tags = new[] { "editor", "cliext" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Start(CommandContext cmd)
        {
            if (!Watchdog.IsSupported)
            {
                cmd.Fail("The watchdog only runs on Windows so far.");
                return ExitCode.Failed;
            }
            cmd.Result.Write(new { message = Watchdog.Start(), policy = CliExtPaths.WatchdogPolicyFile });
            return ExitCode.Success;
        }

        [CliGroupCommand(WatchdogGroup, "Ask the watchdog to exit.", Tags = new[] { "editor", "cliext" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Stop(CommandContext cmd)
        {
            cmd.Result.Write(new { message = Watchdog.Stop() });
            return ExitCode.Success;
        }

        [CliGroupCommand(WatchdogGroup, "The watchdog's last report: the Editor's state (ready, blocked-by-dialog, busy, hung) and any open dialogs with their buttons and the rule that applies. Reads a file, so it answers while a dialog blocks the main thread if no command is queued ahead of it. When even this times out, run Tools~/Watchdog/cliext-watchdog.ps1 directly.", Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Report(CommandContext cmd)
        {
            string json = Watchdog.ReadStatusJson(out double age);
            if (json == null)
            {
                cmd.Fail("The watchdog has not written a report for this project. Start it with: unity command cliext.watchdog start");
                return ExitCode.NotFound;
            }
            cmd.Result.Write(new
            {
                running = Watchdog.RunningPid != 0,
                ageSeconds = Math.Round(age, 1),
                report = Newtonsoft.Json.Linq.JToken.Parse(json)
            });
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.package, cliext.scene

        [CliGroupCommand(PackageGroup, "Import a .unitypackage without the Import dialog (every asset in it is imported).", Tags = new[] { "editor", "cliext", "assets/import" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Import(CommandContext cmd, [CliArg("path", "Path to the .unitypackage", Required = true)] string path)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                cmd.Fail($"No file at '{full}'.");
                return ExitCode.NotFound;
            }
            AssetDatabase.ImportPackage(full, false);
            cmd.Result.Write(new { imported = full });
            return ExitCode.Success;
        }

        [CliGroupCommand(SceneGroup, "Save open scenes that have unsaved changes and a file (every one, or only --scene), so tests, switching scenes, building or quitting have nothing to prompt about. " +
                                     "Overwrites each scene's file with its in-memory changes; --dry-run lists what would be saved and changes nothing. Untitled scenes are skipped. Returns the saved paths.",
            Tags = new[] { "editor", "cliext", "scenes" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SaveOpen(
            CommandContext cmd,
            [CliArg("scene", "Save only this open scene, by asset path (e.g. Assets/Scenes/Main.unity); default: every open scene with unsaved changes")] string scene = "",
            [CliArg("dry-run", "List the scenes that would be saved, and save nothing")] bool dryRun = false)
        {
            string only = string.IsNullOrWhiteSpace(scene) ? null : scene.Trim();
            var open = DialogPrevention.OpenScenePaths();
            if (only != null && !open.Any(p => DialogPrevention.SamePath(p, only)))
            {
                cmd.Result.Write(new { saved = new string[0], dryRun, open });
                cmd.Fail($"'{only}' is not an open scene. Open scenes: {string.Join(", ", open)}.");
                return ExitCode.NotFound;
            }
            var paths = DialogPrevention.SaveOpenScenes(only, dryRun, out int untitled);
            if (dryRun) cmd.Result.Write(new { dryRun, wouldSave = paths, untitledSkipped = untitled });
            else cmd.Result.Write(new { dryRun, saved = paths, untitledSkipped = untitled });
            return ExitCode.Success;
        }

        private static string[] SplitIds(string ids) =>
            string.IsNullOrWhiteSpace(ids) ? null : ids.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }
}

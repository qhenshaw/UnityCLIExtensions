using System;
using System.Linq;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Whether an agent, rather than a person, is driving this Editor. In agent mode the package answers the project's
    /// own dialogs, keeps Play mode running unfocused, saves scenes before Play and starts the watchdog.
    /// </summary>
    public static class AgentMode
    {
        /// <summary>Command-line flag that turns agent mode on: <c>unity open . --args "-cliext-agent"</c>.</summary>
        public const string CommandLineFlag = "-cliext-agent";

        /// <summary>Environment variable that turns agent mode on when set to 1 or true.</summary>
        public const string EnvironmentVariable = "CLIEXT_AGENT";

        private static readonly string s_ProcessReason = DetectFromProcess();

        /// <summary>True when agent mode is on, for any of the reasons in <see cref="Reason"/>.</summary>
        public static bool IsActive => Reason != null;

        /// <summary>Why agent mode is on ("setting", "command line ...", "environment ...", "batchmode"), or null when it is off.</summary>
        public static string Reason => CliExtSettings.instance.AgentModeSetting ? "setting" : s_ProcessReason;

        /// <summary>Raised when agent mode turns on or off through the setting.</summary>
        public static event Action<bool> Changed;

        /// <summary>Turns the per-project agent mode setting on or off. Flags and the environment variable still apply.</summary>
        public static void SetEnabled(bool enabled)
        {
            bool was = IsActive;
            CliExtSettings.instance.AgentModeSetting = enabled;
            if (IsActive != was) Changed?.Invoke(IsActive);
        }

        private static string DetectFromProcess()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, CommandLineFlag, StringComparison.OrdinalIgnoreCase))) return "command line " + CommandLineFlag;
            string env = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return "environment " + EnvironmentVariable;
            if (args.Any(a => string.Equals(a, "-automated", StringComparison.OrdinalIgnoreCase))) return "command line -automated";
            if (Application.isBatchMode) return "batchmode";
            return null;
        }
    }
}

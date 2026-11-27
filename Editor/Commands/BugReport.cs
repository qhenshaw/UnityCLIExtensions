using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Starts <c>Tools~/cliext-report.ps1</c>, which zips what the package author needs to act on a problem (the failure
    /// log, field notes, dialog journal, watchdog status, versions, Editor.log and transcript excerpts) into
    /// <c>CLIExtensionsReports/</c>. It runs out of process, so it also works from the command line when the Editor is
    /// hung or closed; from here it is started detached and the Editor doesn't wait for it.
    /// </summary>
    public static class BugReport
    {
        /// <summary>The folder the zips go in, at the project root (outside Assets/, so nothing is imported).</summary>
        public static string ReportsDirectory => Path.Combine(CliExtPaths.ProjectRoot, "CLIExtensionsReports");

        /// <summary>The report script in the package's Tools~ folder.</summary>
        public static string Script
        {
            get
            {
                string root = CliExtPaths.PackageRoot;
                return root == null ? null : Path.Combine(root, "Tools~", "cliext-report.ps1");
            }
        }

        /// <summary>
        /// Starts the script for the last <paramref name="days"/> days. Returns null and the zip it will write on success,
        /// or the reason it couldn't start.
        /// </summary>
        public static string Launch(int days, bool openFolder, out string zip)
        {
            zip = Path.Combine(ReportsDirectory, $"cliext-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            if (Application.platform != RuntimePlatform.WindowsEditor) return "The bug report script only runs on Windows so far.";
            string script = Script;
            if (script == null || !File.Exists(script)) return $"The bug report script is missing: {script}";

            // Shell-executed with a hidden window: the script gets its own console, so its output can't fill a pipe the
            // Editor never reads, and it doesn't wait on the Editor. It queries the Editor itself, after this returns.
            var info = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Project \"{CliExtPaths.ProjectRoot}\" " +
                $"-Days {days} -Zip \"{zip}\"" + (openFolder ? "" : " -NoExplorer"))
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using var process = Process.Start(info);
                return null;
            }
            catch (Exception e)
            {
                return $"Could not start the bug report script: {e.Message}";
            }
        }

        [MenuItem("Tools/CLI Extensions/Create Bug Report", priority = 22)]
        private static void MenuCreate()
        {
            string error = Launch(14, true, out string zip);
            if (error != null) Debug.LogWarning($"[CLI Extensions] {error}");
            else Debug.Log($"[CLI Extensions] Building a bug report: {zip}. Explorer opens at it when it is done (usually under a minute). Look through it, then attach it to an issue at https://github.com/qhenshaw/UnityCLIExtensions/issues.");
        }
    }
}

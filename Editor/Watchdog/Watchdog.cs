using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Starts, stops and reads the out-of-process watchdog (<c>Tools~/Watchdog/cliext-watchdog.ps1</c>, Windows only).
    /// It runs detached from the Editor, so it keeps working while the Editor's main thread is blocked and after a crash.
    /// In agent mode it starts with the Editor (see <see cref="CliExtSettings.StartWatchdog"/>).
    /// </summary>
    [InitializeOnLoad]
    public static class Watchdog
    {
        static Watchdog()
        {
            CliExtSettings.Changed -= OnSettingsChanged;
            CliExtSettings.Changed += OnSettingsChanged;
            // Not delayCall: in an unfocused Editor driven from the command line, delayCall callbacks can wait a long time
            // (seen queued for minutes after a domain reload), while EditorApplication.update keeps ticking.
            EditorApplication.update -= AutoStartOnce;
            EditorApplication.update += AutoStartOnce;
        }

        private static void AutoStartOnce()
        {
            EditorApplication.update -= AutoStartOnce;
            AutoStart();
        }

        /// <summary>True on Windows, the only platform the watchdog supports so far.</summary>
        public static bool IsSupported => Application.platform == RuntimePlatform.WindowsEditor;

        /// <summary>The watchdog's process id, or 0 when it is not running.</summary>
        public static int RunningPid
        {
            get
            {
                string file = CliExtPaths.WatchdogPidFile;
                if (!File.Exists(file) || !int.TryParse(File.ReadAllText(file).Trim(), out int pid)) return 0;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return process.HasExited ? 0 : pid;
                }
                catch (ArgumentException)
                {
                    return 0;
                }
            }
        }

        /// <summary>True when a watchdog process is watching this project.</summary>
        public static bool IsRunning => RunningPid != 0;

        /// <summary>Starts the watchdog unless it is already running. Returns a message saying what happened.</summary>
        public static string Start()
        {
            if (!IsSupported) return "The watchdog only runs on Windows so far.";
            WatchdogPolicy.WriteForCurrentSettings();
            int running = RunningPid;
            if (running != 0) return $"The watchdog is already running (pid {running}).";

            string script = CliExtPaths.WatchdogScript;
            if (script == null || !File.Exists(script)) return $"The watchdog script is missing: {script}";
            if (File.Exists(CliExtPaths.WatchdogStopFile)) File.Delete(CliExtPaths.WatchdogStopFile);

            var info = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Watch -Detach " +
                $"-Project \"{CliExtPaths.ProjectRoot}\" -EditorPid {Process.GetCurrentProcess().Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                // -Detach starts the real watchdog through WMI and returns; this process is only the launcher.
                using var launcher = Process.Start(info);
                return "Starting the watchdog.";
            }
            catch (Exception e)
            {
                return $"Could not start the watchdog: {e.Message}";
            }
        }

        /// <summary>Asks the watchdog to exit (it checks between rounds), and ends it if it is still running.</summary>
        public static string Stop()
        {
            int pid = RunningPid;
            if (pid == 0) return "The watchdog is not running.";
            File.WriteAllText(CliExtPaths.WatchdogStopFile, DateTime.UtcNow.ToString("o"));
            return $"Asked the watchdog (pid {pid}) to stop.";
        }

        /// <summary>The watchdog's last status file (JSON), or null when it has never written one.</summary>
        public static string ReadStatusJson(out double ageSeconds)
        {
            ageSeconds = double.PositiveInfinity;
            string path = CliExtPaths.WatchdogStatusFile;
            if (!File.Exists(path)) return null;
            ageSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void AutoStart()
        {
            if (!IsSupported || Application.isBatchMode) return;
            // A watchdog that is already running reads the policy on every check, so new rules reach it after a recompile.
            try { WatchdogPolicy.WriteForCurrentSettings(); }
            catch (IOException) { }
            if (AgentMode.IsActive && CliExtSettings.instance.StartWatchdog && !IsRunning)
            {
                string result = Start();
                Debug.Log($"[CLI Extensions] Agent mode ({AgentMode.Reason}): {result}");
            }
        }

        private static void OnSettingsChanged()
        {
            if (!IsSupported) return;
            try { WatchdogPolicy.WriteForCurrentSettings(); }
            catch (IOException) { }
            AutoStart();
        }
    }
}

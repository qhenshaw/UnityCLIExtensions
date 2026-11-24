using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>The Editor's state as last written to <c>Library/CLIExtensions/heartbeat.json</c>.</summary>
    [Serializable]
    public class HeartbeatData
    {
        public int pid;
        public string utc;
        public string playMode;
        public bool compiling;
        public bool updating;
        public bool agentMode;
        public bool quitting;
    }

    /// <summary>
    /// Writes <c>Library/CLIExtensions/heartbeat.json</c> about once a second from <see cref="EditorApplication.update"/>,
    /// which runs on the main thread. When a modal dialog or a long operation holds the main thread the file stops
    /// changing, which the watchdog and any agent can see without asking the (blocked) Editor. On a normal quit it is
    /// written once more with <c>quitting: true</c>, so the watchdog can tell a quit from a crash.
    /// </summary>
    [InitializeOnLoad]
    public static class Heartbeat
    {
        private const double IntervalSeconds = 1.0;
        private static double s_Next;
        private static readonly int s_Pid = Process.GetCurrentProcess().Id;

        static Heartbeat()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.quitting += OnQuitting;
        }

        /// <summary>Reads the last heartbeat, or null when there is none.</summary>
        public static HeartbeatData Read(out double ageSeconds)
        {
            ageSeconds = double.PositiveInfinity;
            string path = CliExtPaths.HeartbeatFile;
            if (!File.Exists(path)) return null;
            ageSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            try { return JsonUtility.FromJson<HeartbeatData>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < s_Next) return;
            s_Next = now + IntervalSeconds;
            Write(false);
        }

        private static void OnQuitting() => Write(true);

        private static void Write(bool quitting)
        {
            var data = new HeartbeatData
            {
                pid = s_Pid,
                utc = DateTime.UtcNow.ToString("o"),
                playMode = EditorApplication.isPlaying ? (EditorApplication.isPaused ? "paused" : "playing") : "edit",
                compiling = EditorApplication.isCompiling,
                updating = EditorApplication.isUpdating,
                agentMode = AgentMode.IsActive,
                quitting = quitting
            };
            try
            {
                string path = CliExtPaths.HeartbeatFile;
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(data));
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            catch (IOException)
            {
                // The watchdog may be reading it; the next tick writes again.
            }
        }
    }
}

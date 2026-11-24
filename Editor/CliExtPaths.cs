using System.IO;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Where the package keeps its state. Everything lives under <c>Library/CLIExtensions/</c> (git-ignored, per project),
    /// so the watchdog, the Editor and an agent can all read it as plain files, even while the Editor's main thread is blocked.
    /// </summary>
    public static class CliExtPaths
    {
        private static string s_ProjectRoot;

        /// <summary>The project folder (the parent of Assets/). Safe to read from any thread.</summary>
        public static string ProjectRoot => s_ProjectRoot ??= Directory.GetCurrentDirectory();

        [UnityEditor.InitializeOnLoadMethod]
        private static void CacheProjectRoot()
        {
            // Application.dataPath is main-thread only; cache it so commands that run off the main thread can use the paths.
            s_ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        /// <summary><c>Library/CLIExtensions</c>, created on first use.</summary>
        public static string StateDirectory
        {
            get
            {
                string dir = Path.Combine(ProjectRoot, "Library", "CLIExtensions");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Written about once a second from the Editor's main loop. A stale file means the main thread is blocked.</summary>
        public static string HeartbeatFile => Path.Combine(StateDirectory, "heartbeat.json");

        /// <summary>One JSON object per line: every dialog the Editor answered for the agent, and every one the watchdog saw.</summary>
        public static string JournalFile => Path.Combine(StateDirectory, "dialogs.jsonl");

        /// <summary>Rewritten by the watchdog on every check: the Editor's state and any open dialogs.</summary>
        public static string WatchdogStatusFile => Path.Combine(StateDirectory, "watchdog-status.json");

        /// <summary>The rules the watchdog answers dialogs with, written by the Editor from the settings.</summary>
        public static string WatchdogPolicyFile => Path.Combine(StateDirectory, "watchdog-policy.json");

        /// <summary>The running watchdog's process id.</summary>
        public static string WatchdogPidFile => Path.Combine(StateDirectory, "watchdog.pid");

        /// <summary>Created to ask the watchdog to exit.</summary>
        public static string WatchdogStopFile => Path.Combine(StateDirectory, "watchdog.stop");

        /// <summary>The package's own folder (in Library/PackageCache for a git install).</summary>
        public static string PackageRoot
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CliExtPaths).Assembly);
                return info != null ? info.resolvedPath : null;
            }
        }

        /// <summary>The watchdog script shipped in the package's <c>Tools~</c> folder.</summary>
        public static string WatchdogScript
        {
            get
            {
                string root = PackageRoot;
                return root == null ? null : Path.Combine(root, "Tools~", "Watchdog", "cliext-watchdog.ps1");
            }
        }
    }
}

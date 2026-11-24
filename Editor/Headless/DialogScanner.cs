using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.PackageManager;

namespace UnityCLIExtensions
{
    /// <summary>One call in the project's code that can open a blocking dialog.</summary>
    [Serializable]
    public class BlockingCall
    {
        public string path;
        public int line;
        public string api;
        public string code;
        public string suggestion;
    }

    /// <summary>
    /// Finds calls in the project's own scripts (Assets/, embedded and local packages) that open a modal dialog,
    /// file picker or progress bar, each with what to use instead so an agent-driven Editor never waits on it.
    /// </summary>
    public static class DialogScanner
    {
        private struct Pattern
        {
            public Regex Regex;
            public string Api;
            public string Suggestion;
        }

        private static readonly Pattern[] s_Patterns =
        {
            P(@"\bDisplayDialogComplex\s*\(", "EditorUtility.DisplayDialogComplex", "AgentDialog.DisplayComplex(..., headlessAnswer: <0|1|2>)"),
            P(@"\bDisplayDialog\s*\(", "EditorUtility.DisplayDialog", "AgentDialog.Display(..., headlessAnswer: <true|false>)"),
            P(@"\bDisplayCancelableProgressBar\s*\(", "EditorUtility.DisplayCancelableProgressBar", "AgentProgress.ShowCancelable, or the Progress API"),
            P(@"\bDisplayProgressBar\s*\(", "EditorUtility.DisplayProgressBar", "AgentProgress.Show, or the Progress API"),
            P(@"\b(OpenFilePanel|OpenFilePanelWithFilters|SaveFilePanel|SaveFilePanelInProject|OpenFolderPanel|SaveFolderPanel)\s*\(", "EditorUtility file/folder picker", "take the path as a parameter (or a [CliArg]) when AgentMode.IsActive; OS pickers are invisible to Pipeline's dialog detection"),
            P(@"\.ShowModal(Utility)?\s*\(", "EditorWindow.ShowModal", "ShowUtility() or a non-modal window; skip it when AgentMode.IsActive"),
            P(@"\b(SaveCurrentModifiedScenesIfUserWantsTo|SaveModifiedScenesIfUserWantsTo)\s*\(", "EditorSceneManager save prompt", "EditorSceneManager.SaveOpenScenes() or SaveScenes() when AgentMode.IsActive"),
            P(@"\bImportPackage\s*\([^;]*,\s*true\s*\)", "AssetDatabase.ImportPackage(interactive: true)", "pass interactive: false when AgentMode.IsActive"),
        };

        private static Pattern P(string regex, string api, string suggestion) =>
            new Pattern { Regex = new Regex(regex, RegexOptions.Compiled), Api = api, Suggestion = suggestion };

        /// <summary>Scans the project's scripts. <paramref name="includeLocalPackages"/> adds embedded and file: packages.</summary>
        public static List<BlockingCall> Scan(bool includeLocalPackages = true)
        {
            var roots = new List<string> { Path.Combine(CliExtPaths.ProjectRoot, "Assets") };
            if (includeLocalPackages)
            {
                string own = CliExtPaths.PackageRoot;
                foreach (var package in PackageInfo.GetAllRegisteredPackages())
                {
                    if (package.source != PackageSource.Embedded && package.source != PackageSource.Local) continue;
                    if (own != null && Path.GetFullPath(package.resolvedPath) == Path.GetFullPath(own)) continue;
                    roots.Add(package.resolvedPath);
                }
            }

            var result = new List<BlockingCall>();
            foreach (string root in roots.Where(Directory.Exists))
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                ScanFile(file, result);
            return result;
        }

        /// <summary>Scans one file's text. Public for tests.</summary>
        public static IEnumerable<BlockingCall> ScanText(string path, string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                if (line.Contains("AgentDialog.") || line.Contains("AgentProgress.")) continue;
                foreach (var pattern in s_Patterns)
                {
                    if (!pattern.Regex.IsMatch(line)) continue;
                    yield return new BlockingCall
                    {
                        path = path,
                        line = i + 1,
                        api = pattern.Api,
                        code = trimmed.TrimEnd('\r').Trim(),
                        suggestion = pattern.Suggestion
                    };
                    break; // DisplayDialogComplex also matches DisplayDialog; report each line once.
                }
            }
        }

        private static void ScanFile(string file, List<BlockingCall> result)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { return; }
            string display = MakeRelative(file);
            result.AddRange(ScanText(display, text));
        }

        private static string MakeRelative(string file)
        {
            string root = CliExtPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(file).Replace('\\', '/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }
    }
}

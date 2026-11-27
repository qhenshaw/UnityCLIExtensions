using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// Tools~/cliext-play-capture.ps1 -Run, through `powershell -File` as an agent calls it: -Run "a","b" arrives as one
    /// string "a,b" and must still be two steps; ';;' separates steps too; commas inside an argument stay. Uses -DryRun,
    /// which parses and prints without touching the Editor.
    /// </summary>
    public class PlayCaptureScriptTests
    {
        private static string Script()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProjectFolders).Assembly);
            string path = info == null ? null : Path.Combine(info.resolvedPath, "Tools~", "cliext-play-capture.ps1");
            if (path == null || !File.Exists(path)) Assert.Ignore("cliext-play-capture.ps1 isn't next to the package.");
            return path;
        }

        private static (int exit, string stdout) Run(string arguments)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows PowerShell joins -File arrays only on Windows.");
            var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Script()}\" -Project \"{Directory.GetCurrentDirectory()}\" -DryRun {arguments}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            });
            string stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return (p.ExitCode, stdout);
        }

        private static string Compact(string json) => System.Text.RegularExpressions.Regex.Replace(json, @"\s+", "");

        [Test]
        public void RunThroughFile_AnArrayJoinedIntoOneString_IsStillTwoSteps()
        {
            var (exit, stdout) = Run("-Run \"cliext.playmode time_scale --value 0.01\",\"debugmenu do --position 1,2,3\" a@0");
            Assert.AreEqual(0, exit, stdout);
            StringAssert.Contains("\"run\":[[\"cliext.playmode\",\"time_scale\",\"--value\",\"0.01\"],[\"debugmenu\",\"do\",\"--position\",\"1,2,3\"]]", Compact(stdout));
        }

        [Test]
        public void RunWithTheSeparator_IsTwoSteps_AndAnUnclosedQuoteExits2()
        {
            var (exit, stdout) = Run("-Run \"scene save;;debugmenu do --tags Red,Blue\" a@0");
            Assert.AreEqual(0, exit, stdout);
            StringAssert.Contains("[\"scene\",\"save\"],[\"debugmenu\",\"do\",\"--tags\",\"Red,Blue\"]", Compact(stdout));
            Assert.AreEqual(2, Run("-Run \"debugmenu do --label 'One\" a@0").exit);
        }
    }
}

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCLIExtensions.TestRunner
{
    /// <summary>
    /// <c>cliext.test run</c> and <c>cliext.test result</c>: test runs whose result belongs to the run that was asked
    /// for. Pipeline's <c>test status</c> keeps showing the previous run until a new one starts, and a filter that
    /// matches nothing looks like a pass; these pick the tests first, refuse an empty match, and record each run.
    /// </summary>
    internal static class TestRunCommands
    {
        private const string Group = "cliext.test";

        [CliGroupCommand(Group, "Run tests and return that run's own result: counts and each failure with its message. The filter is matched first (--filter-type name: part of the full name; " +
                                "exact; assembly; category), and a filter that matches nothing fails with the closest names. Refuses while an open scene has unsaved changes (the Test Runner would stop at a save prompt). " +
                                "Edit Mode waits for the result; Play Mode reloads the domain, so it returns the run id: read it with cliext.test result --wait true.",
            Name = "run", Tags = new[] { "cliext", "tests" }, MainThreadRequired = true, Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> Run(
            CommandContext cmd,
            [CliArg("filter", "What to run (empty: every test in the mode)")] string filter = "",
            [CliArg("filter-type", "name (part of the full name), exact, assembly or category")] string filterType = "name",
            [CliArg("mode", "edit or play")] string mode = "edit",
            [CliArg("wait", "Wait for the result (Edit Mode only; a Play Mode run returns at once)")] bool wait = true,
            [CliArg("timeout", "Seconds to wait for an Edit Mode result")] double timeoutInSeconds = 600)
        {
            TestMode testMode;
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "edit": case "editmode": case "editor": testMode = TestMode.EditMode; break;
                case "play": case "playmode": testMode = TestMode.PlayMode; break;
                default: cmd.Fail($"Unknown mode '{mode}': use edit or play."); return ExitCode.InvalidUsage;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { cmd.Fail("Stop Play mode first (editor.playmode stop)."); return ExitCode.Failed; }
            if (EditorApplication.isCompiling) { cmd.Fail("The Editor is compiling; wait for it (cliext.wait idle)."); return ExitCode.Failed; }
            var dirty = TestRuns.DirtyScenes();
            if (dirty.Count > 0)
            {
                var saves = dirty.Where(d => d != "Untitled").Select(d => $"unity command cliext.scene save_open --scene {d}").ToList();
                string keep = saves.Count > 0 ? $"To keep the changes, run: {string.Join("; ", saves)}." : "";
                if (dirty.Contains("Untitled")) keep += " An Untitled scene must be saved under a path first.";
                cmd.Fail($"Open scene(s) have unsaved changes: {string.Join(", ", dirty)}. The Test Runner would stop at a save prompt. {keep.Trim()} Or discard the changes. Then run again.");
                return ExitCode.Failed;
            }

            ITestAdaptor root = await RetrieveTests(testMode);
            if (root == null) { cmd.Fail("The Test Runner didn't return the test list."); return ExitCode.Failed; }

            var matched = TestRuns.Match(root, filter, filterType);
            if (matched.Count == 0)
            {
                var all = TestRuns.Match(root, "", "name");
                var assemblies = all.Select(TestRuns.AssemblyOf).Where(a => a != null).Distinct().OrderBy(a => a).ToList();
                var close = Closest(all.Select(t => t.FullName), filter ?? "");
                cmd.Result.Write(new { matched = 0, mode = testMode.ToString(), assemblies, close });
                cmd.Fail($"No {testMode} test matches '{filter}' ({filterType}). {all.Count} {testMode} tests exist, in {assemblies.Count} assemblies.");
                return ExitCode.NotFound;
            }

            var record = TestRuns.Start(testMode, matched, filter, filterType);
            if (testMode == TestMode.PlayMode || !wait)
            {
                cmd.Result.Write(record);
                cmd.Log.Info($"Started run {record.runId} ({matched.Count} tests). Read it with: unity command cliext.test result --run-id {record.runId} --wait true");
                return ExitCode.Success;
            }
            return await WaitFor(cmd, record.runId, timeoutInSeconds);
        }

        [CliGroupCommand(Group, "The result of a run started with cliext.test run (default: the latest). --wait true waits until it finishes. Exit 0 when every test passed, 6 when one failed.",
            Name = "result", Tags = new[] { "cliext", "tests" }, MainThreadRequired = true, Effect = CommandEffect.ReadOnly)]
        internal static async Task<ExitCode> Result(
            CommandContext cmd,
            [CliArg("run-id", "The run (default: the latest)")] string runId = "",
            [CliArg("wait", "Wait for the run to finish")] bool wait = false,
            [CliArg("timeout", "Seconds to wait")] double timeoutInSeconds = 600)
        {
            if (string.IsNullOrWhiteSpace(runId)) runId = TestRuns.LatestRunId;
            if (string.IsNullOrWhiteSpace(runId)) { cmd.Fail("No run has been started with cliext.test run."); return ExitCode.NotFound; }
            if (wait) return await WaitFor(cmd, runId, timeoutInSeconds);
            var record = TestRuns.Load(runId);
            if (record == null) { cmd.Fail($"No run '{runId}'."); return ExitCode.NotFound; }
            return Report(cmd, record);
        }

        private static async Task<ExitCode> WaitFor(CommandContext cmd, string runId, double timeoutInSeconds)
        {
            var clock = Stopwatch.StartNew();
            double budget = Math.Max(1, timeoutInSeconds - 1);
            while (true)
            {
                var record = TestRuns.Load(runId);
                if (record == null) { cmd.Fail($"No run '{runId}'."); return ExitCode.NotFound; }
                if (record.status == "completed" || record.status == "failed" || record.status == "error") return Report(cmd, record);
                if (clock.Elapsed.TotalSeconds >= budget)
                {
                    cmd.Result.Write(record);
                    cmd.Fail($"Run {runId} was still {record.status} after {clock.Elapsed.TotalSeconds:0} s.");
                    return ExitCode.TimedOut;
                }
                cmd.CancellationToken.ThrowIfCancellationRequested();
                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Delay(250);
            }
        }

        private static ExitCode Report(CommandContext cmd, TestRunRecord record)
        {
            cmd.Result.Write(record);
            if (record.status == "completed") return ExitCode.Success;
            if (record.status == "failed")
            {
                foreach (var f in record.failures.Take(5)) cmd.Fail($"{f.result}: {f.fullName}: {f.message}");
                return ExitCode.Failed;
            }
            if (record.status == "error") { cmd.Fail("The run itself failed; see the console."); return ExitCode.Failed; }
            return ExitCode.Success;
        }

        /// <summary>The fixtures, namespaces and tests whose name is closest to a mistyped filter.</summary>
        private static System.Collections.Generic.List<string> Closest(System.Collections.Generic.IEnumerable<string> fullNames, string filter)
        {
            string f = filter.Trim().ToLowerInvariant();
            if (f.Length < 3) return new System.Collections.Generic.List<string>();
            // Every dotted prefix of every test (namespace, fixture, test) is a candidate; compare the filter with its last part.
            var candidates = new System.Collections.Generic.HashSet<string>();
            foreach (string name in fullNames)
            {
                string bare = name.Split('(')[0];
                int dot = -1;
                while ((dot = bare.IndexOf('.', dot + 1)) > 0) candidates.Add(bare.Substring(0, dot));
                candidates.Add(bare);
            }
            int limit = Math.Max(2, f.Length / 3);
            return candidates
                .Select(c => (name: c, distance: Distance(f, c.Substring(c.LastIndexOf('.') + 1).ToLowerInvariant())))
                .Where(x => x.distance <= limit)
                .OrderBy(x => x.distance).ThenBy(x => x.name.Length)
                .Take(6).Select(x => x.name).ToList();
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        private static Task<ITestAdaptor> RetrieveTests(TestMode mode)
        {
            var source = new TaskCompletionSource<ITestAdaptor>();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RetrieveTestList(mode, root => source.TrySetResult(root));
            return source.Task;
        }
    }
}

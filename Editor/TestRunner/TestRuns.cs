using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCLIExtensions.TestRunner
{
    /// <summary>One test run's outcome, as saved in <c>Library/CLIExtensions/test-runs/&lt;run id&gt;.json</c>.</summary>
    [Serializable]
    public class TestRunRecord
    {
        public string runId;
        /// <summary>started, running, completed, failed (a test failed) or error (the run itself broke).</summary>
        public string status;
        public string mode;
        public string filter;
        public string filterType;
        public string startedUtc;
        public string finishedUtc;
        public int matched;
        public int total;
        public int passed;
        public int failed;
        public int skipped;
        public int inconclusive;
        public double durationSeconds;
        public List<TestFailure> failures = new List<TestFailure>();
    }

    [Serializable]
    public class TestFailure
    {
        public string fullName;
        public string result;
        public string message;
        public string stackTrace;
    }

    /// <summary>
    /// Starts test runs with the Test Framework API and records each run's own result, so a reader never mistakes the
    /// previous run's status for the new one's, and a filter that matches nothing fails instead of passing. The
    /// callbacks are registered on every domain load, so a Play Mode run's result is recorded after its reload.
    /// </summary>
    [InitializeOnLoad]
    public static class TestRuns
    {
        private static readonly Recorder s_Recorder = new Recorder();

        static TestRuns()
        {
            TestRunnerApi.RegisterTestCallback(s_Recorder);
        }

        private static string Folder
        {
            get
            {
                string dir = Path.Combine(CliExtPaths.StateDirectory, "test-runs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string CurrentFile => Path.Combine(Folder, "current.txt");
        private static string FileFor(string runId) => Path.Combine(Folder, runId + ".json");

        public static TestRunRecord Load(string runId)
        {
            if (string.IsNullOrEmpty(runId)) runId = LatestRunId;
            if (string.IsNullOrEmpty(runId)) return null;
            string file = FileFor(runId);
            try { return File.Exists(file) ? JsonUtility.FromJson<TestRunRecord>(File.ReadAllText(file)) : null; }
            catch (IOException) { return null; }
        }

        /// <summary>The run started last (by this package), or null.</summary>
        public static string LatestRunId => File.Exists(CurrentFile) ? File.ReadAllText(CurrentFile).Trim() : null;

        private static void Save(TestRunRecord record)
        {
            try { File.WriteAllText(FileFor(record.runId), JsonUtility.ToJson(record, true)); }
            catch (IOException e) { Debug.LogWarning($"[CLI Extensions] Could not save test run {record.runId}: {e.Message}"); }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Choosing tests

        /// <summary>Test cases (leaves) of <paramref name="root"/> that match the filter.</summary>
        public static List<ITestAdaptor> Match(ITestAdaptor root, string filter, string filterType)
        {
            var leaves = new List<ITestAdaptor>();
            void Walk(ITestAdaptor t) { if (t.HasChildren) foreach (var c in t.Children) Walk(c); else if (!t.IsSuite) leaves.Add(t); }
            Walk(root);

            string f = (filter ?? "").Trim();
            string type = (filterType ?? "name").Trim().ToLowerInvariant();
            if (f.Length == 0) return leaves;
            switch (type)
            {
                case "name":
                    return leaves.Where(t => t.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                case "exact":
                    return leaves.Where(t => string.Equals(t.FullName, f, StringComparison.Ordinal)).ToList();
                case "assembly":
                {
                    string name = f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f;
                    return leaves.Where(t => string.Equals(AssemblyOf(t), name, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                case "category":
                    return leaves.Where(t => Ancestry(t).Any(a => a.Categories != null && a.Categories.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))).ToList();
                default:
                    throw new ArgumentException($"Unknown filter type '{filterType}': use name (part of the full name), exact, assembly or category.");
            }
        }

        private static IEnumerable<ITestAdaptor> Ancestry(ITestAdaptor t)
        {
            for (var a = t; a != null; a = a.Parent) yield return a;
        }

        public static string AssemblyOf(ITestAdaptor t)
        {
            var asm = Ancestry(t).FirstOrDefault(a => a.IsTestAssembly);
            if (asm == null) return null;
            string n = asm.Name;
            return n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
        }

        /// <summary>Open scenes with unsaved changes: the Test Runner would stop at a save prompt for them.</summary>
        public static List<string> DirtyScenes()
        {
            var dirty = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (s.isDirty) dirty.Add(string.IsNullOrEmpty(s.path) ? "Untitled" : s.path);
            }
            return dirty;
        }

        /// <summary>Starts a run of <paramref name="tests"/>. Returns its record (status "started").</summary>
        public static TestRunRecord Start(TestMode mode, IReadOnlyCollection<ITestAdaptor> tests, string filter, string filterType)
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var settings = new ExecutionSettings(new Filter { testMode = mode, testNames = tests.Select(t => t.FullName).Distinct().ToArray() });
            var record = new TestRunRecord
            {
                status = "started",
                mode = mode == TestMode.PlayMode ? "play" : "edit",
                filter = filter,
                filterType = filterType,
                startedUtc = DateTime.UtcNow.ToString("o"),
                matched = tests.Count
            };
            // The id is known only once Execute returns, but callbacks can fire inside it: keep the record pending.
            s_Recorder.pending = record;
            string runId = api.Execute(settings);
            record.runId = runId;
            s_Recorder.pending = null;
            Save(record);
            File.WriteAllText(CurrentFile, runId);
            return Load(runId) ?? record;
        }

        // ---------------------------------------------------------------------------------------------------------

        private sealed class Recorder : ICallbacks
        {
            public TestRunRecord pending;

            private TestRunRecord Current()
            {
                if (pending != null) return pending;
                return Load(LatestRunId);
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                var record = Current();
                if (record == null) return;
                record.status = "running";
                if (record.runId != null) Save(record);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var record = Current();
                if (record == null || record.status == "completed" || record.status == "failed") return;
                record.finishedUtc = DateTime.UtcNow.ToString("o");
                record.passed = result.PassCount;
                record.failed = result.FailCount;
                record.skipped = result.SkipCount;
                record.inconclusive = result.InconclusiveCount;
                record.total = record.passed + record.failed + record.skipped + record.inconclusive;
                record.durationSeconds = result.Duration;
                record.failures.Clear();
                void Walk(ITestResultAdaptor r)
                {
                    if (r.HasChildren) { foreach (var c in r.Children) Walk(c); return; }
                    if (r.TestStatus == TestStatus.Failed || r.TestStatus == TestStatus.Inconclusive)
                        record.failures.Add(new TestFailure
                        {
                            fullName = r.FullName,
                            result = r.TestStatus.ToString(),
                            message = Clip(r.Message, 1000),
                            stackTrace = Clip(r.StackTrace, 1500)
                        });
                }
                Walk(result);
                record.status = record.failed > 0 || record.inconclusive > 0 ? "failed" : "completed";
                if (record.runId != null) Save(record);
            }

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            private static string Clip(string s, int max) => s == null ? null : s.Length <= max ? s.Trim() : s.Substring(0, max) + "…";
        }
    }
}

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace UnityCLIExtensions.Tests
{
    /// <summary>The failure log: every cliext error goes through it, and it names the command that failed.</summary>
    public class FailureLogTests
    {
        [Test]
        public void PackageSource_ReportsErrorsThroughFail()
        {
            string root = Path.Combine(CliExtPaths.PackageRoot, "Editor");
            var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != "FailureLog.cs")
                .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
                .Where(x => Regex.IsMatch(x.line, @"\bLog\.Error\("))
                .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
                .ToList();
            Assert.IsEmpty(offenders, "Commands must report errors with cmd.Fail(...), so they reach the failure log:\n- " + string.Join("\n- ", offenders));
        }

        [Test]
        public void CommandName_ComesFromTheMethod_ItsAsyncStateMachine_OrALambda()
        {
            var type = typeof(CliExtPaths).Assembly.GetType("UnityCLIExtensions.CliExtCommands");
            var setAgentMode = type.GetMethod("SetAgentMode", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.AreEqual("cliext set_agent_mode", FailureLog.CommandName(setAgentMode));

            // cliext.test lives in its own assembly (UnityCLIExtensions.TestRunner).
            var testCommands = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityCLIExtensions.TestRunner.TestRunCommands")).First(t => t != null);
            var stateMachine = testCommands.GetNestedTypes(BindingFlags.NonPublic).First(t => t.Name.StartsWith("<Run>"));
            var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.AreEqual("cliext.test run", FailureLog.CommandName(moveNext));

            Assert.IsNull(FailureLog.CommandName(typeof(FailureLogTests).GetMethod(nameof(CommandName_ComesFromTheMethod_ItsAsyncStateMachine_OrALambda))));
        }

        [Test]
        public void ParameterNames_KeepNamesAndDropValues()
        {
            var names = FailureLog.ParameterNames(new[] { "--path", "Assets/A.vfx", "--ops-file=ops.json", "positional", "--dry-run", "true" });
            CollectionAssert.AreEqual(new[] { "path", "ops-file", "dry-run" }, names);
        }

        [Test]
        public void Write_AppendsALineThatReadsBack()
        {
            string marker = "failure-log-test-" + System.Guid.NewGuid().ToString("N");
            FailureLog.Write(new FailureLogEntry { command = "cliext test_probe", message = marker });
            var last = FailureLog.ReadLast(5).LastOrDefault(e => e.message == marker);
            Assert.IsNotNull(last, "the entry just written isn't in the log");
            Assert.AreEqual("cliext test_probe", last.command);
            Assert.IsFalse(string.IsNullOrEmpty(last.unity), "no Unity version stamped");
            Assert.IsFalse(string.IsNullOrEmpty(last.time), "no time stamped");
        }

        // --- field notes (cliext.failures note) ---------------------------------------------------------------------

        /// <summary>Runs <paramref name="body"/> and puts field-notes.jsonl back as it was, so the tests leave no notes.</summary>
        private static void WithNotesRestored(System.Action body)
        {
            string file = FieldNotes.LogFile;
            byte[] before = File.Exists(file) ? File.ReadAllBytes(file) : null;
            try { body(); }
            finally
            {
                if (before != null) File.WriteAllBytes(file, before);
                else if (File.Exists(file)) File.Delete(file);
            }
        }

        [Test]
        public void Note_WritesOneRecordWithItsFieldsAndVersions_AndNoOtherValues()
        {
            WithNotesRestored(() =>
            {
                string marker = "field-note-test-" + System.Guid.NewGuid().ToString("N");
                var note = FailureCommands.PrepareNote("Workaround", "cliext.vfx edit", marker + "\nsecond line", "hand-edited the file",
                    "", out string error, out Unity.Pipeline.Commands.ExitCode code);
                Assert.IsNotNull(note, error);
                Assert.AreEqual(Unity.Pipeline.Commands.ExitCode.Success, code);
                FieldNotes.Write(note);

                var last = FieldNotes.ReadLast(5).LastOrDefault(n => n.what.StartsWith(marker));
                Assert.IsNotNull(last, "the note just written isn't in field-notes.jsonl");
                Assert.AreEqual("workaround", last.kind, "the kind is stored lower case");
                Assert.AreEqual("cliext.vfx edit", last.command);
                Assert.AreEqual(marker + " second line", last.what, "--what is stored as one line");
                Assert.AreEqual("hand-edited the file", last.workaround);
                Assert.IsFalse(string.IsNullOrEmpty(last.time), "no time stamped");
                Assert.IsFalse(string.IsNullOrEmpty(last.unity), "no Unity version stamped");

                string line = File.ReadAllLines(FieldNotes.LogFile).Last(l => l.Contains(marker));
                var keys = Regex.Matches(line, "\"(\\w+)\":").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                CollectionAssert.AreEquivalent(new[] { "time", "kind", "command", "what", "workaround", "unity", "pipeline", "package" }, keys);
            });
        }

        [Test]
        public void Note_FromAFile_TakesItsFields_AndAFlagReplacesOne()
        {
            string path = Path.Combine(Path.GetTempPath(), "cliext-note-" + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "\uFEFF{\"kind\": \"missing\", \"command\": \"cliext.anim\", \"what\": \"no op for X\", \"workaround\": \"script eval\"}");
            try
            {
                var note = FailureCommands.PrepareNote("", "cliext.anim mask", "", "", path, out string error, out _);
                Assert.IsNotNull(note, error);
                Assert.AreEqual("missing", note.kind);
                Assert.AreEqual("cliext.anim mask", note.command, "the --command flag replaces the file's command");
                Assert.AreEqual("no op for X", note.what);
                Assert.AreEqual("script eval", note.workaround);

                File.WriteAllText(path, "[1, 2]");
                Assert.IsNull(FailureCommands.PrepareNote("", "", "", "", path, out error, out var code));
                Assert.AreEqual(Unity.Pipeline.Commands.ExitCode.InvalidUsage, code, error);
            }
            finally { File.Delete(path); }

            Assert.IsNull(FailureCommands.PrepareNote("", "", "", "", path, out string missing, out var notFound));
            Assert.AreEqual(Unity.Pipeline.Commands.ExitCode.NotFound, notFound, missing);
        }

        [Test]
        public void Note_RefusesAnUnknownKind_ListingTheKinds_AndANoteWithoutWhat()
        {
            Assert.IsNull(FailureCommands.PrepareNote("annoyance", "cliext.vfx edit", "something", "", "", out string error, out var code));
            Assert.AreEqual(Unity.Pipeline.Commands.ExitCode.InvalidUsage, code);
            StringAssert.Contains("annoyance", error);
            foreach (string kind in FieldNotes.Kinds) StringAssert.Contains(kind, error);
            CollectionAssert.AreEqual(new[] { "bug", "workaround", "missing", "docs", "slow" }, FieldNotes.Kinds);

            Assert.IsNull(FailureCommands.PrepareNote("bug", "", "  ", "", "", out error, out code));
            Assert.AreEqual(Unity.Pipeline.Commands.ExitCode.InvalidUsage, code);
            StringAssert.Contains("--what", error);
        }

        [Test]
        public void Summary_GroupsTheNotesByKindAndCommand_MostFrequentFirst()
        {
            var notes = new[]
            {
                new FieldNote { time = "2026-11-27T10:00:00Z", kind = "workaround", command = "cliext.shadergraph edit", what = "alpha clip by hand" },
                new FieldNote { time = "2026-11-27T11:00:00Z", kind = "missing", command = "cliext.anim", what = "no take list" },
                new FieldNote { time = "2026-11-27T12:00:00Z", kind = "workaround", command = "cliext.shadergraph edit", what = "slider by hand", workaround = "edited JSON" },
            };
            var groups = FailureCommands.SummarizeNotes(notes);
            Assert.AreEqual(2, groups.Length);
            Assert.AreEqual("note", groups[0].source);
            Assert.AreEqual("workaround", groups[0].kind);
            Assert.AreEqual("cliext.shadergraph edit", groups[0].command);
            Assert.AreEqual(2, groups[0].count);
            Assert.AreEqual("slider by hand", groups[0].lastWhat);
            Assert.AreEqual("edited JSON", groups[0].lastWorkaround);
            Assert.AreEqual("2026-11-27T12:00:00Z", groups[0].lastTime);
            Assert.AreEqual(1, groups[1].count);
        }

        [Test]
        public void Summary_ReadsTheNotesFile()
        {
            WithNotesRestored(() =>
            {
                string marker = "summary-note-test-" + System.Guid.NewGuid().ToString("N");
                FieldNotes.Write(new FieldNote { kind = "docs", command = "cliext.test_probe " + marker, what = "probe" });
                var groups = FailureCommands.SummarizeNotes(FieldNotes.ReadLast(int.MaxValue));
                var mine = groups.SingleOrDefault(g => g.command == "cliext.test_probe " + marker);
                Assert.IsNotNull(mine, "the summary has no group for the note just written");
                Assert.AreEqual("docs", mine.kind);
                Assert.AreEqual(1, mine.count);
            });
        }
    }
}

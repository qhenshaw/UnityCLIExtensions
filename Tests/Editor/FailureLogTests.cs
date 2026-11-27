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
    }
}

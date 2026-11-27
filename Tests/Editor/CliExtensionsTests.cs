using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    public class AgentDialogTests
    {
        private bool _agentModeBefore;
        private bool _answerBefore;

        [SetUp]
        public void SetUp()
        {
            _agentModeBefore = CliExtSettings.instance.AgentModeSetting;
            _answerBefore = CliExtSettings.instance.AnswerProjectDialogs;
            CliExtSettings.instance.AgentModeSetting = true;
            CliExtSettings.instance.AnswerProjectDialogs = true;
        }

        [TearDown]
        public void TearDown()
        {
            CliExtSettings.instance.AgentModeSetting = _agentModeBefore;
            CliExtSettings.instance.AnswerProjectDialogs = _answerBefore;
        }

        [Test]
        public void Display_InAgentMode_ReturnsHeadlessAnswerAndJournalsIt()
        {
            DateTime start = DateTime.UtcNow.AddSeconds(-1);
            string title = "CLI Extensions test " + Guid.NewGuid().ToString("N");

            Assert.IsFalse(AgentDialog.Display(title, "message", "Do it", "Skip", headlessAnswer: false));

            var entry = DialogJournal.ReadLast(50, start).LastOrDefault(e => e.title == title);
            Assert.IsNotNull(entry, "The answered dialog is in the journal.");
            Assert.AreEqual("answered", entry.kind);
            Assert.AreEqual("Skip", entry.answer);
            Assert.That(entry.detail, Does.Contain(nameof(Display_InAgentMode_ReturnsHeadlessAnswerAndJournalsIt)), "The journal names the calling method.");
        }

        [Test]
        public void DisplayComplex_InAgentMode_ReturnsHeadlessAnswer()
        {
            Assert.AreEqual(2, AgentDialog.DisplayComplex("CLI Extensions test", "message", "Save", "Cancel", "Don't Save", headlessAnswer: 2));
            Assert.AreEqual(1, AgentDialog.DisplayComplex("CLI Extensions test", "message", "Save", "Cancel", "Don't Save", headlessAnswer: 7),
                "An out-of-range answer falls back to Cancel.");
        }

        [Test]
        public void ShowCancelable_InAgentMode_NeverCancels()
        {
            Assert.IsFalse(AgentProgress.ShowCancelable("CLI Extensions test", "working", 0.5f));
            AgentProgress.Clear();
        }
    }

    public class DialogScannerTests
    {
        [Test]
        public void ScanText_FindsBlockingCallsAndSkipsCommentsAndWrappers()
        {
            const string code =
                "// EditorUtility.DisplayDialog(\"a\", \"b\", \"c\");\n" +
                "if (EditorUtility.DisplayDialog(\"a\", \"b\", \"ok\")) { }\n" +
                "int i = EditorUtility.DisplayDialogComplex(\"a\", \"b\", \"x\", \"y\", \"z\");\n" +
                "AgentDialog.Display(\"a\", \"b\", \"ok\");\n" +
                "var p = EditorUtility.OpenFilePanel(\"Pick\", \"\", \"png\");\n" +
                "window.ShowModal();\n" +
                "AssetDatabase.ImportPackage(path, true);\n";

            var calls = DialogScanner.ScanText("Assets/Test.cs", code).ToList();

            CollectionAssert.AreEqual(new[] { 2, 3, 5, 6, 7 }, calls.Select(c => c.line).ToArray());
            Assert.AreEqual("EditorUtility.DisplayDialogComplex", calls[1].api, "DisplayDialogComplex is reported once, as itself.");
            Assert.IsTrue(calls.All(c => !string.IsNullOrEmpty(c.suggestion)));
        }
    }

    public class WatchdogPolicyTests
    {
        [Test]
        public void Policy_RoundTripsThroughJson_WithTheFieldsTheScriptReads()
        {
            var policy = new WatchdogPolicy { rules = WatchdogPolicy.DefaultRules(ScenePromptAnswer.DontSave) };
            string json = JsonUtility.ToJson(policy);

            foreach (string key in new[] { "autoClick", "restartOnCrash", "killAfterHungSeconds", "intervalSeconds", "staleHeartbeatSeconds", "relaunchArgs", "rules", "title", "action", "button" })
                Assert.That(json, Does.Contain("\"" + key + "\""), key);

            var back = JsonUtility.FromJson<WatchdogPolicy>(json);
            var scene = back.rules.Single(r => r.name == "scene-save-prompt");
            Assert.AreEqual("click", scene.action);
            Assert.AreEqual("^Don't Save", scene.button);
        }

        [Test]
        public void DefaultRules_EscalateScenePromptsUnlessTheSettingSaysOtherwise()
        {
            var rule = WatchdogPolicy.DefaultRules(ScenePromptAnswer.Escalate).Single(r => r.name == "scene-save-prompt");
            Assert.AreEqual("escalate", rule.action);
        }

        [TestCase("The open scene(s) have been modified externally", "scene-modified-externally")]
        [TestCase("Scene(s) Have Been Modified", "scene-save-prompt")]
        [TestCase("Input System native platform backend not enabled", "input-system-backends")]
        [TestCase("Enter Safe Mode?", "safe-mode")]
        [TestCase("Opening project...", "busy")]
        [TestCase("Opening Project in Non-Matching Editor Installation", "editor-version")]
        [TestCase("Save As", "file-picker")]
        [TestCase("Script Updating Consent", "api-updater")]
        [TestCase("Missing signature", "missing-signature")]
        public void Match_PicksTheRuleForUnityDialogs(string title, string expected)
        {
            var policy = new WatchdogPolicy { rules = WatchdogPolicy.DefaultRules(ScenePromptAnswer.Escalate) };
            Assert.AreEqual(expected, policy.Match(title)?.name);
        }

        [Test]
        public void MissingSignature_IsFocusedForTheEditorToAnswer_AndOnlyThatTitle()
        {
            var policy = new WatchdogPolicy { rules = WatchdogPolicy.DefaultRules(ScenePromptAnswer.Escalate) };
            Assert.AreEqual("focus", policy.Match(MissingSignatureAnswer.DialogTitle).action);
            Assert.IsNull(policy.Match("Invalid signature"), "The trust dialogs for a bad signature are left for a person.");
        }

        [Test]
        public void Match_EscalatesUnknownDialogs()
        {
            var policy = new WatchdogPolicy { rules = WatchdogPolicy.DefaultRules(ScenePromptAnswer.Save) };
            Assert.IsNull(policy.Match("Rebuild the navmesh?"));
        }

        [Test]
        public void DefaultRules_TitlesAreValidRegexes()
        {
            foreach (var rule in WatchdogPolicy.DefaultRules(ScenePromptAnswer.Save))
            {
                Assert.DoesNotThrow(() => new System.Text.RegularExpressions.Regex(rule.title), rule.name);
                if (rule.action == "click") Assert.IsFalse(string.IsNullOrEmpty(rule.button), rule.name);
            }
        }
    }

    public class SetupCheckTests
    {
        [Test]
        public void RunAll_ReportsEveryCheckWithAKnownStatus()
        {
            var results = SetupChecks.RunAll();
            CollectionAssert.AreEquivalent(SetupChecks.All.Select(c => c.Id), results.Select(r => r.id));
            var known = new[] { SetupChecks.Ok, SetupChecks.FixAvailable, SetupChecks.Manual, SetupChecks.NotApplicable };
            foreach (var r in results)
                CollectionAssert.Contains(known, r.status, $"{r.id}: {r.detail}");
        }

        [Test]
        public void RunAll_RejectsUnknownIds()
        {
            Assert.Throws<ArgumentException>(() => SetupChecks.RunAll(new[] { "no-such-check" }));
        }
    }

    public class HeartbeatTests
    {
        [Test]
        public void HeartbeatFile_IsUnderTheStateDirectory()
        {
            Assert.That(Path.GetFullPath(CliExtPaths.HeartbeatFile), Does.StartWith(Path.GetFullPath(CliExtPaths.StateDirectory)));
            Assert.That(CliExtPaths.StateDirectory.Replace('\\', '/'), Does.EndWith("Library/CLIExtensions"));
        }
    }
}

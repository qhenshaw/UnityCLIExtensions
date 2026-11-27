using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCLIExtensions.Tests
{
    public class AgentSetupTests
    {
        private static readonly string[] Available = { AgentSetup.VfxSkillName, AgentSetup.SkillName };

        [Test]
        public void ApplicableSkills_LeavesOutTheVfxSkill_WithoutVfxGraph()
        {
            List<string> skills = AgentSetup.ApplicableSkills(Available, new HashSet<string> { "com.unity.pipeline" });
            CollectionAssert.AreEqual(new[] { AgentSetup.SkillName }, skills);
        }

        [Test]
        public void ApplicableSkills_AddsTheVfxSkill_WithVfxGraph_AfterTheMainSkill()
        {
            List<string> skills = AgentSetup.ApplicableSkills(Available, new HashSet<string> { "com.unity.visualeffectgraph" });
            CollectionAssert.AreEqual(new[] { AgentSetup.SkillName, AgentSetup.VfxSkillName }, skills);
        }

        [Test]
        public void EveryPackagedSkill_HasAPurposeForTheClaudeMdBlock_AndAreaSkillsFollowTheirPackage()
        {
            string root = System.IO.Path.Combine(CliExtPaths.PackageRoot, "Skills~");
            foreach (string dir in System.IO.Directory.GetDirectories(root))
            {
                string skill = System.IO.Path.GetFileName(dir);
                if (skill == AgentSetup.SkillName || System.Linq.Enumerable.Contains(AgentSetup.GeneralSkills, skill)) continue;
                Assert.IsTrue(AgentSetup.SkillPurposes.ContainsKey(skill), $"{skill} has no SkillPurposes entry");
            }
            var areas = new[] { "unity-cliext-shadergraph", "unity-cliext-input", "unity-cliext-addressables" };
            List<string> without = AgentSetup.ApplicableSkills(areas, new HashSet<string> { "com.unity.inputsystem" });
            CollectionAssert.AreEqual(new[] { "unity-cliext-input" }, without);
            List<string> always = AgentSetup.ApplicableSkills(new[] { "unity-cliext-import" }, new HashSet<string>());
            CollectionAssert.AreEqual(new[] { "unity-cliext-import" }, always);
        }

        [Test]
        public void Block_AsksAgentsToRecordAFieldNote_WithTheExactCommand()
        {
            string block = AgentSetup.Block("1.2.3", new List<string> { AgentSetup.SkillName, AgentSetup.VfxSkillName });
            StringAssert.Contains(AgentSetup.NoteSentence.Trim(), block);
            StringAssert.Contains("unity command cliext.failures note --kind bug|workaround|missing|docs|slow", block);
            StringAssert.Contains("--file", block);
            StringAssert.Contains("a command you need doesn't exist", block);
            Assert.Less(block.IndexOf("unity-cliext-vfx", System.StringComparison.Ordinal), block.IndexOf("cliext.failures note", System.StringComparison.Ordinal),
                "the note sentence comes after the skill sentences");
            StringAssert.EndsWith("<!-- END unity-cliext -->", block);
        }

        [Test]
        public void StampedVersion_ReadsTheStampOfAnySkill()
        {
            string text = "# Skill\n\n" + AgentSetup.Stamp(AgentSetup.VfxSkillName, "0.23.0") + "\n";
            Assert.AreEqual("0.23.0", AgentSetup.StampedVersion(text));
        }

        [Test]
        public void StampedVersion_ReadsTheStampEarlierVersionsWrote()
        {
            const string written = "<!-- unity-cliext 0.22.0: installed by Unity CLI Extensions. It is replaced when the package updates; " +
                                   "to keep your own edits, turn off Project Settings > CLI Extensions > Keep Skill and CLAUDE.md Current. -->";
            Assert.AreEqual("0.22.0", AgentSetup.StampedVersion("text\n" + written));
        }

        [Test]
        public void StampedVersion_IsNull_ForAFileThePackageDidNotWrite()
        {
            Assert.IsNull(AgentSetup.StampedVersion("# My own skill\n<!-- a comment: of mine -->"));
        }
    }
}

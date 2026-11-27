using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace UnityCLIExtensions.Addressables.Tests
{
    public class AddressablesEditTests
    {
        // A text asset every project with this package has.
        private const string Readme = "Packages/com.qhenshaw.unitycliextensions/README.md";
        private const string NotFound = "AnimNotFoundException";
        private AddressableAssetSettings _settings;

        [SetUp]
        public void SetUp()
        {
            // In memory only: nothing is written to the project.
            _settings = AddressableAssetSettings.Create("Temp/CliextAddressablesTests", "TestSettings", true, false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings == null) return;
            foreach (var g in _settings.groups.Where(g => g != null).ToList())
            {
                foreach (var s in g.Schemas.Where(s => s != null).ToList()) UnityEngine.Object.DestroyImmediate(s);
                UnityEngine.Object.DestroyImmediate(g);
            }
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        private static AddressablesOp Op(string op, string path = null, string group = null, string name = null, string set = null, string labels = null, string address = null) =>
            new AddressablesOp { op = op, path = path, group = group, name = name, set = set, labels = labels, address = address };

        private static string CheckError(AddressablesOp op) => Assert.Catch<ArgumentException>(() => AddressablesCommands.Check(op)).Message;
        private string ApplyError(AddressablesOp op) => Assert.Catch<ArgumentException>(() => AddressablesCommands.Apply(_settings, op)).Message;

        [Test]
        public void Check_NamesTheFix_BeforeAnythingChanges()
        {
            StringAssert.Contains("Did you mean: add_entry?", CheckError(Op("add_entyr", Readme)));
            StringAssert.Contains("add_entry needs path", CheckError(Op("add_entry")));
            Assert.AreEqual(NotFound, Assert.Catch<ArgumentException>(() => AddressablesCommands.Check(Op("add_entry", "Assets/NoSuch.png"))).GetType().Name, "a missing asset is not found (exit 13 outside a batch)");
            StringAssert.Contains("asset find", CheckError(Op("remove_entry", "Assets/NoSuch.png")));
            StringAssert.Contains("isn't under Assets/ or Packages/", CheckError(Op("set_address", "C:/x.png", address: "x")));
            StringAssert.Contains("set_address needs address", CheckError(Op("set_address", Readme)));
            StringAssert.Contains("rename_group needs name", CheckError(Op("rename_group", group: "Default Local Group")));
            StringAssert.Contains("has '/'", CheckError(Op("add_group", name: "A/B")));
            StringAssert.Contains("'[' or ']'", CheckError(Op("add_label", name: "[x]")));
            StringAssert.Contains("set_group needs set", CheckError(Op("set_group", group: "G")));
        }

        [Test]
        public void ParseSettings_GivesOneLineErrorsWithTheNames()
        {
            var s = AddressablesCommands.ParseSettings("bundleMode=PackSeparately; compression=lz4 ;includeInBuild=false", true);
            Assert.AreEqual(BundledAssetGroupSchema.BundlePackingMode.PackSeparately, s.bundleMode);
            Assert.AreEqual(BundledAssetGroupSchema.BundleCompressionMode.LZ4, s.compression);
            Assert.AreEqual(false, s.includeInBuild);

            StringAssert.Contains("compression: 'zip' isn't a bundle compression mode. Use one of: Uncompressed, LZ4, LZMA", Assert.Catch<ArgumentException>(() => AddressablesCommands.ParseSettings("compression=zip", true)).Message);
            StringAssert.Contains("includeInBuild: ", Assert.Catch<ArgumentException>(() => AddressablesCommands.ParseSettings("includeInBuild=maybe", true)).Message);
            StringAssert.Contains("Did you mean: bundleMode?", Assert.Catch<ArgumentException>(() => AddressablesCommands.ParseSettings("bundelMode=PackTogether", true)).Message);
            StringAssert.Contains("isn't name=value", Assert.Catch<ArgumentException>(() => AddressablesCommands.ParseSettings("compression", true)).Message);
            StringAssert.Contains("set names Compression twice", Assert.Catch<ArgumentException>(() => AddressablesCommands.ParseSettings("compression=LZ4;Compression=LZMA", true)).Message);
        }

        [Test]
        public void Apply_RefusesOpsThatWouldChangeNothing()
        {
            var group = _settings.DefaultGroup.Name;
            AddressablesCommands.Apply(_settings, Op("add_entry", Readme, labels: "ui"));
            StringAssert.Contains("is Addressable already", ApplyError(Op("add_entry", Readme)));
            StringAssert.Contains($"is in group {group} already", ApplyError(Op("move_entry", Readme, group: group)));
            string address = _settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(Readme)).address;
            StringAssert.Contains("nothing would change", ApplyError(Op("set_address", Readme, address: address)));
            StringAssert.Contains("nothing would change", ApplyError(Op("set_labels", Readme, labels: "ui")));
            StringAssert.Contains("has the label 'ui' already", ApplyError(Op("add_label", name: "ui")));
            StringAssert.Contains("has that name already", ApplyError(Op("rename_group", group: group, name: group)));
            var bundled = _settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>();
            StringAssert.Contains("nothing would change", ApplyError(Op("set_group", group: group, set: "bundleMode=" + bundled.BundleMode)));
            StringAssert.Contains("is the default group", ApplyError(Op("remove_group", group: group)));
        }

        [Test]
        public void Apply_UnknownGroupsAndLabels_SayDidYouMeanAndWhereToListThem()
        {
            AddressablesCommands.Apply(_settings, Op("add_label", name: "enemies"));
            var e = Assert.Catch<ArgumentException>(() => AddressablesCommands.Apply(_settings, Op("remove_label", name: "enemeis")));
            Assert.AreEqual(NotFound, e.GetType().Name);
            StringAssert.Contains("Did you mean: enemies?", e.Message);
            string g = _settings.DefaultGroup.Name;
            string typo = g.Substring(0, g.Length - 1);
            StringAssert.Contains($"Did you mean: {g}?", ApplyError(Op("set_group", group: typo, set: "compression=LZ4")));
            StringAssert.Contains("cliext.addressables status lists them", ApplyError(Op("set_group", group: "zzzzzzzzzzzz", set: "compression=LZ4")));
        }

        [Test]
        public void Analyze_NoIssuesResults_AreNotCountedAsIssues()
        {
            UnityEditor.AddressableAssets.Build.AnalyzeRules.AnalyzeRule.AnalyzeResult R(string name, MessageType severity) =>
                new UnityEditor.AddressableAssets.Build.AnalyzeRules.AnalyzeRule.AnalyzeResult { resultName = name, severity = severity };
            // The texts the rules return: the base rule's, and the bundle rules' "<rule> - No issues found.".
            Assert.IsTrue(AddressablesCommands.IsNoIssues(R("No issues found", MessageType.None)));
            Assert.IsTrue(AddressablesCommands.IsNoIssues(R("Check Duplicate Bundle Dependencies - No issues found.", MessageType.None)));
            Assert.IsFalse(AddressablesCommands.IsNoIssues(R("Check Duplicate Bundle Dependencies - No issues found.", MessageType.Warning)), "Only severity None is a no-issues result.");
            Assert.IsFalse(AddressablesCommands.IsNoIssues(R("Assets/Tex.png:Duplicated", MessageType.None)));
        }

        [Test]
        public void Apply_RemoveLabel_TakesItOffTheEntries()
        {
            AddressablesCommands.Apply(_settings, Op("add_entry", Readme, labels: "ui;docs"));
            AddressablesCommands.Apply(_settings, Op("remove_label", name: "ui"));
            CollectionAssert.DoesNotContain(_settings.GetLabels(), "ui");
            CollectionAssert.AreEquivalent(new[] { "docs" }, _settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(Readme)).labels);
        }
    }
}

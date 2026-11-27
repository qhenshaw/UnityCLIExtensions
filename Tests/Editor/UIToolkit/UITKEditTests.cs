using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using UnityCLIExtensions.UIToolkit;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// cliext.uitk edit: a batch with a bad op, or one Unity's importer warns about, leaves the UXML/USS as it was on
    /// disk and in memory, so the next good edit saves only its own change; and selectors, element types, ops that would
    /// do nothing or name something missing fail with the fix.
    /// </summary>
    public class UITKEditTests
    {
        private const string Folder = "Assets/CliExtUITKEditTests";

        private const string Uxml =
            "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
            "    <ui:VisualElement name=\"menu\" class=\"panel\">\n" +
            "        <ui:Label name=\"status\" text=\"idle\" />\n" +
            "        <ui:Button name=\"start\" text=\"Start\" class=\"primary\" />\n" +
            "    </ui:VisualElement>\n" +
            "</ui:UXML>\n";

        private const string Uss = ".panel {\n    width: 300px;\n}\n\n.primary {\n    color: #ffffff;\n}\n";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "CliExtUITKEditTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        private static string Make(string ext, string text)
        {
            string path = $"{Folder}/T_{Guid.NewGuid():N}.{ext}";
            File.WriteAllText(FileUtil.GetPhysicalPath(path), text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        private static string Read(string path) => File.ReadAllText(FileUtil.GetPhysicalPath(path));

        private static string Names(string uxmlPath)
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath).CloneTree();
            return string.Join(",", tree.Query<VisualElement>().ToList().Where(e => !string.IsNullOrEmpty(e.name)).Select(e => e.name));
        }

        // StyleSheet.rules is internal: the rules Unity holds in memory for the imported sheet.
        private static int RuleCount(string ussPath) =>
            ((Array)typeof(StyleSheet).GetProperty("rules", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .GetValue(AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath))).Length;

        private static UxmlOp Op(string op, string selector, string type = null, string name = null, string @class = null, string properties = null) =>
            new UxmlOp { op = op, selector = selector, type = type, name = name, @class = @class, properties = properties };

        [Test]
        public void FailedUxmlBatch_LeavesFileAndAssetAsBefore_NextEditSavesOnlyItsChange()
        {
            string path = Make("uxml", Uxml);
            Assert.AreEqual("menu,status,start", Names(path));

            var bad = UITKCommands.EditFile(path, new[] { Op("remove", "#status"), Op("add", "#menu", type: "Label", name: "extra"), Op("remove", "#nosuch") });
            Assert.IsFalse(bad.saved);
            Assert.AreEqual(2, bad.failedAt);
            StringAssert.Contains("no element named 'nosuch'", bad.error);
            Assert.AreEqual(Uxml, Read(path));
            Assert.AreEqual("menu,status,start", Names(path));

            var good = UITKCommands.EditFile(path, new[] { Op("remove", "#start") });
            Assert.IsTrue(good.saved, good.error);
            Assert.AreEqual("menu,status", Names(path));
            string Lines(string s) => string.Join("\n", s.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0));
            Assert.AreEqual(Lines(Uxml.Replace("        <ui:Button name=\"start\" text=\"Start\" class=\"primary\" />\n", "")), Lines(Read(path)));
            Assert.IsFalse(Read(path).Replace("\r", "").Split('\n').Any(l => l.Length > 0 && l.Trim().Length == 0), "remove leaves a blank line");
        }

        [Test]
        public void UssEditTheImporterWarnsAbout_SavesNothing_ButOldWarningsDoNotBlock()
        {
            string path = Make("uss", Uss);
            var bad = UITKCommands.EditFile(path, new[] { Op("remove_rule", ".primary"), Op("set_rule", ".panel", properties: "colr: red") });
            Assert.IsFalse(bad.saved);
            StringAssert.Contains("Unknown property 'colr'", bad.error);
            Assert.AreEqual(Uss, Read(path));
            Assert.AreEqual(2, RuleCount(path));

            // A warning the file already had doesn't stop another edit.
            string warned = Make("uss", Uss.Replace("width: 300px;", "width: 300px;\n    colr: red;"));
            var good = UITKCommands.EditFile(warned, new[] { Op("remove_rule", ".primary") });
            Assert.IsTrue(good.saved, good.error);
            Assert.AreEqual(1, RuleCount(warned));
        }

        [Test]
        public void UxmlOps_ThatWouldBreakOrDoNothing_Fail()
        {
            string path = Make("uxml", Uxml);
            void Fails(UxmlOp op, string message)
            {
                var r = UITKCommands.EditFile(path, new[] { op });
                Assert.IsFalse(r.saved, op.op);
                StringAssert.Contains(message, r.error);
            }
            Fails(Op("remove", "UXML"), "Can't remove the UXML root");
            Fails(Op("add", "#menu", type: "Buton"), "Did you mean: Button");
            Fails(Op("add_class", "#start", @class: "primary"), "would change nothing");
            Fails(Op("remove_class", "#start", @class: "secondary"), "would change nothing");
            Fails(Op("remove", ".primry"), "Did you mean: .primary");
            Fails(Op("remov", "#start"), "Did you mean: remove");
            Fails(Op("set_rule", ".panel"), "is a USS op");
            Fails(Op("move", "#start"), "move needs to");
            Assert.AreEqual(Uxml, Read(path));
        }

        [Test]
        public void UssOps_ThatNameSomethingMissingOrDoNothing_Fail()
        {
            string text = Uss;
            void Fails(UxmlOp op, string message)
            {
                var e = Assert.Throws<ArgumentException>(() => UITKCommands.ApplyUss(text, op, "Assets/x.uss", out _));
                StringAssert.Contains(message, e.Message);
            }
            Fails(Op("remove_rule", ".primry"), "Did you mean: .primary");
            Fails(Op("remove_property", ".panel", properties: "widht"), "Did you mean: width");
            Fails(Op("set_rule", ".panel"), "set_rule needs properties");
            Fails(Op("set_rul", ".panel"), "Did you mean: set_rule");
            Assert.AreEqual(text, UITKCommands.ApplyUss(text, Op("set_rule", ".panel", properties: "width: 300px"), "Assets/x.uss", out _), "a set_rule that changes nothing leaves the text alone");
        }

        [Test]
        public void ElementTypes_ResolveBuiltInsAndKeywords()
        {
            XNamespace ui = "UnityEngine.UIElements";
            Assert.DoesNotThrow(() => UITKCommands.CheckElementType(ui + "Button"));
            Assert.DoesNotThrow(() => UITKCommands.CheckElementType(ui + "Instance"));
            Assert.DoesNotThrow(() => UITKCommands.CheckElementType(XNamespace.None + "Label"));
            var e = Assert.Throws<ArgumentException>(() => UITKCommands.CheckElementType(ui + "Sldier"));
            StringAssert.Contains("Did you mean: Slider", e.Message);
        }

        [Test]
        public void Selectors_RejectWhatTheyCannotMatch()
        {
            StringAssert.Contains("Pseudo-classes", Assert.Throws<ArgumentException>(() => new UITKSelector("Button:hover")).Message);
            StringAssert.Contains("Can't read", Assert.Throws<ArgumentException>(() => new UITKSelector("Button[x]")).Message);
            Assert.Throws<ArgumentException>(() => new UITKSelector("#menu >"));
            Assert.Throws<ArgumentException>(() => new UITKSelector(" "));
            Assert.DoesNotThrow(() => new UITKSelector("ui:Button.primary#start"));
            Assert.DoesNotThrow(() => new UITKSelector("#menu > *"));
            var s = new UITKSelector("#menu > Button.primry");
            StringAssert.Contains("Did you mean: .primary", s.NoMatch("#menu > Button.primry", new[] { "VisualElement", "Button", "#menu", ".primary" }, "cliext.uitk tree"));
        }

        [Test]
        public void Paths_OutsideAssetsOrOfAnotherType_AreUsageErrors_MissingFilesNameHowToMakeOne()
        {
            Assert.Throws<UnityCLIExtensions.Animation.AnimUsageException>(() => UITKCommands.Resolve("ProjectSettings/ProjectVersion.txt"));
            Assert.Throws<UnityCLIExtensions.Animation.AnimUsageException>(() => UITKCommands.Resolve($"{Folder}/x.txt"));
            var e = Assert.Throws<UnityCLIExtensions.Animation.AnimNotFoundException>(() => UITKCommands.Resolve($"{Folder}/None.uss"));
            StringAssert.Contains("Create > UI Toolkit > Style Sheet", e.Message);
        }
    }
}

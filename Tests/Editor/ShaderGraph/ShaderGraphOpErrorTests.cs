using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityCLIExtensions.ShaderGraph.Tests
{
    /// <summary>
    /// cliext.shadergraph ops that would save nothing, or that name a missing node, fail with a message saying what
    /// to do; bad paths are told apart from missing graphs; and a batch that fails leaves the file and the next
    /// load of the graph as they were.
    /// </summary>
    public class ShaderGraphOpErrorTests
    {
        private const string Folder = "Assets/CliExtShaderGraphTests";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        private static ShaderGraphDocument NewGraph(string name) =>
            ShaderGraphDocument.Create($"{Folder}/{name}_{Guid.NewGuid():N}.shadergraph", "Universal", "Unlit", "");

        private static object Apply(ShaderGraphDocument doc, Dictionary<string, string> aliases, ShaderGraphOp op) => ShaderGraphOps.Apply(doc, op, aliases);

        [Test]
        public void Unlink_OfASlotWithNoLink_Fails()
        {
            var doc = NewGraph("Unlink");
            var aliases = new Dictionary<string, string>();
            Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Math/Basic/Multiply", @as = "a" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Math/Basic/Multiply", @as = "b" });
            Apply(doc, aliases, new ShaderGraphOp { op = "link", from = "$a:Out", to = "$b:A" });
            Apply(doc, aliases, new ShaderGraphOp { op = "unlink", to = "$b:A" });
            var e = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "unlink", to = "$b:A" }));
            StringAssert.Contains("No link into", e.Message);
            e = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "unlink", to = "$b:B", from = "$a:Out" }));
            StringAssert.Contains("No link into", e.Message);
        }

        [Test]
        public void OpsThatWouldChangeNothing_Fail()
        {
            var doc = NewGraph("NoOp");
            var aliases = new Dictionary<string, string>();
            Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Math/Basic/Multiply", @as = "m" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "float", name = "Speed", @as = "p" });
            StringAssert.Contains("needs a value", Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set", id = "$m", slot = "A" })).Message);
            StringAssert.Contains("needs x", Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "move", id = "$m" })).Message);
            StringAssert.Contains("needs a name", Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "rename", id = "$p" })).Message);
        }

        [Test]
        public void Remove_OfAnUnknownId_PointsAtInspect()
        {
            var doc = NewGraph("Remove");
            var e = Assert.Throws<ArgumentException>(() => Apply(doc, new Dictionary<string, string>(), new ShaderGraphOp { op = "remove", id = "nope" }));
            StringAssert.Contains("No node or property 'nope'", e.Message);
            StringAssert.Contains("cliext.shadergraph inspect", e.Message);
        }

        [Test]
        public void BadValues_GiveOneLineMessages()
        {
            var doc = NewGraph("Values");
            var aliases = new Dictionary<string, string>();
            Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Input/Texture/Sample Texture 2D", @as = "t" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "boolean", name = "On", @as = "on" });
            var bad = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set", id = "$on", value = "maybe" }));
            StringAssert.Contains("true or false", bad.Message);
            var en = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set", id = "$t", setting = "textureType", value = "Glowy" }));
            StringAssert.Contains("Use one of:", en.Message);
            StringAssert.Contains("Normal", en.Message);
        }

        [Test]
        public void Paths_BadPathIsUsage_MissingGraphNamesCreate_ExistingNamesEdit()
        {
            Assert.Throws<ShaderGraphDocument.UsageException>(() => ShaderGraphDocument.NewGraphPath("NotAssets/X.shadergraph"));
            Assert.Throws<ShaderGraphDocument.UsageException>(() => ShaderGraphDocument.NewGraphPath("X.shadergraph"));
            Assert.Throws<ShaderGraphDocument.UsageException>(() => ShaderGraphDocument.Open("Assets/Some.png"));
            var missing = Assert.Throws<ArgumentException>(() => ShaderGraphDocument.Open($"{Folder}/None.shadergraph"));
            Assert.IsNotInstanceOf<ShaderGraphDocument.UsageException>(missing);
            StringAssert.Contains("cliext.shadergraph create", missing.Message);

            var doc = NewGraph("Exists");
            var exists = Assert.Throws<ArgumentException>(() => ShaderGraphDocument.Create(doc.path, "Universal", "Unlit", ""));
            StringAssert.Contains("cliext.shadergraph edit", exists.Message);
            Assert.Throws<ShaderGraphDocument.UsageException>(() => ShaderGraphDocument.Create($"{Folder}/T.shadergraph", "Nope", "", ""));
            Assert.Throws<ShaderGraphDocument.UsageException>(() => ShaderGraphDocument.Create($"{Folder}/M.shadergraph", "Universal", "Glowy", ""));
            Assert.Throws<FileNotFoundException>(() => ShaderGraphDocument.Create($"{Folder}/F.shadergraph", "", "", $"{Folder}/None.shadergraph"));
            Assert.IsFalse(File.Exists($"{Folder}/T.shadergraph") || File.Exists($"{Folder}/M.shadergraph") || File.Exists($"{Folder}/F.shadergraph"), "A failed create wrote a graph.");
        }

        [Test]
        public void AFailedBatch_LeavesTheFileAndTheNextLoadAsBefore()
        {
            var doc = NewGraph("Batch");
            string before = File.ReadAllText(doc.path);
            var aliases = new Dictionary<string, string>();
            var added = (AddedItem)Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Math/Basic/Multiply", @as = "m" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "color", name = "Tint" });
            Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Math/Basic/NoSuchNode" }));

            Assert.AreEqual(before, File.ReadAllText(doc.path), "The file on disk is as before.");
            var reopened = ShaderGraphDocument.Open(doc.path);
            Assert.IsFalse(reopened.HasNode(added.id), "The failed batch's node isn't in the next load.");
            Assert.IsFalse(reopened.TryFindProperty("Tint", out _), "The failed batch's property isn't in the next load.");

            // The next good edit saves only its own change.
            var aliases2 = new Dictionary<string, string>();
            Apply(reopened, aliases2, new ShaderGraphOp { op = "add_property", type = "float", name = "Speed" });
            reopened.Save();
            var saved = ShaderGraphDocument.Open(doc.path);
            Assert.IsTrue(saved.TryFindProperty("Speed", out _));
            Assert.IsFalse(saved.TryFindProperty("Tint", out _), "The failed batch's property came back with the next save.");
            Assert.IsFalse(saved.HasNode(added.id), "The failed batch's node came back with the next save.");
        }

        [Test]
        public void SetTarget_AlphaClip_OffersAndAddsTheAlphaBlocks_AndSaves()
        {
            var doc = NewGraph("AlphaClip");
            var aliases = new Dictionary<string, string>();
            var refused = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "add_block", block = "Alpha Clip Threshold" }));
            StringAssert.Contains("set_target", refused.Message);

            Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "alphaClip", value = "true" });
            StringAssert.Contains("Alpha Clip Threshold", string.Join(",", doc.AvailableBlockNames()));
            Assert.IsTrue(doc.FragmentBlockNames().Contains("Alpha Clip Threshold"), "The block alpha clipping brings is added.");
            Apply(doc, aliases, new ShaderGraphOp { op = "add_block", block = "Alpha Clip Threshold", @as = "clip" });
            Apply(doc, aliases, new ShaderGraphOp { op = "set", id = "$clip", slot = "0", value = "0.4" });
            Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "surfaceType", value = "Transparent" });
            Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "AllowMaterialOverride", value = "true" });
            Assert.IsFalse(doc.Save().hasErrors);

            var saved = ShaderGraphDocument.Open(doc.path);
            var settings = saved.FirstTargetSettings();
            Assert.AreEqual("true", settings["alphaClip"].ToLowerInvariant());
            Assert.AreEqual("Transparent", settings["surfaceType"]);
            Assert.AreEqual("true", settings["allowMaterialOverride"].ToLowerInvariant());
            StringAssert.Contains("m_AlphaClip\": true", File.ReadAllText(doc.path));
        }

        [Test]
        public void SetTarget_BadSettingValueOrNoChange_Fail()
        {
            var doc = NewGraph("TargetErrors");
            var aliases = new Dictionary<string, string>();
            var unknown = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "alphaClipping", value = "true" }));
            StringAssert.Contains("Did you mean alphaClip", unknown.Message);
            var badEnum = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "surfaceType", value = "Glass" }));
            StringAssert.Contains("Use one of:", badEnum.Message);
            StringAssert.Contains("Transparent", badEnum.Message);
            var same = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "alphaClip", value = "false" }));
            StringAssert.Contains("nothing to change", same.Message);
            var otherTarget = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_target", target = "HDRP", setting = "alphaClip", value = "true" }));
            StringAssert.Contains("Universal", otherTarget.Message);
        }

        [Test]
        public void SetMode_FloatSliderIntegerAndColourHdr_AreSavedAndShown()
        {
            var doc = NewGraph("Modes");
            var aliases = new Dictionary<string, string>();
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "float", name = "Amount", value = "2", @as = "a" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "float", name = "Steps", value = "3.4", @as = "s" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "color", name = "Edge", @as = "c" });
            Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "$a", mode = "slider", min = 0, max = 1 });
            Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "$s", mode = "integer" });
            Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "Edge", mode = "hdr" });
            doc.Save();

            var saved = ShaderGraphDocument.Open(doc.path);
            Assert.AreEqual("Slider(0, 1)", ShaderGraphOps.PropertyMode(saved.FindProperty("Amount")));
            Assert.AreEqual(1f, ((UnityEditor.ShaderGraph.Internal.Vector1ShaderProperty)saved.FindProperty("Amount")).value, "The default is clamped into the range.");
            Assert.AreEqual("Integer", ShaderGraphOps.PropertyMode(saved.FindProperty("Steps")));
            Assert.AreEqual("HDR", ShaderGraphOps.PropertyMode(saved.FindProperty("Edge")));
            var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(doc.path);
            int amount = shader.FindPropertyIndex(saved.FindProperty("Amount").referenceName);
            Assert.AreEqual(UnityEngine.Rendering.ShaderPropertyType.Range, shader.GetPropertyType(amount));
            Assert.AreEqual(new UnityEngine.Vector2(0, 1), shader.GetPropertyRangeLimits(amount));
        }

        [Test]
        public void SetMode_UnknownModeWrongTypeOrNoChange_Fail()
        {
            var doc = NewGraph("ModeErrors");
            var aliases = new Dictionary<string, string>();
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "float", name = "Amount", @as = "a" });
            Apply(doc, aliases, new ShaderGraphOp { op = "add_property", type = "vector2", name = "Offset", @as = "v" });
            var unknown = Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "$a", mode = "hdr" }));
            StringAssert.Contains("default, slider (with min and max), integer", unknown.Message);
            StringAssert.Contains("set_mode works on Float", Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "$v", mode = "slider" })).Message);
            StringAssert.Contains("nothing to change", Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "$a", mode = "default" })).Message);
            StringAssert.Contains("below its max", Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "$a", mode = "slider", min = 2, max = 1 })).Message);
        }

        [Test]
        public void AFailedBatchAfterSetTargetAndSetMode_LeavesTheFileAsBefore()
        {
            var doc = NewGraph("ModeBatch");
            Apply(doc, new Dictionary<string, string>(), new ShaderGraphOp { op = "add_property", type = "float", name = "Amount" });
            doc.Save();
            string before = File.ReadAllText(doc.path);
            var aliases = new Dictionary<string, string>();
            Apply(doc, aliases, new ShaderGraphOp { op = "set_target", setting = "alphaClip", value = "true" });
            Apply(doc, aliases, new ShaderGraphOp { op = "set_mode", id = "Amount", mode = "slider", min = 0, max = 1 });
            Assert.Throws<ArgumentException>(() => Apply(doc, aliases, new ShaderGraphOp { op = "add_node", type = "Math/Basic/NoSuchNode" }));
            Assert.AreEqual(before, File.ReadAllText(doc.path));
            var reopened = ShaderGraphDocument.Open(doc.path);
            Assert.AreEqual("false", reopened.FirstTargetSettings()["alphaClip"].ToLowerInvariant());
            Assert.AreEqual("Default", ShaderGraphOps.PropertyMode(reopened.FindProperty("Amount")));
        }
    }
}

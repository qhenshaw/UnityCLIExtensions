using System;
using System.Collections.Generic;
using System.IO;
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
    }
}

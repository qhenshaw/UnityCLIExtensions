using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityCLIExtensions.VFX.Tests
{
    public class VfxGraphCheckTests
    {
        private const string Folder = "Assets/CliExtVfxCheckTests";

        private string _path;

        [SetUp]
        public void SetUp()
        {
            _path = $"{Folder}/Check_{Guid.NewGuid():N}.vfx";
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);
        }

        /// <summary>Event, spawner, Initialize, Update and a quad output, linked, plus the extra ops given.</summary>
        private VfxGraphDocument Build(bool withBurst, params VfxOp[] extra)
        {
            var ops = new List<VfxOp>
            {
                Add("context", "Event", "ev"),
                Add("context", "Spawn", "sp"),
                Add("context", "Initialize Particle", "init"),
                Add("context", "Update Particle", "up"),
                Add("context", "Output Particle Unlit Quad", "out"),
                Link("$ev", "$sp"), Link("$sp", "$init"), Link("$init", "$up"), Link("$up", "$out"),
            };
            if (withBurst)
                ops.Add(new VfxOp { op = "add", kind = "block", type = "Spawn/Single Burst", context = "$sp", @as = "burst" });
            ops.AddRange(extra);

            // Not saved: the checks read the graph in memory, and saving would compile shaders, which these tests are not about.
            VfxGraphDocument doc = VfxGraphDocument.Create(_path);
            VfxGraphOps.ApplyAll(doc, ops);
            return doc;
        }

        private static VfxOp Add(string kind, string type, string name) => new VfxOp { op = "add", kind = kind, type = type, @as = name };
        private static VfxOp Link(string from, string to) => new VfxOp { op = "link", from = from, to = to };
        private static VfxOp Block(string type, string name) => new VfxOp { op = "add", kind = "block", type = type, context = "$init", @as = name };
        private static VfxOp Set(string id, string slot, string value) => new VfxOp { op = "set", id = id, slot = slot, value = value };
        private static VfxOp SetSetting(string id, string setting, string value) => new VfxOp { op = "set", id = id, setting = setting, value = value };

        private static bool HasOwnWarning(VfxGraphCheck.Result result, string text) =>
            result.warnings.Any(w => w.source == "cliext" && w.problem.Contains(text));

        private static string[] OnPlayAttributes(VfxGraphCheck.Result result) =>
            result.eventAttributes.Single(e => e.@event == "OnPlay").attributes;

        [Test]
        public void BurstWithTheDefaultCountOfZero_Warns_AndCountOne_DoesNot()
        {
            VfxGraphCheck.Result zero = VfxGraphCheck.Run(Build(true));
            Assert.IsTrue(HasOwnWarning(zero, "Count of 0"), "A burst left at its default Count of 0 should warn");

            AssetDatabase.DeleteAsset(_path);
            VfxGraphCheck.Result one = VfxGraphCheck.Run(Build(true, Set("$burst", "Count", "1")));
            Assert.IsFalse(HasOwnWarning(one, "Count of 0"), "A burst with Count 1 should not warn");
        }

        [Test]
        public void SpawnerWithNoBurstOrRate_Warns()
        {
            VfxGraphCheck.Result result = VfxGraphCheck.Run(Build(false));
            Assert.IsTrue(HasOwnWarning(result, "no burst or rate"), "A spawner that spawns nothing should warn");
        }

        [Test]
        public void SetScaleFromSource_IsReadAsScaleXYZ_AndOneChannelAsJustThatChannel()
        {
            VfxGraphCheck.Result all = VfxGraphCheck.Run(Build(true, Block("Set/Set Scale From Source", "scale"), Block("Set/Set Velocity From Source", "velocity")));
            CollectionAssert.IsSubsetOf(new[] { "scaleX", "scaleY", "scaleZ", "velocity" }, OnPlayAttributes(all));
            CollectionAssert.DoesNotContain(OnPlayAttributes(all), "scale", "Scale is read per channel, never as 'scale'");

            AssetDatabase.DeleteAsset(_path);
            VfxGraphCheck.Result single = VfxGraphCheck.Run(Build(true, Block("Set/Set Scale From Source", "scale"), SetSetting("$scale", "channels", "Y")));
            string[] attributes = OnPlayAttributes(single);
            CollectionAssert.Contains(attributes, "scaleY");
            CollectionAssert.DoesNotContain(attributes, "scaleX");
            CollectionAssert.DoesNotContain(attributes, "scaleZ");
        }

        [Test]
        public void AttributeTheSpawnerSetsItself_IsNotListedAsAnEventAttribute()
        {
            var spawnerSetsLifetime = new VfxOp { op = "add", kind = "block", type = "Attribute/Set SpawnEvent Lifetime", context = "$sp" };
            VfxGraphCheck.Result result = VfxGraphCheck.Run(Build(true, Block("Set/Set Lifetime From Source", "lifetime"), Block("Set/Set Color From Source", "color"), spawnerSetsLifetime));
            string[] attributes = OnPlayAttributes(result);
            CollectionAssert.Contains(attributes, "color");
            CollectionAssert.DoesNotContain(attributes, "lifetime", "A Set SpawnEvent block supplies lifetime, so script does not have to");
        }
    }
}

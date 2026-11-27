using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions.VFX.Tests
{
    /// <summary>Ops that can't do what they say fail with a message that names the fix, instead of a stack trace or a no-op.</summary>
    public class VfxOpErrorTests
    {
        private const string Folder = "Assets/CliExtVfxOpErrorTests";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void Parse_ABadNumber_SaysWhatToGive()
        {
            var e = Assert.Throws<ArgumentException>(() => VfxGraphDocument.Parse("abc", typeof(float)));
            StringAssert.Contains("a number", e.Message);
            e = Assert.Throws<ArgumentException>(() => VfxGraphDocument.Parse("1,x,3", typeof(Vector3)));
            StringAssert.Contains("[x,y,z]", e.Message);
            e = Assert.Throws<ArgumentException>(() => VfxGraphDocument.Parse("maybe", typeof(bool)));
            StringAssert.Contains("true or false", e.Message);
        }

        [Test]
        public void Parse_AnUnknownEnumName_ListsTheNames()
        {
            var e = Assert.Throws<ArgumentException>(() => VfxGraphDocument.Parse("Glowy", typeof(KeyCode)));
            StringAssert.Contains("Use one of:", e.Message);
            StringAssert.Contains("Space", e.Message);
            Assert.AreEqual(KeyCode.Space, VfxGraphDocument.Parse("space", typeof(KeyCode)));
        }

        [Test]
        public void Ops_ThatWouldDoNothing_Fail()
        {
            VfxGraphDocument doc = VfxGraphDocument.Create($"{Folder}/Ops_{Guid.NewGuid():N}.vfx");
            VfxGraphOps.ApplyAll(doc, new[]
            {
                new VfxOp { op = "add", kind = "context", type = "Event", @as = "ev" },
                new VfxOp { op = "add", kind = "context", type = "Spawn", @as = "sp" },
                new VfxOp { op = "add", kind = "block", type = "Spawn/Single Burst", context = "$sp" },
            });
            doc.Save();
            doc = VfxGraphDocument.Open(doc.path);
            var models = doc.ListModels().ToList();
            string ev = models.Single(m => m.type == "VFXBasicEvent").id;
            string sp = models.Single(m => m.type == "VFXBasicSpawner").id;
            string burst = models.Single(m => m.kind == "block").id;

            string Fails(VfxOp op)
            {
                // Each command opens the graph afresh; a failed batch makes VFX Graph reload it from disk.
                bool ok = VfxGraphOps.TryApplyAll(VfxGraphDocument.Open(doc.path), new[] { op }, new List<object>(), out _, out string error);
                Assert.IsFalse(ok, $"{op.op} succeeded");
                return error;
            }

            StringAssert.Contains("no flow link", Fails(new VfxOp { op = "unlink", from = ev, to = sp }));
            StringAssert.Contains("needs a value", Fails(new VfxOp { op = "set", id = burst, slot = "Count" }));
            StringAssert.Contains("needs a value", Fails(new VfxOp { op = "set", id = sp, setting = "loopDuration" }));
            StringAssert.Contains("context's id", Fails(new VfxOp { op = "add", kind = "block", type = "Spawn/Single Burst" }));
            StringAssert.Contains("use context, block, operator or parameter", Fails(new VfxOp { op = "add", kind = "blok", type = "Spawn/Single Burst" }));
        }
    }
}

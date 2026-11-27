using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityCLIExtensions.VFX.Tests
{
    /// <summary>
    /// A VFX Graph's editor model is stored in the .vfx but hidden from LoadAllAssetsAtPath, so a rollback that only
    /// unloads the listed objects leaves a failed batch's models in memory, to be saved with the next edit.
    /// </summary>
    public class VfxRollbackTests
    {
        private const string Folder = "Assets/CliExtVfxRollbackTests";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void Rollback_DiscardsModelsAddedToTheGraphInMemory()
        {
            string path = $"{Folder}/Rollback_{Guid.NewGuid():N}.vfx";
            VfxGraphDocument doc = VfxGraphDocument.Create(path);

            using (var rollback = AssetRollback.Begin(new[] { doc.path }))
            {
                VfxGraphOps.ApplyAll(doc, new[]
                {
                    new VfxOp { op = "add", kind = "context", type = "Spawn", @as = "sp" },
                    new VfxOp { op = "add", kind = "context", type = "Initialize Particle", @as = "init" },
                    new VfxOp { op = "link", from = "$sp", to = "$init" },
                });
                Assert.IsTrue(VfxGraphCheck.Run(doc).warnings.Any(w => w.problem.Contains("no burst or rate")), "The batch's spawner is in the graph.");
                rollback.Rollback();
            }

            VfxGraphDocument reopened = VfxGraphDocument.Open(path);
            Assert.IsFalse(VfxGraphCheck.Run(reopened).warnings.Any(w => w.problem.Contains("no burst or rate")),
                "After the rollback the graph has no spawner left in memory.");
        }

        [Test]
        public void Rollback_RestoresABlockARemoveOpDetached()
        {
            string path = $"{Folder}/Remove_{Guid.NewGuid():N}.vfx";
            VfxGraphDocument doc = VfxGraphDocument.Create(path);
            VfxGraphOps.ApplyAll(doc, new[]
            {
                new VfxOp { op = "add", kind = "context", type = "Spawn", @as = "sp" },
                new VfxOp { op = "add", kind = "block", type = "Spawn/Constant Spawn Rate", context = "$sp" },
            });
            doc.Save();
            doc = VfxGraphDocument.Open(path);
            string blockId = doc.ListModels().Single(m => m.kind == "block").id;

            byte[] before = System.IO.File.ReadAllBytes(path);
            bool ok = VfxGraphOps.TryApplyAll(doc, new[]
            {
                new VfxOp { op = "remove", id = blockId },
                new VfxOp { op = "remove", id = "999" },
            }, new System.Collections.Generic.List<object>(), out int failedAt, out string error);
            Assert.IsFalse(ok, "The batch has a bad op.");
            Assert.AreEqual(1, failedAt, error);
            CollectionAssert.AreEqual(before, System.IO.File.ReadAllBytes(path), "The file on disk is as before.");

            VfxGraphDocument reopened = VfxGraphDocument.Open(path);
            Assert.IsTrue(reopened.ListModels().Any(m => m.id == blockId),
                "After the rollback the removed block is back in the graph in memory, so the next save keeps it. Models: " +
                string.Join(", ", reopened.ListModels().Select(m => $"{m.id}:{m.type}")) + " " + error);
            Assert.IsFalse(VfxGraphCheck.Run(reopened).warnings.Any(w => w.problem.Contains("no burst or rate")), "The spawner has its rate block again.");

            // The next good edit saves only its own change: the block is still there, under the same id.
            reopened.Save();
            Assert.IsTrue(VfxGraphDocument.Open(path).ListModels().Any(m => m.id == blockId), "The block survives the next save.");
        }
    }
}

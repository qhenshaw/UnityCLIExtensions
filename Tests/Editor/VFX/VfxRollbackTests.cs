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
    }
}

using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// AssetRollback makes batch edits all-or-nothing. A reimport keeps a native asset's unsaved in-memory state, so
    /// these check that a rollback really discards it, and that files created, changed or deleted on disk come back.
    /// </summary>
    public class AssetRollbackTests
    {
        private const string Folder = "Assets/CliExtRollbackTests";

        [SetUp]
        public void SetUp()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "CliExtRollbackTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void Rollback_DiscardsInMemoryEditsAndNewSubAssets()
        {
            string path = Folder + "/Rollback.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();
            int subAssetsBefore = AssetDatabase.LoadAllAssetsAtPath(path).Length;

            using (var rollback = AssetRollback.Begin(new[] { path }))
            {
                controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
                controller.layers[0].stateMachine.AddState("Run");
                rollback.Rollback();
            }

            var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            Assert.AreEqual(0, reloaded.parameters.Length, "The parameter added in memory is gone.");
            Assert.AreEqual(0, reloaded.layers[0].stateMachine.states.Length, "The state added in memory is gone.");
            Assert.AreEqual(subAssetsBefore, AssetDatabase.LoadAllAssetsAtPath(path).Length, "The state's sub-asset is gone.");
            Assert.IsFalse(EditorUtility.IsDirty(reloaded), "Nothing is left to save.");
        }

        [Test]
        public void RollbackWithRevertUndo_DestroysSubAssetsAddedWithoutUndo()
        {
            string path = Folder + "/RevertUndo.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();
            int subAssetsBefore = AssetDatabase.LoadAllAssetsAtPath(path).Length;

            using (var rollback = AssetRollback.Begin(new[] { path }, revertUndo: true))
            {
                // Added the way an editing API that records no Undo for it would (a TimelineAsset's marker track).
                var extra = new AnimationClip { name = "Unrecorded" };
                AssetDatabase.AddObjectToAsset(extra, path);
                rollback.Rollback();
            }

            Assert.AreEqual(subAssetsBefore, AssetDatabase.LoadAllAssetsAtPath(path).Length, "The unrecorded sub-asset is gone from memory.");
            AssetDatabase.SaveAssets();
            Assert.IsFalse(File.ReadAllText(path).Contains("Unrecorded"), "The next save doesn't write it.");
        }

        [Test]
        public void Rollback_KeepsChangesMadeBeforeTheBatch()
        {
            string path = Folder + "/Before.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();
            controller.AddParameter("Before", AnimatorControllerParameterType.Bool); // unsaved when the batch starts

            using (var rollback = AssetRollback.Begin(new[] { path }))
            {
                controller.AddParameter("During", AnimatorControllerParameterType.Bool);
                rollback.Rollback();
            }

            var names = AssetDatabase.LoadAssetAtPath<AnimatorController>(path).parameters.Select(p => p.name).ToArray();
            CollectionAssert.AreEqual(new[] { "Before" }, names, "Only the batch's own edit is discarded.");
        }

        [Test]
        public void Rollback_InWatchedFolder_DeletesNewFilesAndRestoresChangedAndDeletedOnes()
        {
            string keep = Folder + "/Keep.mask";
            string gone = Folder + "/Gone.mask";
            AssetDatabase.CreateAsset(new AvatarMask(), keep);
            AssetDatabase.CreateAsset(new AvatarMask(), gone);
            AssetDatabase.SaveAssets();
            string keepGuid = AssetDatabase.AssetPathToGUID(keep);
            string goneGuid = AssetDatabase.AssetPathToGUID(gone);
            byte[] keepBytes = File.ReadAllBytes(keep);

            using (var rollback = AssetRollback.Begin(new[] { keep, gone }, new[] { Folder }))
            {
                AssetDatabase.CreateAsset(new AvatarMask(), Folder + "/New.mask");
                var o = AssetDatabase.LoadAssetAtPath<AvatarMask>(keep);
                o.name = "Changed";
                EditorUtility.SetDirty(o);
                AssetDatabase.SaveAssets();
                AssetDatabase.DeleteAsset(gone);
                rollback.Rollback();
            }

            Assert.IsFalse(File.Exists(Folder + "/New.mask"), "The file the batch created is deleted.");
            CollectionAssert.AreEqual(keepBytes, File.ReadAllBytes(keep), "The changed file is restored.");
            Assert.IsTrue(File.Exists(gone), "The deleted file is restored.");
            Assert.AreEqual(keepGuid, AssetDatabase.AssetPathToGUID(keep), "GUIDs survive.");
            Assert.AreEqual(goneGuid, AssetDatabase.AssetPathToGUID(gone), "A restored file keeps its GUID.");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AvatarMask>(gone), "The restored file imports.");
        }
    }
}

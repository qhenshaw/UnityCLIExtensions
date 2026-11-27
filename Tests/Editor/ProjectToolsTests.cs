using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnityCLIExtensions.Tests
{
    public class HierarchyDiffTests
    {
        private static HierarchySnapshot.ObjectSnap Obj(string id, string path, params HierarchySnapshot.ComponentSnap[] components)
        {
            var o = new HierarchySnapshot.ObjectSnap { id = id, hierarchyPath = path };
            o.components.Add(Comp("go-" + id, "GameObject", 0, ("m_Name", path.Split('/').Last())));
            o.components.AddRange(components);
            return o;
        }

        private static HierarchySnapshot.ComponentSnap Comp(string id, string type, int index, params (string path, string value)[] props) =>
            new HierarchySnapshot.ComponentSnap
            {
                id = id, type = type, index = index,
                properties = props.Select(p => new HierarchySnapshot.Property { path = p.path, value = p.value }).ToList()
            };

        private static HierarchySnapshot.Snapshot Snap(params HierarchySnapshot.ObjectSnap[] objects) =>
            new HierarchySnapshot.Snapshot { objects = new List<HierarchySnapshot.ObjectSnap>(objects) };

        [Test]
        public void Diff_ReportsAddedRemovedRenamedComponentsAndProperties()
        {
            var before = Snap(
                Obj("1", "Player", Comp("11", "Transform", 0, ("m_LocalPosition.x", "0")), Comp("12", "Rigidbody", 0)),
                Obj("2", "Enemy"),
                Obj("3", "Light"));
            var after = Snap(
                Obj("1", "Player", Comp("11", "Transform", 0, ("m_LocalPosition.x", "2.5")), Comp("13", "BoxCollider", 0)),
                Obj("3", "Lights/Sun"),
                Obj("", "Pickup"));

            var changes = HierarchySnapshot.Diff(before, after);
            string[] lines = changes.Select(c => $"{c.change} {c.@object} {c.component} {c.property} {c.before}->{c.after}".Trim()).ToArray();

            CollectionAssert.Contains(lines, "property-changed Player Transform m_LocalPosition.x 0->2.5");
            CollectionAssert.Contains(lines, "component-removed Player Rigidbody  ->");
            CollectionAssert.Contains(lines, "component-added Player BoxCollider  ->");
            CollectionAssert.Contains(lines, "object-removed Enemy   ->");
            CollectionAssert.Contains(lines, "object-renamed Lights/Sun   Light->Lights/Sun");
            CollectionAssert.Contains(lines, "object-added Pickup   ->");
            Assert.IsFalse(changes.Any(c => c.property == "m_Name" && c.@object == "Lights/Sun"), "A rename is reported once, not again as m_Name.");
            Assert.AreEqual(6, changes.Count);
        }

        [Test]
        public void Diff_MatchesObjectsWithoutIdsByPath()
        {
            var before = Snap(Obj("", "New Thing", Comp("", "Transform", 0, ("m_LocalScale.x", "1"))));
            var after = Snap(Obj("42:0", "New Thing", Comp("43:0", "Transform", 0, ("m_LocalScale.x", "1"))));
            Assert.IsEmpty(HierarchySnapshot.Diff(before, after), "Saving the scene gave the object an id; it is still the same object.");
        }

        [Test]
        public void Diff_ARenamedParentIsOneChange_AndReferencesToWhatMovedWithItAreUnchanged()
        {
            var before = Snap(
                Obj("1", "Player"),
                Obj("2", "Player/Root"),
                Obj("3", "Player/Root/Hips"),
                Obj("4", "Player/Body", Comp("41", "SkinnedMeshRenderer", 0, ("m_RootBone", "Player/Root/Hips (Transform)"), ("m_Target", "Player/Root"))),
                Obj("5", "Camera", Comp("51", "Follow", 0, ("target", "Player/Root/Hips (Transform)"))));
            var after = Snap(
                Obj("1", "Hero"),
                Obj("2", "Hero/Root"),
                Obj("3", "Hero/Root/Hips"),
                Obj("4", "Hero/Body", Comp("41", "SkinnedMeshRenderer", 0, ("m_RootBone", "Hero/Root/Hips (Transform)"), ("m_Target", "Hero/Root"))),
                Obj("5", "Camera", Comp("51", "Follow", 0, ("target", "Hero/Root (Transform)"))));

            var changes = HierarchySnapshot.Diff(before, after);
            string[] lines = changes.Select(c => $"{c.change} {c.@object} {c.component} {c.property} {c.before}->{c.after}".Trim()).ToArray();

            CollectionAssert.AreEquivalent(new[]
            {
                "object-renamed Hero   Player->Hero",
                // A reference that now points at another object is still a change.
                "property-changed Camera Follow target Player/Root/Hips (Transform)->Hero/Root (Transform)"
            }, lines);
        }

        [Test]
        public void Diff_AChildRenamedOrMovedUnderARenamedParentIsStillReported()
        {
            var before = Snap(Obj("1", "Player"), Obj("2", "Player/Root"), Obj("3", "Player/Gun"), Obj("4", "Other"));
            var after = Snap(Obj("1", "Hero"), Obj("2", "Hero/Base"), Obj("3", "Other/Gun"), Obj("4", "Other"));

            string[] lines = HierarchySnapshot.Diff(before, after).Select(c => $"{c.change} {c.before}->{c.after}").ToArray();

            CollectionAssert.AreEquivalent(new[] { "object-renamed Player->Hero", "object-renamed Player/Root->Hero/Base", "object-renamed Player/Gun->Other/Gun" }, lines);
        }

        [Test]
        public void DrivenByBrain_LeavesOutTheTransformPoseAndTheLens_NotTheRest()
        {
            var go = new UnityEngine.GameObject("DrivenCamera");
            try
            {
                var t = HierarchySnapshot.DrivenByBrain(go.transform);
                Assert.IsTrue(t("m_LocalPosition") && t("m_LocalRotation.w"));
                Assert.IsFalse(t("m_LocalScale"), "The brain doesn't scale the camera.");
                var cam = HierarchySnapshot.DrivenByBrain(go.AddComponent<UnityEngine.Camera>());
                Assert.IsTrue(cam("field of view") && cam("near clip plane") && cam("m_LensShift.x"));
                Assert.IsFalse(cam("m_CullingMask") || cam("m_ClearFlags"), "Settings the brain doesn't touch are compared.");
                Assert.IsNull(HierarchySnapshot.DrivenByBrain(go.AddComponent<UnityEngine.BoxCollider>()));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ResolvePath_NamesWhatIsWrong()
        {
            Assert.Throws<Animation.AnimUsageException>(() => HierarchySnapshot.ResolvePath("C:/Scenes/Main.unity"));
            Assert.Throws<Animation.AnimUsageException>(() => HierarchySnapshot.ResolvePath("Packages/com.qhenshaw.unitycliextensions/package.json"));
            Assert.Throws<Animation.AnimUsageException>(() => HierarchySnapshot.ResolvePath("Assets"));
            StringAssert.Contains("asset find --type Scene", Assert.Throws<Animation.AnimNotFoundException>(() => HierarchySnapshot.ResolvePath("Assets/NoSuchScene.unity")).Message);
            StringAssert.Contains("asset find --type Prefab", Assert.Throws<Animation.AnimNotFoundException>(() => HierarchySnapshot.ResolvePath("Assets/NoSuch.prefab")).Message);
        }

        [Test]
        public void ChangesOnSave_ListsWhatTheSaveWrites_AgainstTheFile()
        {
            const string path = "Assets/CliTestSaveChanges.unity";
            // The test runner's own scene is untitled and unsaved, so a new scene can't be added beside it.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            try
            {
                var moved = new UnityEngine.GameObject("Moved");
                var cam = new UnityEngine.GameObject("Cam").AddComponent<UnityEngine.Camera>();
                new UnityEngine.GameObject("Untouched");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(moved, scene);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cam.gameObject, scene);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(UnityEngine.GameObject.Find("Untouched"), scene);
                Assert.IsTrue(UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path));
                Assert.IsEmpty(HierarchySnapshot.ChangesOnSave(path), "A scene just saved has nothing to write.");

                moved.transform.position = new UnityEngine.Vector3(2, 0, 0);
                cam.fieldOfView = 40;
                var changes = HierarchySnapshot.ChangesOnSave(path);
                CollectionAssert.AreEquivalent(new[] { "Moved", "Cam" }, changes.Select(c => c.@object).Distinct().ToArray());
                Assert.IsTrue(changes.Any(c => c.@object == "Moved" && c.property == "m_LocalPosition.x" && c.before == "0" && c.after == "2"));
                Assert.IsTrue(changes.Any(c => c.@object == "Cam" && c.property == "field of view" && c.before == "60" && c.after == "40"));
                Assert.IsTrue(changes.All(c => c.drivenBy == null), "No CinemachineBrain here, so nothing is the brain's.");
                Assert.IsFalse(UnityEditor.AssetDatabase.IsValidFolder("Assets/__CliextSaveCompare"), "The temporary copy was left behind.");
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.AssetDatabase.DeleteAsset(path);
            }
        }
    }

    public class AssetDependencyArgTests
    {
        [Test]
        public void ResolveArg_AcceptsAPathOrGuid_AndNamesWhatIsWrong()
        {
            const string manifest = "Packages/com.qhenshaw.unitycliextensions/package.json";
            Assert.AreEqual(manifest, AssetDependencies.ResolveArg(manifest));
            Assert.AreEqual(manifest, AssetDependencies.ResolveArg(UnityEditor.AssetDatabase.AssetPathToGUID(manifest)));
            Assert.Throws<Animation.AnimUsageException>(() => AssetDependencies.ResolveArg("C:/Windows/notepad.exe"));
            StringAssert.Contains("is a folder", Assert.Throws<Animation.AnimUsageException>(() => AssetDependencies.ResolveArg("Packages/com.qhenshaw.unitycliextensions/Editor")).Message);
            StringAssert.Contains("asset find", Assert.Throws<Animation.AnimNotFoundException>(() => AssetDependencies.ResolveArg("Assets/NoSuch.mat")).Message);
            Assert.Throws<Animation.AnimNotFoundException>(() => AssetDependencies.ResolveArg("0123456789abcdef0123456789abcdef"));
        }
    }

    public class AffectedTestsTests
    {
        [Test]
        public void Plan_MapsThisPackagesScriptToItsTestAssembly()
        {
            var plan = AffectedTests.Plan(new[] { "Packages/com.qhenshaw.unitycliextensions/Editor/AgentMode.cs", "Assets/Level.unity" });

            CollectionAssert.Contains(plan.changedAssemblies, "UnityCLIExtensions.Editor");
            CollectionAssert.Contains(plan.otherChanges, "Assets/Level.unity");
            var tests = plan.testAssemblies.Single(t => t.name == "UnityCLIExtensions.Tests");
            Assert.AreEqual("EditMode", tests.mode);
            Assert.AreEqual("UnityCLIExtensions.Editor", tests.because);
        }

        [Test]
        public void Plan_WithOnlyNonScriptChanges_AffectsNoTests()
        {
            var plan = AffectedTests.Plan(new[] { "Assets/Materials/Red.mat" });
            Assert.IsEmpty(plan.testAssemblies);
            Assert.IsEmpty(plan.changedAssemblies);
        }

        [Test]
        public void AllTestAssemblies_IncludesThisPackagesTests()
        {
            Assert.IsTrue(AffectedTests.AllTestAssemblies().Any(t => t.name == "UnityCLIExtensions.Tests"));
        }
    }

    public class EditBatchTests
    {
        [TearDown]
        public void TearDown() => EditBatch.End();

        [Test]
        public void BeginAndEnd_OpenAndCloseTheBatch()
        {
            Assert.IsFalse(EditBatch.IsActive);
            EditBatch.Begin(60);
            Assert.IsTrue(EditBatch.IsActive);
            Assert.That(EditBatch.DeadlineUtc, Is.GreaterThan(System.DateTime.UtcNow.AddSeconds(50)));
            Assert.IsTrue(EditBatch.End());
            Assert.IsFalse(EditBatch.IsActive);
            Assert.IsFalse(EditBatch.End(), "Ending twice reports that none was open.");
        }
    }
}

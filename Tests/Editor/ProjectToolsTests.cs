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

using System;
using System.Linq;
using NUnit.Framework;
using UnityCLIExtensions.Project;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// cliext.physics, cliext.navmesh and cliext.quality argument checks. Settings are only read or changed on a
    /// SerializedObject that is never applied, so the project settings don't change.
    /// </summary>
    public class ProjectSettingsCommandTests
    {
        private static ArgumentException Catch(TestDelegate d) => Assert.Catch<ArgumentException>(d);

        private static SerializedProperty QualityLevel(out SerializedObject so)
        {
            so = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
            return so.FindProperty("m_QualitySettings").GetArrayElementAtIndex(0);
        }

        [Test]
        public void Layers_UnknownOrOutOfRange_AreUsageErrorsNamingTheLayers()
        {
            Assert.AreEqual(4, ProjectSettingsCommands.Layer("Water"));
            Assert.AreEqual(4, ProjectSettingsCommands.Layer("4"));
            var e = Catch(() => ProjectSettingsCommands.Layer("Defalt", "--a"));
            Assert.AreEqual("AnimUsageException", e.GetType().Name);
            StringAssert.StartsWith("--a: no layer 'Defalt'. Did you mean: Default? Layers: Default,", e.Message);
            StringAssert.Contains("layer 40 is outside 0-31", Catch(() => ProjectSettingsCommands.Layer("40")).Message);
        }

        private static string[] SomeLayers()
        {
            var l = new string[32];
            for (int i = 0; i < 32; i++) l[i] = "";
            l[0] = "Default"; l[1] = "TransparentFX"; l[2] = "Ignore Raycast"; l[4] = "Water"; l[5] = "UI"; l[8] = "Enemy";
            return l;
        }

        [Test]
        public void LayerNames_AreSetOnFreeUserIndexes_AndUsedOrBuiltinOnesAreRefused()
        {
            var next = ProjectSettingsCommands.PlanLayers("9=Player; 10=Pickup", SomeLayers(), out var changes);
            Assert.AreEqual("Player", next[9]);
            Assert.AreEqual("Pickup", next[10]);
            CollectionAssert.AreEqual(new[] { "9: (none) -> Player", "10: (none) -> Pickup" }, changes);
            Assert.AreEqual("", ProjectSettingsCommands.PlanLayers("8=none", SomeLayers(), out _)[8], "none clears a layer.");

            var used = Catch(() => ProjectSettingsCommands.PlanLayers("8=Player", SomeLayers(), out _));
            Assert.AreEqual("AnimUsageException", used.GetType().Name);
            StringAssert.Contains("layer 8 is already Enemy: pick a free index (free: 3, 6, 7, 9-31), or clear it first with 8=none", used.Message);
            StringAssert.Contains("layer 4 is Unity's built-in Water", Catch(() => ProjectSettingsCommands.PlanLayers("4=Player", SomeLayers(), out _)).Message);
            StringAssert.Contains("layer 8 is already named Enemy: layer names are unique", Catch(() => ProjectSettingsCommands.PlanLayers("9=enemy", SomeLayers(), out _)).Message);
            StringAssert.Contains("names layer 9 twice", Catch(() => ProjectSettingsCommands.PlanLayers("9=A;9=B", SomeLayers(), out _)).Message);
            StringAssert.Contains("isn't a layer index 0-31", Catch(() => ProjectSettingsCommands.PlanLayers("40=A", SomeLayers(), out _)).Message);
            StringAssert.Contains("isn't index=name", Catch(() => ProjectSettingsCommands.PlanLayers("Player", SomeLayers(), out _)).Message);
            var same = Catch(() => ProjectSettingsCommands.PlanLayers("8=Enemy", SomeLayers(), out _));
            Assert.AreEqual(typeof(ArgumentException), same.GetType(), "Names the layers already have exit 6.");
            StringAssert.Contains("nothing would change", same.Message);
        }

        [Test]
        public void MatrixOnly_MakesALayerCollideWithJustTheNamedOnes_BothWays()
        {
            var rows = Enumerable.Repeat(uint.MaxValue, 32).ToArray();
            var next = ProjectSettingsCommands.OnlyRows(rows, 9, 1u << 8, out var changed);
            Assert.AreEqual(1u << 8, next[9]);
            for (int b = 0; b < 32; b++)
                Assert.AreEqual(b == 8, (next[b] & (1u << 9)) != 0, $"layer {b}'s row agrees with layer 9's");
            Assert.AreEqual(31, changed.Count);
            ProjectSettingsCommands.OnlyRows(next, 9, 1u << 8, out var again);
            CollectionAssert.IsEmpty(again, "The same --only again changes nothing.");
            Assert.AreEqual("3, 6, 7, 10-31", ProjectSettingsCommands.Ranges(new[] { 3, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 }));
            Assert.IsNull(ProjectSettingsCommands.Ranges(new int[0]));
        }

        [Test]
        public void MaterialAssign_FindsTheObjectsColliders_AndSaysHowToAddOne()
        {
            var go = new GameObject("PhysTestFloor");
            var bare = new GameObject("PhysTestBare");
            try
            {
                go.AddComponent<BoxCollider>();
                go.AddComponent<SphereCollider>();
                Assert.AreEqual(2, ProjectSettingsCommands.ColliderTargets("/PhysTestFloor", false).Count, "A leading / (a hierarchy path) is fine; both colliders.");
                var none = Catch(() => ProjectSettingsCommands.ColliderTargets("PhysTestBare", false));
                Assert.AreEqual("AnimNotFoundException", none.GetType().Name);
                StringAssert.Contains("has no Collider: add one first (component add --type BoxCollider)", none.Message);
                StringAssert.Contains("it has a 3D Collider: leave out --2d", Catch(() => ProjectSettingsCommands.ColliderTargets("PhysTestFloor", true)).Message);
                var missing = Catch(() => ProjectSettingsCommands.ColliderTargets("PhysTestFlor", false));
                Assert.AreEqual("AnimNotFoundException", missing.GetType().Name);
                StringAssert.Contains("Did you mean: PhysTestFloor", missing.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(bare); }
        }

        [Test]
        public void Obstacle_IsCheckedBeforeAnythingChanges()
        {
            var add = ProjectSettingsCommands.PlanObstacle(null, "Box", "", "[2,2,2]", "", -1, -1, true);
            Assert.IsTrue(add.Add);
            Assert.AreEqual(NavMeshObstacleShape.Box, add.Shape);
            Assert.AreEqual(new Vector3(2, 2, 2), add.Size);
            Assert.IsTrue(add.Carve);
            var cap = ProjectSettingsCommands.PlanObstacle(null, "Box", "capsule", "", "[0,1,0]", 0.4f, 1.5f, null);
            Assert.AreEqual(NavMeshObstacleShape.Capsule, cap.Shape);
            Assert.AreEqual(0.4f, cap.Radius);
            StringAssert.Contains("--shape: 'Sphere' isn't an obstacle shape", Catch(() => ProjectSettingsCommands.PlanObstacle(null, "Box", "Sphere", "", "", -1, -1, null)).Message);
            StringAssert.Contains("every side must be more than 0", Catch(() => ProjectSettingsCommands.PlanObstacle(null, "Box", "", "[1,0,1]", "", -1, -1, null)).Message);
            StringAssert.Contains("a capsule takes --radius and --height", Catch(() => ProjectSettingsCommands.PlanObstacle(null, "Box", "Capsule", "[1,1,1]", "", -1, -1, null)).Message);
            StringAssert.Contains("a box takes --size", Catch(() => ProjectSettingsCommands.PlanObstacle(null, "Box", "", "", "", 1, -1, null)).Message);

            var go = new GameObject("NavTestBox");
            try
            {
                var o = go.AddComponent<NavMeshObstacle>();
                o.carving = true;
                var same = Catch(() => ProjectSettingsCommands.PlanObstacle(o, "NavTestBox", "Box", "[1,1,1]", "", -1, -1, true));
                Assert.AreEqual(typeof(ArgumentException), same.GetType());
                StringAssert.Contains("already has those values: nothing would change", same.Message);
                CollectionAssert.AreEqual(new[] { "carve: true -> false" }, ProjectSettingsCommands.PlanObstacle(o, "NavTestBox", "", "", "", -1, -1, false).Changes);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void MaterialSettings_AreCheckedBeforeAnythingIsMade()
        {
            var ok = ProjectSettingsCommands.ParseMaterialSet("dynamicfriction=0.2;bounceCombine=Maximum", false);
            Assert.AreEqual(0.2f, ok["dynamicFriction"]);
            Assert.AreEqual(PhysicsMaterialCombine.Maximum, ok["bounceCombine"]);
            StringAssert.Contains("bounciness 2 is out of range: give 0 to 1", Catch(() => ProjectSettingsCommands.ParseMaterialSet("bounciness=2", false)).Message);
            StringAssert.Contains("staticFriction -1 is out of range: give 0 or more", Catch(() => ProjectSettingsCommands.ParseMaterialSet("staticFriction=-1", false)).Message);
            StringAssert.Contains("Did you mean: bounciness?", Catch(() => ProjectSettingsCommands.ParseMaterialSet("bouncyness=1", false)).Message);
            StringAssert.Contains("Use one of: Average, Multiply, Minimum, Maximum", Catch(() => ProjectSettingsCommands.ParseMaterialSet("frictionCombine=Lowest", false)).Message);
            StringAssert.Contains("PhysicsMaterial2D has no 'dynamicFriction'", Catch(() => ProjectSettingsCommands.ParseMaterialSet("dynamicFriction=1", true)).Message);
            StringAssert.Contains("isn't a Single value", Catch(() => ProjectSettingsCommands.ParseMaterialSet("friction=abc", true)).Message);
        }

        [Test]
        public void EnumSettings_TakeTheStoredNumber_NotTheNameIndex()
        {
            var level = QualityLevel(out _);
            var changes = SerializedText.SetFields(level, "antiAliasing=2");
            Assert.AreEqual(2, level.FindPropertyRelative("antiAliasing").intValue, "antiAliasing=2 is 2x, not the third name (4x).");
            if (changes.Count > 0) StringAssert.EndsWith("-> 2x Multi Sampling", changes[0]);
            SerializedText.SetFields(level, "antiAliasing=4x Multi Sampling");
            Assert.AreEqual(4, level.FindPropertyRelative("antiAliasing").intValue);
            var bad = Catch(() => SerializedText.SetFields(level, "antiAliasing=3"));
            Assert.AreEqual("AnimUsageException", bad.GetType().Name);
            StringAssert.Contains("antiAliasing: '3' isn't one of: Disabled, 2x Multi Sampling", bad.Message);
            Assert.AreEqual(4, level.FindPropertyRelative("antiAliasing").intValue, "A refused value leaves the field as it was.");
        }

        [Test]
        public void SetFields_BadNamesAndValues_AreOneLineUsageErrors_AndUnchangedValuesAreNotChanges()
        {
            var level = QualityLevel(out _);
            var unknown = Catch(() => SerializedText.SetFields(level, "shadowDistnce=10"));
            Assert.AreEqual("AnimUsageException", unknown.GetType().Name);
            StringAssert.StartsWith("No setting 'shadowDistnce'. Did you mean: shadowDistance? Settings: ", unknown.Message);
            var number = Catch(() => SerializedText.SetFields(level, "shadowDistance=far"));
            Assert.AreEqual("shadowDistance: 'far' isn't a Single value: give a number such as 1 or 0.5.", number.Message);
            StringAssert.Contains("No setting 'excludedTargetPlatforms'", Catch(() => SerializedText.SetFields(level, "excludedTargetPlatforms=Android", "excludedTargetPlatforms")).Message);
            StringAssert.Contains("isn't name=value", Catch(() => SerializedText.SetFields(level, "shadowDistance")).Message);
            string current = SerializedText.Read(level.FindPropertyRelative("shadowDistance"));
            CollectionAssert.IsEmpty(SerializedText.SetFields(level, $"shadowDistance={current}"), "A value it already has is not a change.");
        }

        [Test]
        public void QualityLevelsAgentsAndPlatforms_UnknownNames_AreUsageErrorsWithTheValidOnes()
        {
            Assert.AreEqual(0, ProjectSettingsCommands.QualityIndex("0"));
            Assert.AreEqual(0, ProjectSettingsCommands.QualityIndex(QualitySettings.names[0].ToUpperInvariant()));
            var level = Catch(() => ProjectSettingsCommands.QualityIndex("NoSuchLevel"));
            Assert.AreEqual("AnimUsageException", level.GetType().Name);
            StringAssert.Contains($"Levels: {string.Join(", ", QualitySettings.names)}.", level.Message);
            StringAssert.Contains("no quality level '99'", Catch(() => ProjectSettingsCommands.QualityIndex("99")).Message);

            Assert.AreEqual(NavMesh.GetSettingsByIndex(0).agentTypeID, ProjectSettingsCommands.AgentId(""));
            var agent = Catch(() => ProjectSettingsCommands.AgentId("NoSuchAgent"));
            Assert.AreEqual("AnimUsageException", agent.GetType().Name);
            StringAssert.Contains("Agent types: ", agent.Message);

            var target = Catch(() => ProjectSettingsCommands.ParseCurrent<BuildTarget>("Andriod", "--platform", "build target", "Build targets", nameof(BuildTarget.NoTarget)));
            StringAssert.Contains("Did you mean: Android?", target.Message);
            StringAssert.DoesNotContain("NoTarget", target.Message);
            var obsolete = typeof(BuildTarget).GetFields().Where(f => f.IsDefined(typeof(ObsoleteAttribute), false)).Select(f => f.Name).FirstOrDefault();
            if (obsolete != null) StringAssert.DoesNotContain($" {obsolete},", target.Message, "Obsolete targets aren't offered.");
            Assert.AreEqual(BuildTarget.Android, ProjectSettingsCommands.ParseCurrent<BuildTarget>("android", "--platform", "build target", "Build targets"));
        }
    }
}

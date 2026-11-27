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

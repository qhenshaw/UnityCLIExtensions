using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Cinemachine;
using UnityEngine;

namespace UnityCLIExtensions.Cinemachine.Tests
{
    public class CinemachineCommandTests
    {
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _made.Clear();
        }

        private CinemachineCamera MakeCamera(string name, int priority)
        {
            var go = new GameObject(name);
            _made.Add(go);
            var cam = go.AddComponent<CinemachineCamera>();
            cam.Priority.Value = priority;
            return cam;
        }

        private static ArgumentException Catch(TestDelegate d) => Assert.Catch<ArgumentException>(d);

        [Test]
        public void Set_ResolvesEverythingBeforeChangingAnything()
        {
            var cam = MakeCamera("CmTestCam", 20);
            var e = Catch(() => CinemachineCommands.Plan(cam, 50, "CmTestNoSuchTarget", "", null, null));
            Assert.AreEqual("AnimNotFoundException", e.GetType().Name);
            StringAssert.Contains("scene get_hierarchy", e.Message);
            Assert.AreEqual(20, cam.Priority.Value, "The priority given with an unknown target isn't set.");
        }

        [Test]
        public void Set_ThatWouldChangeNothing_Fails()
        {
            var cam = MakeCamera("CmTestCam", 20);
            var none = Catch(() => CinemachineCommands.Plan(cam, null, "", "", null, null));
            Assert.AreEqual("AnimUsageException", none.GetType().Name);
            StringAssert.Contains("pass --priority, --follow", none.Message);
            var same = Catch(() => CinemachineCommands.Plan(cam, 20, "none", "", null, true));
            StringAssert.Contains("already has priority 20, no follow target, enabled: nothing would change", same.Message);
            var plan = CinemachineCommands.Plan(cam, 20, "", "", 50, null);
            CollectionAssert.AreEqual(new[] { "fov" }, plan.Changed, "Only the value that differs is changed.");
        }

        [Test]
        public void Set_TargetsOnACameraWithNoRig_Warn_AndRigFollowAddsOne()
        {
            var cam = MakeCamera("CmTestCam", 10);
            var target = new GameObject("CmTestTarget");
            _made.Add(target);
            cam.Target.TrackingTarget = target.transform;
            Assert.AreEqual("fixed", CinemachineCommands.CurrentRig(cam));
            var warnings = CinemachineCommands.RigWarnings(cam);
            Assert.AreEqual(2, warnings.Count, string.Join(" | ", warnings));
            StringAssert.Contains("no position (body) component", warnings[0]);
            StringAssert.Contains("--rig follow", warnings[0]);
            StringAssert.Contains("no rotation (aim) component", warnings[1]);

            var plan = CinemachineCommands.Plan(cam, null, "", "", null, null, "follow");
            CollectionAssert.AreEqual(new[] { "rig" }, plan.Changed);
            CinemachineCommands.ApplyRig(cam, plan.Rig);
            Assert.IsNotNull(cam.GetComponent<CinemachineFollow>());
            Assert.IsNotNull(cam.GetComponent<CinemachineRotationComposer>());
            Assert.AreEqual("follow", CinemachineCommands.CurrentRig(cam));
            CollectionAssert.IsEmpty(CinemachineCommands.RigWarnings(cam));
            StringAssert.Contains("already has rig follow: nothing would change", Catch(() => CinemachineCommands.Plan(cam, null, "", "", null, null, "follow")).Message);
            StringAssert.Contains("Did you mean: orbital", Catch(() => CinemachineCommands.Plan(cam, null, "", "", null, null, "orbitl")).Message);

            CinemachineCommands.ApplyRig(cam, "lookat");
            Assert.IsNull(cam.GetComponent<CinemachineFollow>(), "A new rig replaces the old body.");
            Assert.AreEqual("lookat", CinemachineCommands.CurrentRig(cam));
        }

        [Test]
        public void BadValues_AreUsageErrorsThatSayWhatToGive()
        {
            var cam = MakeCamera("CmTestCam", 20);
            StringAssert.Contains("more than 0 and less than 180", Catch(() => CinemachineCommands.Plan(cam, null, "", "", 200, null)).Message);
            var rig = Catch(() => CinemachineCommands.ParseRig("orbitl"));
            Assert.AreEqual("AnimUsageException", rig.GetType().Name);
            StringAssert.Contains("Did you mean: orbital", rig.Message);
            StringAssert.Contains("Use one of: follow, orbital, thirdperson, lookat, fixed", rig.Message);
            Assert.AreEqual("follow", CinemachineCommands.ParseRig(""));
        }

        [Test]
        public void CustomBlendsAssetPath_IsChecked()
        {
            StringAssert.Contains("isn't under Assets/ or Packages/", Catch(() => CinemachineCommands.CheckNewAssetPath("C:/Blends.asset", out _)).Message);
            StringAssert.Contains("must end in .asset", Catch(() => CinemachineCommands.CheckNewAssetPath("Assets/Blends.txt", out _)).Message);
            StringAssert.Contains("pass --asset", Catch(() => CinemachineCommands.CheckNewAssetPath("", out _)).Message);
            var folder = Catch(() => CinemachineCommands.CheckNewAssetPath("Assets/CmTestNoSuchFolder/Blends.asset", out _));
            Assert.AreEqual("AnimNotFoundException", folder.GetType().Name);
            Assert.AreEqual("Assets/CmTestBlends.asset", CinemachineCommands.CheckNewAssetPath("Assets/CmTestBlends.asset", out var existing));
            Assert.IsNull(existing);
        }

        [Test]
        public void UnknownCamera_IsNotFoundWithDidYouMean()
        {
            MakeCamera("CmTestCamera", 10);
            var e = Catch(() => CinemachineCommands.FindCamera("CmTestCamra"));
            Assert.AreEqual("AnimNotFoundException", e.GetType().Name);
            StringAssert.Contains("Did you mean: CmTestCamera", e.Message);
        }
    }
}

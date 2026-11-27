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

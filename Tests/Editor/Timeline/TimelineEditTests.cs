using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace UnityCLIExtensions.Timeline.Tests
{
    public class TimelineEditTests
    {
        private const string Folder = "Assets/CliExtTimelineTests";
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _made.Clear();
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        private TimelineAsset InMemory()
        {
            var t = ScriptableObject.CreateInstance<TimelineAsset>();
            _made.Add(t);
            var anim = t.CreateTrack<AnimationTrack>(null, "Anim");
            var clip = anim.CreateDefaultClip(); clip.displayName = "Idle"; clip.duration = 1;
            t.CreateTrack<GroupTrack>(null, "Group");
            return t;
        }

        private static string Error(TimelineAsset t, TimelineOp op) =>
            Assert.Catch<ArgumentException>(() => TimelineCommands.Apply(t, op)).Message;

        [Test]
        public void OpsThatWouldChangeNothing_Fail()
        {
            var t = InMemory();
            StringAssert.Contains("would change nothing", Error(t, new TimelineOp { op = "set_clip", track = "Anim", clip = "Idle" }));
            StringAssert.Contains("rename_track needs a name", Error(t, new TimelineOp { op = "rename_track", track = "Anim" }));
            StringAssert.Contains("already unmuted", Error(t, new TimelineOp { op = "mute_track", track = "Anim", value = "false" }));
            StringAssert.Contains("already on Anim", Error(t, new TimelineOp { op = "move_clip", track = "Anim", clip = "Idle", to = "Anim" }));
            Assert.AreEqual("Anim", t.GetRootTracks().First().name, "A rename without a name leaves the track's name.");
        }

        [Test]
        public void BadValuesAndNames_GiveOneLineFixes()
        {
            var t = InMemory();
            StringAssert.Contains("duration must be more than 0", Error(t, new TimelineOp { op = "set_clip", track = "Anim", clip = "Idle", set = "duration=-1" }));
            Assert.AreEqual(1, t.GetRootTracks().First().GetClips().First().duration, "A rejected duration isn't set.");
            StringAssert.Contains("Did you mean: Anim", Error(t, new TimelineOp { op = "remove_track", track = "Anm" }));
            StringAssert.Contains("Did you mean: Idle", Error(t, new TimelineOp { op = "remove_clip", track = "Anim", clip = "Idl" }));
            StringAssert.Contains("Did you mean: timeScale", Error(t, new TimelineOp { op = "set_clip", track = "Anim", clip = "Idle", set = "timescal=2" }));
            StringAssert.Contains("holds tracks, not clips", Error(t, new TimelineOp { op = "add_clip", track = "Group" }));
            StringAssert.Contains("remove_marker needs a time", Error(t, new TimelineOp { op = "remove_marker" }));
            StringAssert.Contains("Did you mean: add_clip", Error(t, new TimelineOp { op = "add_clips" }));
        }

        [Test]
        public void BindingAnObjectWithoutTheComponent_FailsInsteadOfClearingTheBinding()
        {
            var t = InMemory();
            var track = (AnimationTrack)t.GetRootTracks().First();
            var plain = new GameObject("TimelineTestPlain"); _made.Add(plain);
            var rig = new GameObject("TimelineTestRig"); _made.Add(rig);
            rig.transform.SetParent(plain.transform);
            rig.AddComponent<Animator>();
            var e = Assert.Catch<ArgumentException>(() => TimelineCommands.BindingFor(track, typeof(Animator), plain));
            StringAssert.Contains("has no Animator", e.Message);
            StringAssert.Contains("--object TimelineTestRig", e.Message);
            Assert.AreEqual(rig.GetComponent<Animator>(), TimelineCommands.BindingFor(track, typeof(Animator), rig));
        }

        [Test]
        public void AFailedBatch_PutsRemovedTracksAndClipsBack_AndTheNextSaveKeepsThem()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "CliExtTimelineTests");
            string path = $"{Folder}/Rollback_{Guid.NewGuid():N}.playable";
            var t = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(t, path);
            var anim = t.CreateTrack<AnimationTrack>(null, "Anim");
            anim.CreateDefaultClip().displayName = "A";
            anim.CreateDefaultClip().displayName = "B";
            var group = t.CreateTrack<GroupTrack>(null, "Group");
            t.CreateTrack<AnimationTrack>(group, "Sub");
            EditorUtility.SetDirty(t);
            AssetDatabase.SaveAssets();
            int objects = AssetDatabase.LoadAllAssetsAtPath(path).Length;
            byte[] before = System.IO.File.ReadAllBytes(path);

            bool ok = TimelineCommands.TryApplyAll(t, new[]
            {
                new TimelineOp { op = "remove_track", track = "Group" },
                new TimelineOp { op = "remove_clip", track = "Anim", clip = "B" },
                new TimelineOp { op = "add_marker", time = 0.5 },
                new TimelineOp { op = "remove_track", track = "Nope" },
            }, new List<object>(), out int failedAt, out string error);
            Assert.IsFalse(ok);
            Assert.AreEqual(3, failedAt, error);
            CollectionAssert.AreEqual(before, System.IO.File.ReadAllBytes(path), "The file on disk is as before.");

            t = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            string Shape(TimelineAsset x) => string.Join(",", TimelineCommands.Tracks(x).Select(p => p.path + "[" + string.Join("", p.track.GetClips().Select(c => c.displayName + (c.asset != null ? "" : "!"))) + "]"));
            Assert.AreEqual("Anim[AB],Group[],Group/Sub[]", Shape(t), "In memory the removed track, its child and the removed clip are back.");

            Assert.IsTrue(TimelineCommands.TryApplyAll(t, new[] { new TimelineOp { op = "mute_track", track = "Anim" } }, new List<object>(), out _, out error), error);
            Assert.AreEqual(objects, AssetDatabase.LoadAllAssetsAtPath(path).Length, "The next save keeps every track and clip asset, and adds no marker track.");
            Resources.UnloadAsset(t);
            Assert.AreEqual("Anim[AB],Group[],Group/Sub[]", Shape(AssetDatabase.LoadAssetAtPath<TimelineAsset>(path)), "Reloaded from disk, the timeline has them.");
        }
    }
}

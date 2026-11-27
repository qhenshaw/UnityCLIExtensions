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

        /// <summary>A director whose Anim track moves Actor/Child's x from 0 to 10 over a 1 s clip named Slide; the child starts at x 3.</summary>
        private (UnityEngine.Playables.PlayableDirector director, Transform child) SlideRig()
        {
            var t = ScriptableObject.CreateInstance<TimelineAsset>(); _made.Add(t);
            var clip = new AnimationClip { name = "SlideClip" }; _made.Add(clip);
            clip.SetCurve("Child", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 10));
            var track = t.CreateTrack<AnimationTrack>(null, "Anim");
            var tc = track.CreateClip(clip); tc.displayName = "Slide"; tc.start = 0; tc.duration = 1;
            var actor = new GameObject("TimelineTestActor"); _made.Add(actor);
            actor.AddComponent<Animator>();
            var child = new GameObject("Child").transform;
            child.SetParent(actor.transform);
            child.localPosition = new Vector3(3, 0, 0);
            var dgo = new GameObject("TimelineTestDirector"); _made.Add(dgo);
            var d = dgo.AddComponent<UnityEngine.Playables.PlayableDirector>();
            d.playOnAwake = false;
            d.playableAsset = t;
            d.SetGenericBinding(track, actor.GetComponent<Animator>());
            return (d, child);
        }

        [Test]
        public void EditModeScrub_ReadsThePose_ThenPutsTheBoundObjectsBack()
        {
            var (d, child) = SlideRig();
            var read = TimelineCommands.Scrub(d, 0.5, new[] { child.gameObject }, restore: true, out string scene);
            StringAssert.Contains("unchanged", scene);
            Assert.AreEqual(5.0, ((double[])Get(read[0], "localPosition"))[0], 1e-3, "The pose at 0.5 s is read while it is sampled.");
            Assert.AreEqual(3f, child.localPosition.x, 1e-4, "The bound object is back at its own pose.");
            Assert.AreEqual(0.5, d.time, 1e-6, "The director keeps the scrubbed time.");
            Assert.IsFalse(AnimationMode.InAnimationMode(), "Animation mode is stopped again.");
        }

        [Test]
        public void EditModeScrub_WithKeepPose_LeavesTheFrame()
        {
            var (d, child) = SlideRig();
            TimelineCommands.Scrub(d, 0.25, new GameObject[0], restore: false, out string scene);
            StringAssert.Contains("kept", scene);
            Assert.AreEqual(2.5f, child.localPosition.x, 1e-3);
        }

        [Test]
        public void ActiveClips_NameEachTracksClipAndItsLocalTime()
        {
            var (d, _) = SlideRig();
            var t = (TimelineAsset)d.playableAsset;
            ((AnimationTrack)t.GetRootTracks().First()).CreateClip(new AnimationClip { name = "Second" }).start = 2;
            var tracks = TimelineCommands.ActiveClips(t, 0.4);
            Assert.AreEqual(1, tracks.Length);
            Assert.AreEqual("Anim", Get(tracks[0], "track"));
            var clips = (Array)Get(tracks[0], "clips");
            Assert.AreEqual(1, clips.Length, "Only the clip under the playhead is listed.");
            var c = clips.GetValue(0);
            Assert.AreEqual("Slide", Get(c, "clip"));
            Assert.AreEqual(0.4, (double)Get(c, "localTime"), 1e-4);
            Assert.AreEqual("SlideClip", Get(c, "animationClip"));
        }

        private static object Get(object o, string name) => o.GetType().GetProperty(name).GetValue(o);

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

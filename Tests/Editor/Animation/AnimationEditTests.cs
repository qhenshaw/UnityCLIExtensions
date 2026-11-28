using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// cliext.animator edit and cliext.anim edit_clip batches: a failed batch leaves the asset as it was on disk and in
    /// memory, so the next good edit saves only its own change; and ops that would do nothing or name something
    /// missing fail with the fix.
    /// </summary>
    public class AnimationEditTests
    {
        private const string Folder = "Assets/CliExtAnimationEditTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "CliExtAnimationEditTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        private static AnimatorController MakeController()
        {
            string path = $"{Folder}/C_{Guid.NewGuid():N}.controller";
            var c = AnimatorController.CreateAnimatorControllerAtPath(path);
            c.AddParameter("Speed", AnimatorControllerParameterType.Float);
            c.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
            var sm = c.layers[0].stateMachine;
            var idle = sm.AddState("Idle");
            var run = sm.AddState("Run");
            idle.AddTransition(run).AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            run.AddTransition(idle).AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            sm.defaultState = idle;
            EditorUtility.SetDirty(c);
            AssetDatabase.SaveAssets();
            return c;
        }

        private static string States(AnimatorController c) =>
            string.Join(",", c.layers[0].stateMachine.states.Select(s => $"{s.state.name}[{s.state.transitions.Length}]"));

        [Test]
        public void FailedBatch_RestoresARemovedStateInMemory_AndTheNextSaveKeepsIt()
        {
            var c = MakeController();
            string path = AssetDatabase.GetAssetPath(c);
            byte[] before = File.ReadAllBytes(path);

            bool ok = AnimatorEditing.TryApplyAll(c, new[]
            {
                new AnimatorOp { op = "add_substatemachine", name = "Air" },
                new AnimatorOp { op = "remove_state", state = "Run" },
                new AnimatorOp { op = "rename_state", state = "Idle", name = "Rest" },
                new AnimatorOp { op = "remove_parameter", name = "Speed" },
                new AnimatorOp { op = "remove_state", state = "NoSuchState" },
            }, new List<object>(), out int failedAt, out string error);

            Assert.IsFalse(ok, "The batch has a bad op.");
            Assert.AreEqual(4, failedAt, error);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "The file on disk is as before.");

            var loaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            Assert.AreEqual("Idle[1],Run[1]", States(loaded), "In memory the removed state, its transitions and the old name are back.");
            Assert.AreEqual(0, loaded.layers[0].stateMachine.stateMachines.Length, "The batch's sub-state machine is gone.");
            CollectionAssert.AreEqual(new[] { "Speed", "Jump" }, loaded.parameters.Select(p => p.name).ToArray());

            // The next good edit saves only its own change.
            Assert.IsTrue(AnimatorEditing.TryApplyAll(loaded, new[] { new AnimatorOp { op = "set_parameter", name = "Speed", value = "0.5" } }, new List<object>(), out _, out error), error);
            string text = File.ReadAllText(path);
            StringAssert.Contains("m_Name: Run", text, "The saved file still has the Run state.");
            StringAssert.DoesNotContain("m_Name: Air", text);
            StringAssert.DoesNotContain("m_Name: Rest", text);
            StringAssert.Contains("m_DefaultFloat: 0.5", text);
        }

        [TestCase("rename_state", "Idle", null, null, "needs a name")]
        [TestCase("rename_state", "Idle", "Run", null, "already a state 'Run'")]
        [TestCase("set_default", "Idle", null, null, "already the default")]
        [TestCase("set_state", "Idle", null, null, "needs a motion or set")]
        [TestCase("set_layer", null, null, null, "set_layer needs set")]
        [TestCase("add_substatemachine", null, null, null, "needs a name")]
        [TestCase("remove_layer", null, null, null, "needs the layer")]
        [TestCase("set_state", "Runn", null, "speed=1", "Did you mean: Run?")]
        [TestCase("set_state", "Zzqx", null, "speed=1", "States: Idle, Run. cliext.animator inspect")]
        public void OpThatWouldDoNothingOrNamesNothing_Fails(string op, string state, string name, string set, string expected)
        {
            var c = MakeController();
            byte[] before = File.ReadAllBytes(AssetDatabase.GetAssetPath(c));
            bool ok = AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = op, state = state, name = name, set = set } }, new List<object>(), out _, out string error);
            Assert.IsFalse(ok);
            StringAssert.Contains(expected, error);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(AssetDatabase.GetAssetPath(c)));
        }

        [Test]
        public void BadConditionModeOrThreshold_IsOneLineNamingTheFix()
        {
            var c = MakeController();
            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "set_transition", from = "Idle", to = "Run", conditions = "Speed Biggr 0.1" } }, new List<object>(), out _, out string mode);
            StringAssert.Contains("Use one of: If, IfNot, Greater, Less", mode);
            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "set_transition", from = "Idle", to = "Run", conditions = "Speed Greater abc" } }, new List<object>(), out _, out string number);
            StringAssert.Contains("isn't a Single value", number);
            StringAssert.DoesNotContain("   at ", number);
        }

        [Test]
        public void BlendTreeOnAMissingOrNonFloatParameter_Fails()
        {
            var c = MakeController();
            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "add_blendtree", state = "Move", parameter = "Nope" } }, new List<object>(), out _, out string missing);
            StringAssert.Contains("No parameter 'Nope'", missing);
            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "add_blendtree", state = "Move", parameter = "Jump" } }, new List<object>(), out _, out string trigger);
            StringAssert.Contains("needs a Float parameter", trigger);
            Assert.AreEqual("Idle[1],Run[1]", States(AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GetAssetPath(c))));
        }

        [Test]
        public void FailedClipBatch_LeavesTheClipAsItWas_AndTheNextSaveSavesOnlyItsOwnChange()
        {
            string path = $"{Folder}/Clip_{Guid.NewGuid():N}.anim";
            var clip = new AnimationClip();
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0, 0, 1, 1));
            AssetDatabase.CreateAsset(clip, path);
            byte[] before = File.ReadAllBytes(path);

            bool ok = ClipCommands.TryApplyClipOps(clip, new[]
            {
                new ClipOp { op = "set_key", property = "m_LocalPosition.x", time = "0", value = "5" },
                new ClipOp { op = "add_event", time = "0.3", function = "Hit" },
                new ClipOp { op = "set_settings", set = "loopTime=true" },
                new ClipOp { op = "remove_key", property = "m_LocalPosition.y", time = "9" },
            }, new List<object>(), out int failedAt, out string error);

            Assert.IsFalse(ok);
            Assert.AreEqual(3, failedAt, error);
            StringAssert.Contains("No key at 9s", error);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "The file on disk is as before.");
            var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            var x = AnimationUtility.GetEditorCurve(loaded, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x"));
            Assert.IsFalse(x != null && x.keys.Any(k => k.value == 5), "In memory the batch's x key is gone.");
            Assert.AreEqual(0, AnimationUtility.GetAnimationEvents(loaded).Length, "In memory the batch's event is gone.");
            Assert.IsFalse(AnimationUtility.GetAnimationClipSettings(loaded).loopTime, "In memory loopTime is as before.");

            Assert.IsTrue(ClipCommands.TryApplyClipOps(loaded, new[] { new ClipOp { op = "add_event", time = "0.5", function = "Step" } }, new List<object>(), out _, out error), error);
            string text = File.ReadAllText(path);
            StringAssert.Contains("functionName: Step", text);
            StringAssert.DoesNotContain("functionName: Hit", text);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"x: 5,|value: 5\r?\n"), "The failed batch's x key was saved.");
            StringAssert.Contains("m_LoopTime: 0", text);
        }

        [TestCase("set_key", null, "1", "needs a time")]
        [TestCase("set_key", "0.5", null, "needs a value")]
        [TestCase("set_key", "0.5", "abc", "isn't a number")]
        [TestCase("add_event", "0.5", null, "needs a function")]
        [TestCase("remove_event", null, null, "needs a time or a function")]
        [TestCase("set_settings", null, null, "set_settings needs")]
        public void ClipOpWithAMissingOrBadValue_Fails(string op, string time, string value, string expected)
        {
            string path = $"{Folder}/Clip_{Guid.NewGuid():N}.anim";
            var clip = new AnimationClip();
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Linear(0, 0, 1, 1));
            AssetDatabase.CreateAsset(clip, path);
            bool ok = ClipCommands.TryApplyClipOps(clip, new[] { new ClipOp { op = op, property = "m_LocalPosition.y", time = time, value = value } }, new List<object>(), out _, out string error);
            Assert.IsFalse(ok);
            StringAssert.Contains(expected, error);
        }

        [Test]
        public void Hint_SuggestsACloseName_ElsePointsAtTheListCommand()
        {
            StringAssert.Contains("Did you mean: Speed?", AnimationErrors.Hint("Sped", new[] { "Speed", "Jump" }, "Parameters", "cliext.animator inspect"));
            string none = AnimationErrors.Hint("Zzqx", new[] { "Speed", "Jump" }, "Parameters", "cliext.animator inspect");
            StringAssert.Contains("Parameters: Speed, Jump", none);
            StringAssert.Contains("cliext.animator inspect", none);
        }

        /// <summary>
        /// A copy of a model from the project, importing the clips named in <paramref name="clips"/> ("name:first-last"
        /// frames); ignored when the project has no model with a take.
        /// </summary>
        internal static string ModelWithClips(string folder, params string[] clips)
        {
            string source = AssetDatabase.FindAssets("t:Model").Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.StartsWith("Assets/") && !p.StartsWith(folder) && AssetImporter.GetAtPath(p) is ModelImporter mi && mi.importedTakeInfos.Length > 0);
            if (source == null) Assert.Ignore("The project has no model with an animation take to copy.");
            string path = $"{folder}/Model_{Guid.NewGuid():N}{Path.GetExtension(source)}";
            Assert.IsTrue(AssetDatabase.CopyAsset(source, path));
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            string take = importer.importedTakeInfos[0].name;
            importer.clipAnimations = clips.Select(spec =>
            {
                var parts = spec.Split(':', '-');
                return new ModelImporterClipAnimation { name = parts[0], takeName = take, firstFrame = float.Parse(parts[1]), lastFrame = float.Parse(parts[2]) };
            }).ToArray();
            importer.SaveAndReimport();
            return path;
        }

        [Test]
        public void MotionReferences_ABareModelNeedsOneClip_AndASharedNameNamesNone()
        {
            string model = ModelWithClips(Folder, "Dup:0-1", "Dup:0-10", "Solo:0-5");
            var bare = Assert.Throws<ClipReferenceException>(() => AnimationErrors.ModelClip(model, null));
            StringAssert.Contains("3 clips", bare.Message);
            StringAssert.Contains($"{model}@<clip>", bare.Message);
            StringAssert.Contains("Solo (", bare.Message);
            var dup = Assert.Throws<ClipReferenceException>(() => AnimationErrors.ModelClip(model, "Dup"));
            StringAssert.Contains("2 clips named 'Dup'", dup.Message);
            StringAssert.Contains("1 frame", dup.Message);
            StringAssert.Contains("cliext.anim edit_clips", dup.Message);
            Assert.AreEqual("Solo", AnimationErrors.ModelClip(model, "Solo").name);

            var c = MakeController();
            byte[] before = File.ReadAllBytes(AssetDatabase.GetAssetPath(c));
            foreach (var op in new[]
            {
                new AnimatorOp { op = "set_state", state = "Idle", motion = model },
                new AnimatorOp { op = "set_state", state = "Idle", motion = model + "@Dup" },
                new AnimatorOp { op = "add_blendtree", state = "Move", parameter = "Speed", children = $"{model}@0;{model}@Solo@1" }
            })
            {
                Assert.IsFalse(AnimatorEditing.TryApplyAll(c, new[] { op }, new List<object>(), out _, out string error, out Exception failure), op.motion ?? op.children);
                Assert.AreEqual(Unity.Pipeline.Commands.ExitCode.InvalidUsage, AnimationErrors.BatchExit(failure), error);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(AssetDatabase.GetAssetPath(c)), "A refused motion saved the controller.");
            }
            Assert.IsTrue(AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "set_state", state = "Idle", motion = model + "@Solo" } }, new List<object>(), out _, out string ok), ok);
            Assert.AreEqual("Solo", c.layers[0].stateMachine.states.First(s => s.state.name == "Idle").state.motion.name);
        }

        [Test]
        public void MotionReferences_ABareModelWithOneClip_IsThatClip()
        {
            string model = ModelWithClips(Folder, "Only:0-5");
            var c = MakeController();
            Assert.IsTrue(AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "set_state", state = "Idle", motion = model } }, new List<object>(), out _, out string error), error);
            Assert.AreEqual("Only", c.layers[0].stateMachine.states.First(s => s.state.name == "Idle").state.motion.name);
        }

        [Test]
        public void AddBehaviour_AddsItWithItsFields_AndRemoveBehaviourTakesItOff()
        {
            var c = MakeController();
            string path = AssetDatabase.GetAssetPath(c);
            var results = new List<object>();
            Assert.IsTrue(AnimatorEditing.TryApplyAll(c, new[]
            {
                new AnimatorOp { op = "add_behaviour", state = "Run", type = nameof(CliExtTestBehaviour), set = "damage=12.5;hits=3" },
                new AnimatorOp { op = "add_behaviour", type = typeof(CliExtTestBehaviour).FullName }
            }, results, out _, out string error), error);
            var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            var run = reloaded.layers[0].stateMachine.states.First(s => s.state.name == "Run").state;
            var b = run.behaviours.OfType<CliExtTestBehaviour>().Single();
            Assert.AreEqual(12.5f, b.damage);
            Assert.AreEqual(3, b.Hits);
            Assert.AreEqual(1, reloaded.layers[0].stateMachine.behaviours.OfType<CliExtTestBehaviour>().Count());

            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "add_behaviour", state = "Run", type = "NoSuchBehaviour" } }, new List<object>(), out _, out string missing);
            StringAssert.Contains("No StateMachineBehaviour class 'NoSuchBehaviour'", missing);
            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "add_behaviour", state = "Run", type = nameof(CliExtTestBehaviour), set = "nope=1" } }, new List<object>(), out _, out string field);
            StringAssert.Contains("no serialized field 'nope'", field);
            StringAssert.Contains("damage", field);
            Assert.AreEqual(1, AssetDatabase.LoadAssetAtPath<AnimatorController>(path).layers[0].stateMachine.states.First(s => s.state.name == "Run").state.behaviours.Length);

            Assert.IsTrue(AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "remove_behaviour", state = "Run", type = nameof(CliExtTestBehaviour) } }, new List<object>(), out _, out error), error);
            Assert.AreEqual(0, AssetDatabase.LoadAssetAtPath<AnimatorController>(path).layers[0].stateMachine.states.First(s => s.state.name == "Run").state.behaviours.Length);
            AnimatorEditing.TryApplyAll(c, new[] { new AnimatorOp { op = "remove_behaviour", state = "Run", type = nameof(CliExtTestBehaviour) } }, new List<object>(), out _, out string none);
            StringAssert.Contains("has no CliExtTestBehaviour behaviour", none);
        }
    }
}

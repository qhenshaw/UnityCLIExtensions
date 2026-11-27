using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace UnityCLIExtensions.Splines.Tests
{
    public class SplineEditTests
    {
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _made.Clear();
        }

        private SplineContainer Make(string name = "SplineTestPath")
        {
            var go = new GameObject(name);
            _made.Add(go);
            var c = go.AddComponent<SplineContainer>();
            c.Spline = SplineCommands.BuildSpline("[0,0,0];[1,0,0];[2,0,1]", "Linear", false);
            return c;
        }

        private static string Shape(SplineContainer c) =>
            string.Join("|", c.Splines.Select(s => (s.Closed ? "closed:" : "open:") + string.Join(";", s.Knots.Select((k, i) =>
                $"{k.Position.x:0.###},{k.Position.y:0.###},{k.Position.z:0.###}/{s.GetTangentMode(i)}"))));

        private static string Error(SplineContainer c, SplineOp op) =>
            Assert.Catch<ArgumentException>(() => SplineCommands.Apply(c, op)).Message;

        [Test]
        public void AFailedBatch_LeavesTheSplineAndTheUndoHistoryAsBefore()
        {
            var c = Make();
            string original = Shape(c);
            Assert.IsTrue(SplineCommands.TryApplyAll(c, new[] { new SplineOp { op = "set_knot", index = 1, position = "[1,2,0]" } }, new List<object>(), out _, out string error), error);
            string afterGood = Shape(c);
            Assert.AreNotEqual(original, afterGood);

            var results = new List<object>();
            bool ok = SplineCommands.TryApplyAll(c, new[]
            {
                new SplineOp { op = "add_knot", position = "[5,0,5]" },
                new SplineOp { op = "set_closed" },
                new SplineOp { op = "add_spline", knots = "[0,1,0];[0,2,0]" },
                new SplineOp { op = "remove_knot", index = 40 },
            }, results, out int failedAt, out error);
            Assert.IsFalse(ok);
            Assert.AreEqual(3, failedAt);
            StringAssert.Contains("No knot 40", error);
            Assert.AreEqual(0, results.Count, "No results are reported for a failed batch.");
            Assert.AreEqual(afterGood, Shape(c), "The added knot, the closed flag and the added spline are gone.");

            Undo.PerformUndo();
            Assert.AreEqual(original, Shape(c), "One Undo undoes the good edit before the failed batch: the failed batch left no Undo step.");
            Undo.PerformRedo();
            Assert.AreEqual(afterGood, Shape(c), "Redo brings back the good edit, not the failed batch.");
        }

        [Test]
        public void AGoodBatch_IsOneUndoStep()
        {
            var c = Make();
            string original = Shape(c);
            Assert.IsTrue(SplineCommands.TryApplyAll(c, new[]
            {
                new SplineOp { op = "add_knot", position = "[3,0,0]" },
                new SplineOp { op = "set_closed", value = "true" },
                new SplineOp { op = "set_tangent_mode", mode = "AutoSmooth" },
            }, new List<object>(), out _, out string error), error);
            Assert.AreEqual(4, c.Spline.Count);
            Undo.PerformUndo();
            Assert.AreEqual(original, Shape(c));
        }

        [Test]
        public void OpsThatWouldChangeNothing_Fail()
        {
            var c = Make();
            StringAssert.Contains("would change nothing", Error(c, new SplineOp { op = "set_knot", index = 0 }));
            StringAssert.Contains("already has those values", Error(c, new SplineOp { op = "set_knot", index = 1, position = "[1,0,0]" }));
            StringAssert.Contains("already open", Error(c, new SplineOp { op = "set_closed", value = "false" }));
            StringAssert.Contains("already Linear", Error(c, new SplineOp { op = "set_tangent_mode", mode = "Linear" }));
            StringAssert.Contains("already Linear", Error(c, new SplineOp { op = "set_tangent_mode", mode = "linear", index = 2 }));
        }

        [Test]
        public void BadValuesAndNames_GiveOneLineFixes()
        {
            var c = Make();
            StringAssert.Contains("Did you mean: add_knot", Error(c, new SplineOp { op = "add_knt" }));
            StringAssert.Contains("Use one of: AutoSmooth, Linear", Error(c, new SplineOp { op = "set_tangent_mode", mode = "Smooth" }));
            StringAssert.Contains("No knot 9: spline 0 has 3 (0 to 2)", Error(c, new SplineOp { op = "remove_knot", index = 9 }));
            StringAssert.Contains("remove_knot needs an index", Error(c, new SplineOp { op = "remove_knot" }));
            StringAssert.Contains("No spline 3", Error(c, new SplineOp { op = "add_knot", spline = 3, position = "[0,0,0]" }));
            StringAssert.Contains("add_knot needs a position", Error(c, new SplineOp { op = "add_knot" }));
            StringAssert.Contains("position: '[1,x]' isn't a Vector3 value", Error(c, new SplineOp { op = "set_knot", index = 0, position = "[1,x]" }));
            StringAssert.Contains("value: 'maybe' isn't a Boolean value", Error(c, new SplineOp { op = "set_closed", value = "maybe" }));
            StringAssert.Contains("at least two knots", Error(c, new SplineOp { op = "add_spline", knots = "[0,0,0]" }));
            Assert.AreEqual(3, c.Spline.Count, "No op above changed the spline.");
        }

        [Test]
        public void SampleCount_OpenIncludesBothEnds_ClosedStopsBeforeTheStartAgain()
        {
            CollectionAssert.AreEqual(new[] { 0f, 0.5f, 1f }, SplineCommands.SampleTs(3, false));
            CollectionAssert.AreEqual(new[] { 0f, 0.25f, 0.5f, 0.75f }, SplineCommands.SampleTs(4, true), "t = k/N on a closed spline: no doubled start point.");
        }

        [Test]
        public void UnknownContainer_IsNotFoundWithDidYouMean()
        {
            Make("SplineTestPath");
            var e = Assert.Catch<ArgumentException>(() => SplineCommands.Find("SplineTestPth"));
            Assert.AreEqual("AnimNotFoundException", e.GetType().Name);
            StringAssert.Contains("Did you mean: SplineTestPath", e.Message);
        }
    }
}

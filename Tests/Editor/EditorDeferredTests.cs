using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityCLIExtensions.Tests
{
    public class EditorDeferredTests
    {
        private static IEnumerator WaitUntil(System.Func<bool> condition, double seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (!condition() && EditorApplication.timeSinceStartup < end) yield return null;
        }

        [UnityTest]
        public IEnumerator Run_RunsOnALaterUpdate()
        {
            int runs = 0;
            var handle = EditorDeferred.Run(() => runs++);
            Assert.AreEqual(0, runs, "Nothing runs inside Run itself.");
            yield return WaitUntil(() => !handle.Pending, 10);
            Assert.AreEqual(1, runs);
            Assert.IsTrue(handle.Ran);
        }

        [UnityTest]
        public IEnumerator Run_WaitsForTheCondition()
        {
            bool open = false, ran = false;
            var handle = EditorDeferred.Run(() => ran = true, when: () => open);
            yield return null;
            yield return null;
            Assert.IsFalse(ran, "Waits while the condition is false.");
            open = true;
            yield return WaitUntil(() => !handle.Pending, 10);
            Assert.IsTrue(ran);
        }

        [UnityTest]
        public IEnumerator Run_GivesUpAfterTheTimeout()
        {
            bool ran = false, timedOut = false;
            var handle = EditorDeferred.Run(() => ran = true, when: () => false, timeoutSeconds: 0.2, onTimeout: () => timedOut = true, label: "test");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Deferred call 'test' gave up"));
            yield return WaitUntil(() => !handle.Pending, 10);
            Assert.IsFalse(ran);
            Assert.IsTrue(handle.TimedOut);
            Assert.IsTrue(timedOut);
        }

        [UnityTest]
        public IEnumerator Cancel_DropsTheCall()
        {
            bool ran = false;
            var handle = EditorDeferred.Run(() => ran = true);
            handle.Cancel();
            yield return null;
            yield return null;
            Assert.IsFalse(ran);
            Assert.IsTrue(handle.Cancelled);
        }
    }
}

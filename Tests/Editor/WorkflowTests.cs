using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    public class CompileDiagnosticsTests
    {
        [Test]
        public void Parse_SplitsFileLineColumnCodeAndMessage()
        {
            var m = CompileDiagnostics.Parse("Library/ScriptAssemblies/Assembly-CSharp.dll",
                "Assets/Scripts/Player.cs(12,5): error CS0103: The name 'speed' does not exist in the current context", CompilerMessageType.Error);

            Assert.AreEqual("error", m.severity);
            Assert.AreEqual("Assets/Scripts/Player.cs", m.file);
            Assert.AreEqual(12, m.line);
            Assert.AreEqual(5, m.column);
            Assert.AreEqual("CS0103", m.code);
            Assert.AreEqual("The name 'speed' does not exist in the current context", m.message);
            Assert.AreEqual("Assembly-CSharp.dll", m.assembly);
        }

        [Test]
        public void Parse_KeepsMessagesWithoutAPositionWhole()
        {
            var m = CompileDiagnostics.Parse("A.dll", "warning: something odd happened", CompilerMessageType.Warning);
            Assert.AreEqual("warning", m.severity);
            Assert.IsNull(m.file);
            Assert.AreEqual("warning: something odd happened", m.message);
        }

        [Test]
        public void Parse_HandlesWindowsPaths()
        {
            var m = CompileDiagnostics.Parse("A.dll", @"E:\Pkg\Editor\X.cs(3,10): warning CS0168: The variable 'e' is declared but never used", CompilerMessageType.Warning);
            Assert.AreEqual("E:/Pkg/Editor/X.cs", m.file);
            Assert.AreEqual("CS0168", m.code);
        }
    }

    public class PlayModeTimeTests
    {
        [Test]
        public void IsValid_AcceptsPauseSlowMotionAndUpToTheMaximum_RejectsNegativeAndNaN()
        {
            Assert.IsTrue(PlayModeTime.IsValid(0f));
            Assert.IsTrue(PlayModeTime.IsValid(0.005f));
            Assert.IsTrue(PlayModeTime.IsValid(PlayModeTime.MaxTimeScale));
            Assert.IsFalse(PlayModeTime.IsValid(-0.1f));
            Assert.IsFalse(PlayModeTime.IsValid(PlayModeTime.MaxTimeScale + 1f));
            Assert.IsFalse(PlayModeTime.IsValid(float.NaN));
        }

        [Test]
        public void Set_ReturnsThePreviousTimeScale()
        {
            float before = Time.timeScale;
            try
            {
                PlayModeTime.Set(0.5f);
                Assert.AreEqual(0.5f, PlayModeTime.Set(0.25f), 0.0001f);
                Assert.AreEqual(0.25f, Time.timeScale, 0.0001f);
            }
            finally
            {
                Time.timeScale = before;
            }
        }
    }

    public class StaticStateScannerTests
    {
        private class Unreset
        {
            public static int Score;
            public static event Action Died;
            public static string Name { get; set; }
            public static readonly int[] Table = new int[4];
            public const int Max = 3;
            private static void Use() => Died?.Invoke();
        }

        private class WithReset
        {
            public static int Count;

            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
            private static void ResetStatics() => Count = 0;
        }

        [Test]
        public void ScanTypes_ReportsMutableStaticsAndSkipsConstantsAndReadonly()
        {
            var findings = StaticStateScanner.ScanTypes(new[] { typeof(Unreset) });
            var members = findings.Select(f => f.member + ":" + f.kind).ToArray();

            CollectionAssert.AreEquivalent(new[] { "Score:field", "Died:delegate", "Name:auto-property" }, members);
            Assert.IsTrue(findings.All(f => !f.hasResetHook));
        }

        [Test]
        public void ScanTypes_NotesAResetHookAndItsLoadType()
        {
            var finding = StaticStateScanner.ScanTypes(new[] { typeof(WithReset) }).Single();
            Assert.AreEqual("Count", finding.member);
            Assert.IsTrue(finding.hasResetHook);
            Assert.AreEqual("SubsystemRegistration", finding.resetHookLoadType);
        }

        [Test]
        public void ScanTypes_ListsMembersWithoutAResetFirst()
        {
            var findings = StaticStateScanner.ScanTypes(new[] { typeof(WithReset), typeof(Unreset) });
            Assert.IsFalse(findings.First().hasResetHook);
            Assert.IsTrue(findings.Last().hasResetHook);
        }
    }
}

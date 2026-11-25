using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCLIExtensions.Tests
{
    public class MissingSignatureAnswerTests
    {
        private EditorWindow _window;
        private string _pressed;

        [TearDown]
        public void TearDown()
        {
            if (_window != null) _window.Close();
            _window = null;
            _pressed = null;
        }

        private EditorWindow OpenDialog<T>(string title, string proceedLabel) where T : EditorWindow
        {
            _window = ScriptableObject.CreateInstance<T>();
            _window.titleContent = new GUIContent("CLI Extensions test window");
            foreach (string label in new[] { proceedLabel, "Cancel", "Learn More" })
            {
                string captured = label;
                _window.rootVisualElement.Add(new Button(() => _pressed = captured) { text = label });
            }
            _window.Show();
            // Titled after Show: showing focuses the window, and the real windowFocusChanged hook would answer it first.
            _window.titleContent = new GUIContent(title);
            return _window;
        }

        [TestCase("Import Anyway")]
        [TestCase("Reimport Anyway")]
        public void TryAnswer_MissingSignatureDialog_PressesProceedAndJournalsIt(string proceedLabel)
        {
            DateTime start = DateTime.UtcNow.AddSeconds(-1);
            EditorWindow dialog = OpenDialog<ModalWindowContainer>(MissingSignatureAnswer.DialogTitle, proceedLabel);

            Assert.IsTrue(MissingSignatureAnswer.TryAnswer(dialog));
            Assert.AreEqual(proceedLabel, _pressed);

            var entry = DialogJournal.ReadLast(50, start).LastOrDefault(e => e.title == MissingSignatureAnswer.DialogTitle);
            Assert.IsNotNull(entry, "The answer is in the journal.");
            Assert.AreEqual("answered", entry.kind);
            Assert.AreEqual(proceedLabel, entry.answer);
        }

        [Test]
        public void TryAnswer_SameDialogTwice_PressesOnce()
        {
            EditorWindow dialog = OpenDialog<ModalWindowContainer>(MissingSignatureAnswer.DialogTitle, "Import Anyway");

            Assert.IsTrue(MissingSignatureAnswer.TryAnswer(dialog));
            _pressed = null;
            Assert.IsFalse(MissingSignatureAnswer.TryAnswer(dialog));
            Assert.IsNull(_pressed);
        }

        [Test]
        public void TryAnswer_OtherTrustDialogTitle_PressesNothing()
        {
            EditorWindow dialog = OpenDialog<ModalWindowContainer>("Invalid signature", "Import Anyway");

            Assert.IsFalse(MissingSignatureAnswer.TryAnswer(dialog));
            Assert.IsNull(_pressed);
        }

        [Test]
        public void TryAnswer_OtherWindowType_PressesNothing()
        {
            EditorWindow dialog = OpenDialog<EditorWindow>(MissingSignatureAnswer.DialogTitle, "Import Anyway");

            Assert.IsFalse(MissingSignatureAnswer.TryAnswer(dialog));
            Assert.IsNull(_pressed);
        }

        [Test]
        public void TryAnswer_NoProceedButton_PressesNothing()
        {
            EditorWindow dialog = OpenDialog<ModalWindowContainer>(MissingSignatureAnswer.DialogTitle, "Something Else");

            Assert.IsFalse(MissingSignatureAnswer.TryAnswer(dialog));
            Assert.IsNull(_pressed);
        }

        [Test]
        public void TryAnswer_Null_ReturnsFalse()
        {
            Assert.IsFalse(MissingSignatureAnswer.TryAnswer(null));
        }
    }
}

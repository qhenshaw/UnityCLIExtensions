using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Presses <c>Import Anyway</c> (or <c>Reimport Anyway</c>) on Unity's "Missing signature" dialog, which Unity 6.3 and later show before importing a
    /// .unitypackage that has no signature (anything not from the Asset Store, including packages exported locally).
    /// <para>
    /// The dialog is a Package Manager UI Toolkit modal (<c>ModalWindowContainer</c>), so it has no Win32 buttons for the
    /// watchdog to press, and while it is open the Editor's update loop and the Pipeline server are stopped. Unity still
    /// raises <see cref="EditorWindow.windowFocusChanged"/> when the modal takes focus, also with the Editor in the
    /// background, so this presses the dialog's own button from there, with a navigation submit, the same way
    /// <c>cliext.uitk click</c> does.
    /// </para>
    /// <para>
    /// It always runs, with or without agent mode. It answers only the dialog titled exactly "Missing signature"; the trust
    /// dialogs for an invalid or untrusted signature have other titles and are left for a person. Every answer is written
    /// to the dialog journal and the Console.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class MissingSignatureAnswer
    {
        /// <summary>The dialog's window title.</summary>
        public const string DialogTitle = "Missing signature";

        /// <summary>The labels of the button this presses: Import Anyway, or Reimport Anyway when the assets are already in the project.</summary>
        public static readonly string[] ProceedLabels = { "Import Anyway", "Reimport Anyway" };

        private const string ContainerTypeName = "ModalWindowContainer";
        private const int MaxMessageLength = 500;

        private static EditorWindow s_LastAnswered;

        static MissingSignatureAnswer()
        {
            EditorWindow.windowFocusChanged -= OnWindowFocusChanged;
            EditorWindow.windowFocusChanged += OnWindowFocusChanged;
        }

        private static void OnWindowFocusChanged()
        {
            try { TryAnswer(EditorWindow.focusedWindow); }
            catch (System.Exception e) { Debug.LogWarning($"[CLI Extensions] Could not answer the {DialogTitle} dialog: {e.Message}"); }
        }

        /// <summary>True when <paramref name="window"/> is Unity's "Missing signature" dialog.</summary>
        public static bool IsMissingSignatureDialog(EditorWindow window)
        {
            return window != null && window.GetType().Name == ContainerTypeName && window.titleContent.text == DialogTitle;
        }

        /// <summary>
        /// Presses the <see cref="ProceedLabels"/> button when <paramref name="window"/> is the "Missing signature" dialog. Returns
        /// true when it pressed it. Each dialog is answered once.
        /// </summary>
        public static bool TryAnswer(EditorWindow window)
        {
            if (!IsMissingSignatureDialog(window)) return false;
            if (ReferenceEquals(window, s_LastAnswered)) return false;
            s_LastAnswered = window;

            List<Button> buttons = window.rootVisualElement.Query<Button>().ToList();
            Button proceed = buttons.FirstOrDefault(b => ProceedLabels.Contains(b.text));
            string[] labels = buttons.Select(b => b.text).ToArray();
            string message = Describe(window.rootVisualElement);

            if (proceed == null || !proceed.enabledInHierarchy)
            {
                DialogJournal.Write(new DialogJournalEntry
                {
                    kind = "escalated", title = DialogTitle, message = message, buttons = labels,
                    detail = $"{nameof(MissingSignatureAnswer)}: no enabled '{string.Join("' or '", ProceedLabels)}' button"
                });
                return false;
            }

            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = proceed;
                proceed.SendEvent(submit);
            }

            DialogJournal.Write(new DialogJournalEntry
            {
                kind = "answered", title = DialogTitle, message = message, buttons = labels, answer = proceed.text,
                detail = nameof(MissingSignatureAnswer)
            });
            Debug.Log($"[CLI Extensions] Pressed '{proceed.text}' on Unity's '{DialogTitle}' dialog. {message}");
            return true;
        }

        // The dialog's text (help box, headers and the package rows), for the journal.
        private static string Describe(VisualElement root)
        {
            IEnumerable<string> texts = root.Query<Label>().ToList()
                .Where(l => l.visible && !string.IsNullOrWhiteSpace(l.text))
                .Select(l => l.text.Trim());
            string message = string.Join(" | ", texts);
            return message.Length > MaxMessageLength ? message.Substring(0, MaxMessageLength) + "..." : message;
        }
    }
}

using System.Diagnostics;
using UnityEditor;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Drop-in replacements for <see cref="EditorUtility.DisplayDialog(string,string,string,string)"/> and
    /// <see cref="EditorUtility.DisplayDialogComplex"/> that never block an agent. With a person at the Editor they show
    /// the dialog as usual. In agent mode (see <see cref="AgentMode"/>) they return the headless answer you give at the
    /// call site, and log the dialog to the journal and the Console, so the agent can see what was decided for it.
    /// </summary>
    /// <example>
    /// <code>
    /// if (AgentDialog.Display("Rebuild atlas?", "This takes a minute.", "Rebuild", "Skip", headlessAnswer: true))
    ///     RebuildAtlas();
    /// </code>
    /// </example>
    public static class AgentDialog
    {
        /// <summary>True when dialogs are answered without being shown: agent mode is on and the setting allows it.</summary>
        public static bool AnswersHeadless => AgentMode.IsActive && CliExtSettings.instance.AnswerProjectDialogs;

        /// <summary>Like <see cref="EditorUtility.DisplayDialog(string,string,string,string)"/>; returns <paramref name="headlessAnswer"/> in agent mode.</summary>
        /// <param name="headlessAnswer">True means <paramref name="ok"/>, false means <paramref name="cancel"/>. Pick the safe choice.</param>
        public static bool Display(string title, string message, string ok, string cancel = "", bool headlessAnswer = true)
        {
            if (!AnswersHeadless)
                return string.IsNullOrEmpty(cancel)
                    ? EditorUtility.DisplayDialog(title, message, ok)
                    : EditorUtility.DisplayDialog(title, message, ok, cancel);

            Log(title, message, string.IsNullOrEmpty(cancel) ? new[] { ok } : new[] { ok, cancel },
                headlessAnswer || string.IsNullOrEmpty(cancel) ? ok : cancel);
            return headlessAnswer;
        }

        /// <summary>
        /// Like <see cref="EditorUtility.DisplayDialogComplex"/> (0 = ok, 1 = cancel, 2 = alt); returns
        /// <paramref name="headlessAnswer"/> in agent mode.
        /// </summary>
        public static int DisplayComplex(string title, string message, string ok, string cancel, string alt, int headlessAnswer = 1)
        {
            if (!AnswersHeadless)
                return EditorUtility.DisplayDialogComplex(title, message, ok, cancel, alt);

            string[] buttons = { ok, cancel, alt };
            int answer = headlessAnswer < 0 || headlessAnswer > 2 ? 1 : headlessAnswer;
            Log(title, message, buttons, buttons[answer]);
            return answer;
        }

        private static void Log(string title, string message, string[] buttons, string answer)
        {
            string caller = CallerName();
            DialogJournal.Write(new DialogJournalEntry
            {
                kind = "answered",
                title = title,
                message = message,
                buttons = buttons,
                answer = answer,
                detail = caller
            });
            UnityEngine.Debug.Log($"[CLI Extensions] Agent mode answered \"{title}\" with \"{answer}\" ({caller}).\n{message}");
        }

        private static string CallerName()
        {
            // Frame 0 is CallerName, 1 is Log, 2 is Display/DisplayComplex, 3 is the code that asked.
            var frame = new StackTrace(3, false).GetFrame(0);
            var method = frame?.GetMethod();
            return method == null ? "unknown" : $"{method.DeclaringType?.FullName}.{method.Name}";
        }
    }
}

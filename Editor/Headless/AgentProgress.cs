using System.Collections.Generic;
using UnityEditor;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Drop-in replacements for <see cref="EditorUtility.DisplayProgressBar"/>,
    /// <see cref="EditorUtility.DisplayCancelableProgressBar"/> and <see cref="EditorUtility.ClearProgressBar"/>.
    /// The Editor's progress bar is a modal window: while it is up, nothing else gets the main thread and a crashed
    /// caller can leave it on screen. In agent mode these report through the background <see cref="Progress"/> API
    /// instead (visible in the status bar and to <c>unity command</c> progress), and log the first report of each task.
    /// </summary>
    public static class AgentProgress
    {
        private static readonly Dictionary<string, int> s_Ids = new Dictionary<string, int>();

        /// <summary>Like <see cref="EditorUtility.DisplayProgressBar"/>.</summary>
        public static void Show(string title, string info, float progress)
        {
            if (!AgentDialog.AnswersHeadless)
            {
                EditorUtility.DisplayProgressBar(title, info, progress);
                return;
            }
            Report(title, info, progress);
        }

        /// <summary>Like <see cref="EditorUtility.DisplayCancelableProgressBar"/>. Never reports a cancel in agent mode.</summary>
        public static bool ShowCancelable(string title, string info, float progress)
        {
            if (!AgentDialog.AnswersHeadless)
                return EditorUtility.DisplayCancelableProgressBar(title, info, progress);
            Report(title, info, progress);
            return false;
        }

        /// <summary>Like <see cref="EditorUtility.ClearProgressBar"/>: ends every task started through this class.</summary>
        public static void Clear()
        {
            EditorUtility.ClearProgressBar();
            foreach (int id in s_Ids.Values)
                if (Progress.Exists(id)) Progress.Remove(id);
            s_Ids.Clear();
        }

        private static void Report(string title, string info, float progress)
        {
            if (!s_Ids.TryGetValue(title, out int id) || !Progress.Exists(id))
            {
                id = Progress.Start(title, info);
                s_Ids[title] = id;
                DialogJournal.Write(new DialogJournalEntry { kind = "progress", title = title, message = info });
            }
            Progress.Report(id, progress, info);
        }
    }
}

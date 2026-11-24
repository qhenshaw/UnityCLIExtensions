#if UNITY_6000_7_OR_NEWER
using System.Linq;
using UnityEditor;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Unity 6.7 and later report every native and EditorWindow modal through <c>EditorDialogEvents</c>. This writes each
    /// one to the dialog journal, so the agent sees dialogs from Unity and packages too, not only AgentDialog's.
    /// (Pipeline 1.0 also uses these events for its <c>blocked_by_dialog</c> status on the same versions.)
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorDialogLogger
    {
        static EditorDialogLogger()
        {
            EditorDialogEvents.dialogWillShow -= OnShow;
            EditorDialogEvents.dialogWillShow += OnShow;
        }

        private static void OnShow(DialogEventInfo info)
        {
            DialogJournal.Write(new DialogJournalEntry
            {
                kind = "shown",
                title = info.Title,
                message = info.Message,
                buttons = info.ButtonLabels?.ToArray(),
                detail = info.Source.ToString()
            });
        }
    }
}
#endif

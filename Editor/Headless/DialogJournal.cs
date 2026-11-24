using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>One line of <c>Library/CLIExtensions/dialogs.jsonl</c>.</summary>
    [Serializable]
    public class DialogJournalEntry
    {
        /// <summary>UTC time, ISO 8601.</summary>
        public string time;
        /// <summary>Who wrote it: <c>editor</c> (AgentDialog, EditorDialogEvents) or <c>watchdog</c>.</summary>
        public string source;
        /// <summary>
        /// What happened: <c>answered</c> (agent mode answered without showing it), <c>shown</c> (a dialog opened),
        /// <c>clicked</c> (the watchdog pressed a button), <c>escalated</c> (nobody may answer it automatically),
        /// <c>progress</c> (a progress bar was turned into a background Progress item).
        /// </summary>
        public string kind;
        public string title;
        public string message;
        public string[] buttons;
        /// <summary>The button pressed or the answer returned, when there was one.</summary>
        public string answer;
        /// <summary>The policy rule or calling method, when known.</summary>
        public string detail;
    }

    /// <summary>Appends to and reads the dialog journal shared by the Editor and the watchdog.</summary>
    public static class DialogJournal
    {
        private static readonly object s_Gate = new object();

        /// <summary>Raised on the writing thread after an entry is appended by the Editor.</summary>
        public static event Action<DialogJournalEntry> Written;

        /// <summary>Appends one entry, stamping its time. Never throws: a journal failure must not break the caller.</summary>
        public static void Write(DialogJournalEntry entry)
        {
            entry.time ??= DateTime.UtcNow.ToString("o");
            entry.source ??= "editor";
            try
            {
                string line = JsonUtility.ToJson(entry) + "\n";
                lock (s_Gate)
                {
                    using var stream = new FileStream(CliExtPaths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.Write(line);
                }
                Written?.Invoke(entry);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CLI Extensions] Could not write the dialog journal: {e.Message}");
            }
        }

        /// <summary>The last <paramref name="count"/> entries, oldest first, optionally only those at or after <paramref name="sinceUtc"/>.</summary>
        public static List<DialogJournalEntry> ReadLast(int count, DateTime? sinceUtc = null)
        {
            var result = new List<DialogJournalEntry>();
            string path = CliExtPaths.JournalFile;
            if (!File.Exists(path)) return result;

            string[] lines;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                lines = reader.ReadToEnd().Split('\n');

            for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                DialogJournalEntry entry;
                try { entry = JsonUtility.FromJson<DialogJournalEntry>(line); }
                catch { continue; }
                if (entry == null) continue;
                if (sinceUtc.HasValue && DateTime.TryParse(entry.time, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)
                    && t.ToUniversalTime() < sinceUtc.Value) break;
                result.Add(entry);
            }
            result.Reverse();
            return result;
        }

        /// <summary>Deletes the journal.</summary>
        public static void Clear()
        {
            lock (s_Gate)
            {
                if (File.Exists(CliExtPaths.JournalFile)) File.Delete(CliExtPaths.JournalFile);
            }
        }
    }
}

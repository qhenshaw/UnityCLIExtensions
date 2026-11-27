using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// One line of <c>Library/CLIExtensions/field-notes.jsonl</c>: a problem an agent met with a cliext command in real
    /// use that the failure log can't see (it succeeded the slow way, worked around a command, or missed one).
    /// </summary>
    [Serializable]
    public class FieldNote
    {
        /// <summary>UTC time, ISO 8601.</summary>
        public string time;
        /// <summary>One of <see cref="FieldNotes.Kinds"/>.</summary>
        public string kind;
        /// <summary>The cliext command or area it is about (<c>cliext.vfx edit</c>); may be empty.</summary>
        public string command;
        /// <summary>What happened, one line.</summary>
        public string what;
        /// <summary>What the agent did instead, one line; may be empty.</summary>
        public string workaround;
        public string unity;
        public string pipeline;
        public string package;
    }

    /// <summary>
    /// The local field notes, written by <c>cliext.failures note</c>. Only the note's own text fields, the time and the
    /// versions are stored: never the parameter values of the command the note is about. Nothing leaves the machine.
    /// </summary>
    public static class FieldNotes
    {
        /// <summary>The kinds a note can have, in the order the help lists them.</summary>
        public static readonly string[] Kinds = { "bug", "workaround", "missing", "docs", "slow" };

        /// <summary>The longest text kept per field; longer text is cut and ends in "...".</summary>
        public const int MaxFieldLength = 2000;

        private static readonly object s_Gate = new object();

        public static string LogFile => Path.Combine(CliExtPaths.StateDirectory, "field-notes.jsonl");

        /// <summary>The kind as stored (lower case), or null when it isn't one of <see cref="Kinds"/>.</summary>
        public static string NormalizeKind(string kind)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            return Kinds.Contains(k) ? k : null;
        }

        /// <summary>Text as one line: runs of whitespace (line breaks included) become one space, cut at <see cref="MaxFieldLength"/>.</summary>
        public static string OneLine(string text)
        {
            string s = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return s.Length > MaxFieldLength ? s.Substring(0, MaxFieldLength - 3) + "..." : s;
        }

        /// <summary>
        /// Appends one note, stamping its time and versions and making each text field one line. Throws
        /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file can't be written.
        /// </summary>
        public static FieldNote Write(FieldNote note)
        {
            var entry = new FieldNote
            {
                time = string.IsNullOrEmpty(note.time) ? DateTime.UtcNow.ToString("o") : note.time,
                kind = OneLine(note.kind),
                command = OneLine(note.command),
                what = OneLine(note.what),
                workaround = OneLine(note.workaround),
                unity = FailureLog.UnityVersion,
                pipeline = FailureLog.PipelineVersion,
                package = FailureLog.PackageVersion,
            };
            string line = JsonUtility.ToJson(entry) + "\n";
            lock (s_Gate)
            {
                Directory.CreateDirectory(CliExtPaths.StateDirectory);
                using var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write(line);
            }
            return entry;
        }

        /// <summary>The last <paramref name="count"/> notes, oldest first, optionally only those at or after <paramref name="sinceUtc"/>.</summary>
        public static List<FieldNote> ReadLast(int count, DateTime? sinceUtc = null) =>
            FailureLog.ReadLastLines<FieldNote>(LogFile, count, sinceUtc, n => n.time);

        /// <summary>Deletes the notes.</summary>
        public static void Clear()
        {
            lock (s_Gate)
            {
                if (File.Exists(LogFile)) File.Delete(LogFile);
            }
        }
    }
}

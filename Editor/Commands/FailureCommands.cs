using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// <c>cliext.failures</c>: read and clear the local failure log (<see cref="FailureLog"/>) and the field notes
    /// (<see cref="FieldNotes"/>), and build a bug report for the package author (<see cref="BugReport"/>).
    /// </summary>
    internal static class FailureCommands
    {
        private const string Group = "cliext.failures";

        [CliGroupCommand(Group, "The local failure log: every cliext command that reported an error, with the parameter names it was given (no values), the error, the package's stack frames and the versions. Oldest first. " +
                                "--notes true lists the field notes (from cliext.failures note) instead. Nothing leaves this machine. A bad --since exits 2.",
            Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Log(
            CommandContext cmd,
            [CliArg("tail", "Most recent entries to return")] int tail = 20,
            [CliArg("since", "Only entries at or after this UTC time (ISO 8601)")] string since = "",
            [CliArg("notes", "List the field notes (field-notes.jsonl) instead of the failures")] bool notes = false)
        {
            if (!TryParseSince(cmd, since, out DateTime? sinceUtc)) return ExitCode.InvalidUsage;
            if (notes)
                foreach (var note in FieldNotes.ReadLast(Math.Max(1, tail), sinceUtc))
                    cmd.Result.WriteRecord(note);
            else
                foreach (var entry in FailureLog.ReadLast(Math.Max(1, tail), sinceUtc))
                    cmd.Result.WriteRecord(entry);
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "The failure log grouped by command, exception type and top stack frame (source failure), then the field notes grouped by kind and command (source note): how often each, when last, and the last error or note. " +
                                "Most frequent first within each source. A bad --since exits 2.",
            Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Summary(
            CommandContext cmd,
            [CliArg("since", "Only entries at or after this UTC time (ISO 8601)")] string since = "")
        {
            if (!TryParseSince(cmd, since, out DateTime? sinceUtc)) return ExitCode.InvalidUsage;
            var groups = FailureLog.ReadLast(int.MaxValue, sinceUtc)
                .GroupBy(e => (e.command, e.exception, top: e.frames != null && e.frames.Length > 0 ? e.frames[0] : ""))
                .Select(g => new
                {
                    source = "failure",
                    command = g.Key.command,
                    exception = g.Key.exception,
                    topFrame = g.Key.top,
                    count = g.Count(),
                    lastTime = g.Last().time,
                    lastMessage = g.Last().message,
                })
                .OrderByDescending(g => g.count).ThenBy(g => g.command, StringComparer.Ordinal);
            foreach (var g in groups) cmd.Result.WriteRecord(g);
            foreach (var g in SummarizeNotes(FieldNotes.ReadLast(int.MaxValue, sinceUtc))) cmd.Result.WriteRecord(g);
            return ExitCode.Success;
        }

        /// <summary>One record per kind and command of the notes, most frequent first.</summary>
        internal static NoteGroup[] SummarizeNotes(System.Collections.Generic.IEnumerable<FieldNote> notes) =>
            notes.GroupBy(n => (kind: n.kind ?? "", command: n.command ?? ""))
                .Select(g => new NoteGroup
                {
                    source = "note",
                    kind = g.Key.kind,
                    command = g.Key.command,
                    count = g.Count(),
                    lastTime = g.Last().time,
                    lastWhat = g.Last().what,
                    lastWorkaround = g.Last().workaround,
                })
                .OrderByDescending(g => g.count).ThenBy(g => g.kind, StringComparer.Ordinal).ThenBy(g => g.command, StringComparer.Ordinal)
                .ToArray();

        /// <summary>A summary record for the field notes of one kind and command.</summary>
        [Serializable]
        internal sealed class NoteGroup
        {
            public string source;
            public string kind;
            public string command;
            public int count;
            public string lastTime;
            public string lastWhat;
            public string lastWorkaround;
        }

        /// <summary>The fields a --file note holds; anything else in the file is ignored.</summary>
        [Serializable]
        internal sealed class NoteFile
        {
            public string kind;
            public string command;
            public string what;
            public string workaround;
        }

        [CliGroupCommand(Group, "Record a field note in Library/CLIExtensions/field-notes.jsonl: a problem with a cliext command that the failure log can't see. " +
                                "Use it before moving on when a cliext command fails in a way its error doesn't explain, when you work around one (hand-editing a file a cliext command edits, " +
                                "a script eval where a command should exist, reading the package source or README to learn a command), or when a command you need doesn't exist. " +
                                "Give --kind and --what (--command and --workaround are optional), or --file with a JSON object holding the same fields " +
                                "(use it on Windows PowerShell 5.1, which mangles quoted text); a flag given with --file replaces that field. " +
                                "Only these fields, the time and the Unity, Pipeline and package versions are stored, and the reply is the stored note. " +
                                "Exit 0 when recorded; an unknown --kind (the error lists the kinds), no --what, or a --file that isn't a JSON object exits 2; " +
                                "a --file that can't be read exits 13; a notes file that can't be written exits 6.",
            Name = "note", Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.Destructive)]
        internal static ExitCode Note(
            CommandContext cmd,
            [CliArg("kind", "bug, workaround, missing, docs or slow")] string kind = "",
            [CliArg("command", "The cliext command or area the note is about, e.g. cliext.vfx edit (optional)")] string command = "",
            [CliArg("what", "What happened, in one line")] string what = "",
            [CliArg("workaround", "What you did instead, in one line (optional)")] string workaround = "",
            [CliArg("file", "A JSON file with kind, command, what and workaround, instead of the flags (relative to the project root unless absolute)")] string file = "")
        {
            FieldNote note = PrepareNote(kind, command, what, workaround, file, out string error, out ExitCode code);
            if (note == null)
            {
                cmd.Fail(error);
                return code;
            }

            FieldNote stored;
            try { stored = FieldNotes.Write(note); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                cmd.Fail($"Can't write {FieldNotes.LogFile}: {e.Message}");
                return ExitCode.Failed;
            }
            cmd.Result.Write(new
            {
                recorded = true,
                stored.time,
                stored.kind,
                stored.command,
                stored.what,
                stored.workaround,
                stored.unity,
                stored.pipeline,
                stored.package,
                file = FieldNotes.LogFile,
            });
            return ExitCode.Success;
        }

        /// <summary>
        /// The note <c>cliext.failures note</c> would store, from the flags and the optional --file (a flag replaces the
        /// file's field), with the kind checked and lower-cased. Null with the error and exit code when it can't be made.
        /// </summary>
        internal static FieldNote PrepareNote(string kind, string command, string what, string workaround, string file, out string error, out ExitCode code)
        {
            error = null;
            code = ExitCode.Success;
            var note = new FieldNote();
            if (!string.IsNullOrWhiteSpace(file))
            {
                string path = Path.IsPathRooted(file) ? file : Path.Combine(CliExtPaths.ProjectRoot, file);
                string json;
                try { json = File.ReadAllText(path); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                {
                    error = $"Can't read --file '{file}': {e.Message}";
                    code = ExitCode.NotFound;
                    return null;
                }
                string trimmed = json.Trim().TrimStart('\uFEFF').Trim();
                NoteFile parsed = null;
                if (trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    try { parsed = JsonUtility.FromJson<NoteFile>(trimmed); }
                    catch (ArgumentException) { }
                }
                if (parsed == null)
                {
                    error = $"--file '{file}' isn't a JSON object. Write {{\"kind\": \"workaround\", \"command\": \"cliext.vfx edit\", \"what\": \"...\", \"workaround\": \"...\"}}.";
                    code = ExitCode.InvalidUsage;
                    return null;
                }
                note.kind = parsed.kind;
                note.command = parsed.command;
                note.what = parsed.what;
                note.workaround = parsed.workaround;
            }
            if (!string.IsNullOrWhiteSpace(kind)) note.kind = kind;
            if (!string.IsNullOrWhiteSpace(command)) note.command = command;
            if (!string.IsNullOrWhiteSpace(what)) note.what = what;
            if (!string.IsNullOrWhiteSpace(workaround)) note.workaround = workaround;

            string valid = string.Join(", ", FieldNotes.Kinds);
            string normalized = FieldNotes.NormalizeKind(note.kind);
            if (normalized == null)
            {
                error = string.IsNullOrWhiteSpace(note.kind)
                    ? $"Pass --kind (or kind in --file): one of {valid}."
                    : $"Unknown kind '{note.kind}'. Use one of: {valid}.";
                code = ExitCode.InvalidUsage;
                return null;
            }
            note.kind = normalized;
            if (string.IsNullOrWhiteSpace(note.what))
            {
                error = "Pass --what (or what in --file): what happened, in one line.";
                code = ExitCode.InvalidUsage;
                return null;
            }
            return note;
        }

        [CliGroupCommand(Group, "Delete the local failure log, or with --notes true the field notes instead. The reply names the file it cleared.",
            Name = "clear", Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.Destructive)]
        internal static ExitCode Clear(
            CommandContext cmd,
            [CliArg("notes", "Delete the field notes (field-notes.jsonl) instead of the failure log")] bool notes = false)
        {
            if (notes) FieldNotes.Clear();
            else FailureLog.Clear();
            cmd.Result.Write(new { cleared = true, file = notes ? FieldNotes.LogFile : FailureLog.LogFile });
            return ExitCode.Success;
        }

        // ReadOnly: it changes nothing in the project or the Editor; the zip goes to CLIExtensionsReports/, outside Assets.
        [CliGroupCommand(Group, "Build a bug report for the package author: starts Tools~/cliext-report.ps1 out of process and returns at once with the zip's path, " +
                                "<project>/CLIExtensionsReports/cliext-report-<time>.zip, which appears when the script finishes (usually under a minute). " +
                                "The zip holds the failure log, the field notes, the dialog journal, the watchdog status, versions, the manifest, the installed skills' stamps, " +
                                "the CLAUDE.md block, the end of Editor.log and excerpts of the agent transcripts around each note and failure. Nothing is uploaded. " +
                                "Tools > CLI Extensions > Create Bug Report does the same. Exit 0 when started; --days below 1 exits 2; " +
                                "a missing script, a non-Windows Editor or a script that can't start exits 6.",
            Name = "report", Tags = new[] { "editor", "cliext", "observability" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Report(
            CommandContext cmd,
            [CliArg("days", "How many days of transcripts, failures and notes to include")] int days = 14,
            [CliArg("open-folder", "Open Explorer at the zip when it is done")] bool openFolder = true)
        {
            if (days < 1)
            {
                cmd.Fail($"--days {days}: give 1 or more.");
                return ExitCode.InvalidUsage;
            }
            string error = BugReport.Launch(days, openFolder, out string zip);
            if (error != null)
            {
                cmd.Fail(error);
                return ExitCode.Failed;
            }
            cmd.Result.Write(new { started = true, zip, note = "The script runs out of process; the zip is complete when this file exists." });
            return ExitCode.Success;
        }

        private static bool TryParseSince(CommandContext cmd, string since, out DateTime? sinceUtc)
        {
            sinceUtc = null;
            if (string.IsNullOrEmpty(since)) return true;
            if (DateTime.TryParse(since, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
            {
                sinceUtc = parsed;
                return true;
            }
            cmd.Fail($"'{since}' is not a date and time. Use ISO 8601, e.g. 2026-11-24T16:00:00Z.");
            return false;
        }
    }
}

using System;
using System.Globalization;
using System.Linq;
using Unity.Pipeline.Commands;

namespace UnityCLIExtensions
{
    /// <summary><c>cliext.failures</c>: read and clear the local failure log (<see cref="FailureLog"/>).</summary>
    internal static class FailureCommands
    {
        private const string Group = "cliext.failures";

        [CliGroupCommand(Group, "The local failure log: every cliext command that reported an error, with the parameter names it was given (no values), the error, the package's stack frames and the versions. Oldest first. Nothing leaves this machine.",
            Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Log(
            CommandContext cmd,
            [CliArg("tail", "Most recent entries to return")] int tail = 20,
            [CliArg("since", "Only entries at or after this UTC time (ISO 8601)")] string since = "")
        {
            if (!TryParseSince(cmd, since, out DateTime? sinceUtc)) return ExitCode.InvalidUsage;
            foreach (var entry in FailureLog.ReadLast(Math.Max(1, tail), sinceUtc))
                cmd.Result.WriteRecord(entry);
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "The failure log grouped by command, exception type and top stack frame: how often each failed, when last, and the last error. Most frequent first.",
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
                    command = g.Key.command,
                    exception = g.Key.exception,
                    topFrame = g.Key.top,
                    count = g.Count(),
                    lastTime = g.Last().time,
                    lastMessage = g.Last().message,
                })
                .OrderByDescending(g => g.count).ThenBy(g => g.command, StringComparer.Ordinal);
            foreach (var g in groups) cmd.Result.WriteRecord(g);
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Delete the local failure log.",
            Name = "clear", Tags = new[] { "editor", "cliext", "observability" }, MainThreadRequired = false, Effect = CommandEffect.Destructive)]
        internal static ExitCode Clear(CommandContext cmd)
        {
            FailureLog.Clear();
            cmd.Result.Write(new { cleared = true });
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

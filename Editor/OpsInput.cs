using System;
using System.IO;
using Unity.Pipeline.Commands;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Reads the ops of a batch edit from --ops or --ops-file, the same way for every command: exactly one of the two,
    /// else invalid usage (exit 2), before the command looks at its target.
    /// </summary>
    public static class OpsInput
    {
        public static bool TryRead(CommandContext cmd, string ops, string opsFile, out string json, out ExitCode failure)
        {
            json = null;
            failure = ExitCode.Success;
            bool hasOps = !string.IsNullOrWhiteSpace(ops);
            bool hasFile = !string.IsNullOrWhiteSpace(opsFile);
            if (hasOps && hasFile)
            {
                cmd.Fail("Pass --ops or --ops-file, not both.");
                failure = ExitCode.InvalidUsage;
                return false;
            }
            if (!hasOps && !hasFile)
            {
                cmd.Fail("Pass --ops (a JSON array of ops) or --ops-file (a file holding it; use it on Windows PowerShell 5.1, which drops the quotes inside inline JSON).");
                failure = ExitCode.InvalidUsage;
                return false;
            }
            if (hasFile)
            {
                try { json = File.ReadAllText(opsFile); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                {
                    cmd.Fail($"Can't read --ops-file: {e.Message}");
                    failure = ExitCode.NotFound;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(json))
                {
                    cmd.Fail($"--ops-file '{opsFile}' is empty.");
                    failure = ExitCode.InvalidUsage;
                    return false;
                }
            }
            else json = ops;
            return true;
        }
    }
}

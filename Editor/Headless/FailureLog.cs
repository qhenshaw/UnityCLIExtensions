using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>One line of <c>Library/CLIExtensions/failures.jsonl</c>: a cliext command that reported an error.</summary>
    [Serializable]
    public class FailureLogEntry
    {
        /// <summary>UTC time, ISO 8601.</summary>
        public string time;
        /// <summary>The command, as typed: <c>cliext.vfx edit</c>. Empty when it couldn't be told from the stack.</summary>
        public string command;
        /// <summary>The parameter names it was given, without their values.</summary>
        public string[] parameters;
        /// <summary>The error it reported.</summary>
        public string message;
        /// <summary>The exception type, for an exception that escaped a command; empty otherwise.</summary>
        public string exception;
        /// <summary>The package's own stack frames, innermost first (Type.Method).</summary>
        public string[] frames;
        public string unity;
        public string pipeline;
        public string package;
        public string os;
        public bool agentMode;
    }

    /// <summary>
    /// The local failure log. Every cliext command reports its errors through <see cref="Fail"/>, which writes the error
    /// as <c>cmd.Log.Error</c> did and appends a line here. Pipeline's own hook for a finished command is internal, so
    /// this is where the package can see a failure. Nothing leaves the machine.
    /// </summary>
    [InitializeOnLoad]
    public static class FailureLog
    {
        private static readonly object s_Gate = new object();
        private static readonly string s_Unity = Application.unityVersion;
        private static readonly string s_Os = SystemInfo.operatingSystem;
        private static string s_Pipeline, s_Package;

        public static string LogFile => Path.Combine(CliExtPaths.StateDirectory, "failures.jsonl");

        /// <summary>The versions every entry is stamped with, read once when the package loads (also used by <see cref="FieldNotes"/>).</summary>
        internal static string UnityVersion => s_Unity;
        internal static string PipelineVersion => s_Pipeline;
        internal static string PackageVersion => s_Package;

        static FailureLog()
        {
            // PackageInfo wants the main thread; some commands fail off it, so read the versions once here.
            s_Pipeline = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CommandContext).Assembly)?.version;
            s_Package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FailureLog).Assembly)?.version;
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        /// <summary>Reports a command's error (as <c>cmd.Log.Error</c>) and records it in the failure log.</summary>
        public static void Fail(this CommandContext cmd, string message)
        {
            cmd.Log.Error(message);
            IReadOnlyList<string> args = null;
            try { args = cmd.Arguments; } catch { }
            Write(new FailureLogEntry
            {
                command = CommandFromStack(new StackTrace(1, false)),
                parameters = ParameterNames(args),
                message = message,
                frames = PackageFrames(new StackTrace(1, false)),
            });
        }

        // An exception that escaped a cliext command reaches Unity's log through Pipeline's executor.
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || stackTrace == null || !stackTrace.Contains("UnityCLIExtensions.")) return;
            var frames = Regex.Matches(stackTrace, @"(UnityCLIExtensions[\w.]*?)[:.](\w+) \(").Cast<Match>()
                .Select(m => m.Groups[1].Value.Split('.').Last() + "." + m.Groups[2].Value).Distinct().Take(8).ToArray();
            int colon = condition.IndexOf(':');
            Write(new FailureLogEntry
            {
                message = colon > 0 ? condition.Substring(colon + 1).Trim() : condition,
                exception = colon > 0 ? condition.Substring(0, colon).Trim() : "Exception",
                frames = frames,
            });
        }

        /// <summary>Appends one entry, stamping its time and versions. Never throws.</summary>
        public static void Write(FailureLogEntry entry)
        {
            entry.time ??= DateTime.UtcNow.ToString("o");
            entry.command ??= "";
            entry.exception ??= "";
            entry.parameters ??= new string[0];
            entry.frames ??= new string[0];
            entry.unity = s_Unity;
            entry.os = s_Os;
            entry.pipeline = s_Pipeline;
            entry.package = s_Package;
            try { entry.agentMode = AgentMode.IsActive; } catch { }
            try
            {
                string line = JsonUtility.ToJson(entry) + "\n";
                lock (s_Gate)
                {
                    Directory.CreateDirectory(CliExtPaths.StateDirectory);
                    using var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.Write(line);
                }
            }
            catch { /* the log must never break the command */ }
        }

        /// <summary>The last <paramref name="count"/> entries, oldest first, optionally only those at or after <paramref name="sinceUtc"/>.</summary>
        public static List<FailureLogEntry> ReadLast(int count, DateTime? sinceUtc = null) =>
            ReadLastLines<FailureLogEntry>(LogFile, count, sinceUtc, e => e.time);

        /// <summary>
        /// The last <paramref name="count"/> objects of a JSON-lines file, oldest first, optionally only those whose time
        /// (from <paramref name="timeOf"/>) is at or after <paramref name="sinceUtc"/>. Lines that don't parse are skipped.
        /// </summary>
        internal static List<T> ReadLastLines<T>(string file, int count, DateTime? sinceUtc, Func<T, string> timeOf) where T : class
        {
            var result = new List<T>();
            if (!File.Exists(file)) return result;
            string[] lines;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                lines = reader.ReadToEnd().Split('\n');
            for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                T entry;
                try { entry = JsonUtility.FromJson<T>(line); } catch { continue; }
                if (entry == null) continue;
                if (sinceUtc.HasValue && DateTime.TryParse(timeOf(entry), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)
                    && t.ToUniversalTime() < sinceUtc.Value) break;
                result.Add(entry);
            }
            result.Reverse();
            return result;
        }

        /// <summary>Deletes the log.</summary>
        public static void Clear()
        {
            lock (s_Gate)
            {
                if (File.Exists(LogFile)) File.Delete(LogFile);
            }
        }

        // ---------------------------------------------------------------------------------------------------------

        internal static string[] ParameterNames(IReadOnlyList<string> args) =>
            args == null ? new string[0]
                : args.Where(a => a != null && a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
                    .Select(a => a.Substring(2).Split('=')[0]).Distinct().ToArray();

        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The command whose method is on <paramref name="trace"/>: the method itself, its async state machine, or a lambda in it.</summary>
        internal static string CommandFromStack(StackTrace trace)
        {
            foreach (StackFrame frame in trace.GetFrames() ?? new StackFrame[0])
            {
                string name = CommandName(frame.GetMethod());
                if (name != null) return name;
            }
            return "";
        }

        internal static string CommandName(MethodBase method)
        {
            if (method == null) return null;
            var attr = method.GetCustomAttribute<CliGroupCommandAttribute>();
            if (attr == null)
            {
                // "<Run>d__3.MoveNext" (async) or "<Run>b__3_0" (a lambda): the command is Run on the outer type.
                Type type = method.DeclaringType;
                Match m = Regex.Match(method.Name, @"^<(\w+)>");
                Type owner = type;
                if (!m.Success && type != null) { m = Regex.Match(type.Name, @"^<(\w+)>"); owner = type.DeclaringType; }
                else if (type != null && type.Name.StartsWith("<", StringComparison.Ordinal)) owner = type.DeclaringType;
                if (!m.Success || owner == null) return null;
                method = owner.GetMethods(AnyStatic).FirstOrDefault(x => x.Name == m.Groups[1].Value && x.GetCustomAttribute<CliGroupCommandAttribute>() != null);
                if (method == null) return null;
                attr = method.GetCustomAttribute<CliGroupCommandAttribute>();
            }
            string name = string.IsNullOrEmpty(attr.Name) ? Regex.Replace(method.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant() : attr.Name;
            return attr.Group + " " + name;
        }

        private static string[] PackageFrames(StackTrace trace) =>
            (trace.GetFrames() ?? new StackFrame[0])
                .Select(f => f.GetMethod())
                .Where(m => m?.DeclaringType != null && (m.DeclaringType.Namespace ?? "").StartsWith("UnityCLIExtensions", StringComparison.Ordinal))
                .Select(m => $"{Outer(m.DeclaringType).Name}.{Clean(m)}")
                .Distinct().Take(8).ToArray();

        private static Type Outer(Type t)
        {
            while (t.DeclaringType != null && t.Name.StartsWith("<", StringComparison.Ordinal)) t = t.DeclaringType;
            return t;
        }

        private static string Clean(MethodBase m)
        {
            Match async = Regex.Match(m.DeclaringType.Name, @"^<(\w+)>");
            if (async.Success && m.Name == "MoveNext") return async.Groups[1].Value;
            Match lambda = Regex.Match(m.Name, @"^<(\w+)>");
            return lambda.Success ? lambda.Groups[1].Value + " (lambda)" : m.Name;
        }
    }
}

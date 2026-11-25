using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Runs C# through Pipeline's own <c>script eval</c> engine, after filling <c>{{key}}</c> placeholders, so one
    /// snippet file serves many values (the snippet runs inside the Editor, where shell arguments and environment
    /// variables never reach it). A placeholder left unfilled is an error, so a typo can't run with a blank in it.
    /// </summary>
    public static class TemplatedEval
    {
        /// <summary>The outcome of one eval.</summary>
        public sealed class Result
        {
            public bool success;
            /// <summary>The snippet's return value, as Pipeline's eval gives it.</summary>
            public object value;
            /// <summary>The error code and message when it failed (compile errors, an exception, a bad argument).</summary>
            public string errorCode;
            public string error;
            /// <summary>Compiler errors and warnings, one line each.</summary>
            public List<string> diagnostics = new List<string>();
        }

        private static readonly Regex s_Placeholder = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

        /// <summary>
        /// Parses <c>key=value</c> pairs separated by <c>;</c> or new lines (or by <c>,</c> when there is no <c>;</c>).
        /// </summary>
        public static Dictionary<string, string> ParseVars(string vars)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(vars)) return result;
            char[] separators = vars.Contains(";") || vars.Contains("\n") ? new[] { ';', '\n' } : new[] { ',' };
            foreach (string pair in vars.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{pair.Trim()}' isn't key=value.");
                result[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim('\r');
            }
            return result;
        }

        /// <summary>Fills the placeholders in <paramref name="source"/>. Throws, naming them, when any are left unfilled.</summary>
        public static string Fill(string source, IReadOnlyDictionary<string, string> vars)
        {
            var missing = new SortedSet<string>();
            string filled = s_Placeholder.Replace(source, m =>
            {
                string key = m.Groups[1].Value;
                if (vars.TryGetValue(key, out string value)) return value;
                missing.Add(key);
                return m.Value;
            });
            if (missing.Count > 0)
                throw new ArgumentException($"Unfilled placeholder(s): {string.Join(", ", missing.Select(k => "{{" + k + "}}"))}. Pass them with --vars \"{string.Join(";", missing.Select(k => k + "=..."))}\".");
            return filled;
        }

        /// <summary>Reads <paramref name="file"/> (relative to the project root unless absolute) and fills it.</summary>
        public static string Load(string file, IReadOnlyDictionary<string, string> vars)
        {
            string path = Path.IsPathRooted(file) ? file : Path.Combine(CliExtPaths.ProjectRoot, file);
            if (!File.Exists(path)) throw new ArgumentException($"No snippet at '{path}'.");
            return Fill(File.ReadAllText(path), vars);
        }

        /// <summary>Evaluates <paramref name="code"/> with Pipeline's <c>script eval</c> engine.</summary>
        public static async Task<Result> Evaluate(string code, bool internals = false)
        {
            MethodInfo evaluate = EvaluateMethod;
            if (evaluate == null)
                return new Result { errorCode = "UNSUPPORTED", error = "This Pipeline version's script eval engine wasn't found (Unity.Pipeline.Runtime.Commands.CodeEvalCommand.EvaluateAsync). Use unity command script eval directly." };

            var task = (Task)evaluate.Invoke(null, new object[] { code, null, false, false, internals });
            await task;
            object response = task.GetType().GetProperty("Result")?.GetValue(task);
            return Convert(response);
        }

        private static MethodInfo s_Evaluate;
        private static bool s_Searched;

        private static MethodInfo EvaluateMethod
        {
            get
            {
                if (s_Searched) return s_Evaluate;
                s_Searched = true;
                Type type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Unity.Pipeline.Runtime.Commands.CodeEvalCommand", false))
                    .FirstOrDefault(t => t != null);
                s_Evaluate = type?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "EvaluateAsync" && m.GetParameters().Length == 5);
                return s_Evaluate;
            }
        }

        private static Result Convert(object response)
        {
            if (response == null) return new Result { errorCode = "NO_RESPONSE", error = "script eval returned nothing." };
            object Get(object o, string name) => o?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o);
            var result = new Result
            {
                success = Get(response, "Success") is true,
                value = Get(response, "Result"),
                errorCode = Get(response, "ErrorCode") as string,
                error = string.Join(". ", new[] { Get(response, "Error") as string, Get(response, "ErrorDetails") as string }.Where(s => !string.IsNullOrEmpty(s)))
            };
            if (Get(response, "Diagnostics") is IEnumerable diagnostics)
                foreach (object d in diagnostics)
                    result.diagnostics.Add($"line {Get(d, "Line")}, col {Get(d, "Column")}: {Get(d, "Severity")} {Get(d, "Id")}: {Get(d, "Message")}");
            return result;
        }

        /// <summary>The value as text, for comparing with "ready".</summary>
        public static string AsText(object value) => value switch
        {
            null => "",
            string s => s,
            _ => value.ToString()
        };
    }
}

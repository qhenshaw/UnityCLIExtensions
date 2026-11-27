using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace UnityCLIExtensions.Animation
{
    /// <summary>A missing asset, object or name the command was pointed at: exit 13 (not found).</summary>
    internal sealed class AnimNotFoundException : ArgumentException
    {
        public AnimNotFoundException(string message) : base(message) { }
    }

    /// <summary>An argument that can't work whatever the project holds (a wrong asset type, a path outside Assets/, an unknown enum name): exit 2.</summary>
    internal sealed class AnimUsageException : ArgumentException
    {
        public AnimUsageException(string message) : base(message) { }
    }

    internal static class AnimationErrors
    {
        /// <summary>The exit code for an error thrown while resolving a command's arguments.</summary>
        public static ExitCode Exit(Exception e) =>
            e is AnimNotFoundException ? ExitCode.NotFound : e is AnimUsageException ? ExitCode.InvalidUsage : ExitCode.Failed;

        /// <summary>A one-line message for any error an op can throw (no stack trace for a bad number).</summary>
        public static string Message(Exception e) =>
            e is ArgumentException || e is FormatException || e is OverflowException || e is InvalidOperationException || e is IOException
                ? e.Message : $"{e.GetType().Name}: {e.Message}";

        /// <summary>Normalizes a path or GUID; null when it's empty.</summary>
        public static string AssetPath(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            if (path.Length == 32 && path.All(Uri.IsHexDigit)) path = AssetDatabase.GUIDToAssetPath(path);
            return path;
        }

        /// <summary>
        /// Throws when nothing is at <paramref name="path"/> (not found, naming <paramref name="howToGetOne"/>) or when
        /// what is there isn't a <paramref name="what"/> (invalid usage).
        /// </summary>
        public static void RequireType(string path, string what, Type type, string howToGetOne, string given = null)
        {
            given ??= path;
            var main = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null) throw new AnimNotFoundException($"No {what} at '{given}'. {howToGetOne}");
            if (!type.IsInstanceOfType(main) && AssetDatabase.LoadAssetAtPath(path, type) == null)
                throw new AnimUsageException($"'{given}' is {A(main.GetType().Name)}, not {A(what)}.");
        }

        private static string Article(string what) => "aeiouAEIOU".IndexOf(what[0]) >= 0 ? "an" : "a";

        /// <summary>"an AnimationClip", "a model".</summary>
        public static string A(string what) => $"{Article(what)} {what}";

        /// <summary>
        /// " Did you mean: X?" for close names, else " &lt;Kind&gt;: a, b, c." (or none) and the command that lists them.
        /// </summary>
        public static string Hint(string name, IEnumerable<string> candidates, string kinds, string listCommand)
        {
            var all = candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            var close = Close(name, all);
            if (close.Count > 0) return $" Did you mean: {string.Join(", ", close)}?";
            string list = all.Count == 0 ? $" It has no {kinds.ToLowerInvariant()}." : $" {kinds}: {string.Join(", ", all.Take(20))}{(all.Count > 20 ? ", ..." : "")}.";
            return list + $" {listCommand} lists them.";
        }

        public static List<string> Close(string name, IEnumerable<string> candidates)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n.Length == 0) return new List<string>();
            return candidates
                .Select(c => (c, d: Distance(n, c.ToLowerInvariant())))
                .Where(x => x.d <= Math.Max(1, n.Length / 4) || (n.Length >= 3 && x.c.ToLowerInvariant().Contains(n)))
                .OrderBy(x => x.d).Take(3).Select(x => x.c).ToList();
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    // Two letters swapped ("widht") is one typo, not two.
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }

        /// <summary>An enum value by name, invalid usage with the names when it isn't one.</summary>
        public static T ParseEnum<T>(string text, string what, params string[] leaveOut) where T : struct, Enum
        {
            if (!string.IsNullOrWhiteSpace(text) && !int.TryParse(text.Trim(), out _) && !leaveOut.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase) &&
                Enum.TryParse(text.Trim(), true, out T v)) return v;
            throw new AnimUsageException($"'{text}' isn't {A(what)}. Use one of: {string.Join(", ", Enum.GetNames(typeof(T)).Except(leaveOut))}.");
        }
    }
}

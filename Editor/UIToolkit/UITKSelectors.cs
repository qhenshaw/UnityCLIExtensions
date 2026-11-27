using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityCLIExtensions.Animation;

namespace UnityCLIExtensions.UIToolkit
{
    /// <summary>
    /// A small USS-like selector: compound parts (Type, #name, .class, combined as Button.primary#start) separated by
    /// spaces (descendant) or &gt; (child). Matched against any tree through the accessors given.
    /// </summary>
    internal sealed class UITKSelector
    {
        private sealed class Part
        {
            public string type;
            public string name;
            public List<string> classes = new List<string>();
            public bool child; // '>' before it
        }

        private readonly List<Part> m_Parts = new List<Part>();
        private static readonly Regex s_Token = new Regex(@"([#.]?)([A-Za-z_\-][\w\-]*)", RegexOptions.Compiled);
        // One compound part, whole: an optional type (with an optional UXML prefix such as ui:), then #name and .class.
        private static readonly Regex s_Compound = new Regex(@"^(\*|([A-Za-z_][\w\-]*:)?[A-Za-z_][\w\-]*)?([#.][A-Za-z_\-][\w\-]*)*$", RegexOptions.Compiled);
        private static readonly string[] s_PseudoClasses = { "hover", "active", "focus", "checked", "disabled", "enabled", "inactive", "root", "selected" };

        public UITKSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("Empty selector: give Type, #name or .class, such as Button#start.");
            bool nextIsChild = false;
            foreach (string raw in Regex.Split(selector.Replace(">", " > ").Trim(), @"\s+"))
            {
                if (raw == ">") { nextIsChild = true; continue; }
                int colon = raw.IndexOf(':');
                if (colon >= 0 && s_PseudoClasses.Contains(Regex.Match(raw.Substring(colon + 1), @"^[\w\-]*").Value.ToLowerInvariant()))
                    throw new ArgumentException($"Pseudo-classes such as '{raw.Substring(colon)}' aren't supported in '{selector}': select by Type, #name and .class.");
                if (!s_Compound.IsMatch(raw))
                    throw new ArgumentException($"Can't read '{raw}' in the selector '{selector}': use Type, #name and .class, joined (Button.primary#start), with spaces or > between parts.");
                var part = new Part { child = nextIsChild };
                nextIsChild = false;
                // A UXML prefix (ui:Button) is dropped: types match by their local name.
                string body = colon >= 0 ? raw.Substring(colon + 1) : raw;
                foreach (Match m in s_Token.Matches(body))
                {
                    if (m.Groups[1].Value == "#") part.name = m.Groups[2].Value;
                    else if (m.Groups[1].Value == ".") part.classes.Add(m.Groups[2].Value);
                    else part.type = m.Groups[2].Value;
                }
                m_Parts.Add(part);
            }
            if (nextIsChild || m_Parts.Count == 0 || m_Parts[0].child)
                throw new ArgumentException($"'{selector}' has a > without an element on each side.");
        }

        /// <summary>The selector's simple parts: "Type", "#name" and ".class".</summary>
        public IEnumerable<string> Tokens() =>
            m_Parts.SelectMany(p => (p.type != null ? new[] { p.type } : new string[0]).Concat(p.name != null ? new[] { "#" + p.name } : new string[0]).Concat(p.classes.Select(c => "." + c)));

        /// <summary>
        /// Why nothing matched, in one line: the first type, #name or .class the tree doesn't have at all (with "Did you
        /// mean" for a close one), else that the parts are there but not arranged that way; and the command that shows the tree.
        /// </summary>
        public string NoMatch(string selector, IEnumerable<string> present, string listCommand)
        {
            var all = present.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            foreach (string token in Tokens())
            {
                char kind = token[0] == '#' || token[0] == '.' ? token[0] : 'T';
                var same = all.Where(p => (p[0] == '#' || p[0] == '.' ? p[0] : 'T') == kind).ToList();
                bool there = kind == 'T' ? same.Any(p => string.Equals(p, token, StringComparison.OrdinalIgnoreCase)) : same.Contains(token);
                if (there) continue;
                string what = kind == '#' ? "element named" : kind == '.' ? "element with the class" : "element of type";
                var close = AnimationErrors.Close(token, same);
                return $"Nothing matches '{selector}': no {what} '{token.TrimStart('#', '.')}'." +
                       (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "") + $" {listCommand} shows the elements.";
            }
            return $"Nothing matches '{selector}': its parts are there, but not nested that way. {listCommand} shows the elements.";
        }

        /// <summary>The nodes under <paramref name="root"/> (itself included) that match.</summary>
        public List<T> Match<T>(T root, Func<T, IEnumerable<T>> children, Func<T, T> parent, Func<T, string> type, Func<T, string> name, Func<T, IEnumerable<string>> classes) where T : class
        {
            bool Fits(T node, Part p) =>
                (p.type == null || string.Equals(type(node), p.type, StringComparison.OrdinalIgnoreCase)) &&
                (p.name == null || name(node) == p.name) &&
                p.classes.All(c => classes(node).Contains(c));

            bool Matches(T node, int index)
            {
                var part = m_Parts[index];
                if (!Fits(node, part)) return false;
                if (index == 0) return true;
                if (part.child) { var up = parent(node); return up != null && Matches(up, index - 1); }
                for (var up = parent(node); up != null; up = parent(up)) if (Matches(up, index - 1)) return true;
                return false;
            }

            var result = new List<T>();
            var stack = new Stack<T>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (Matches(n, m_Parts.Count - 1)) result.Add(n);
                foreach (var c in children(n).Reverse()) stack.Push(c);
            }
            return result;
        }

        /// <summary>Every "Type", "#name" and ".class" in a tree, for <see cref="NoMatch"/>.</summary>
        public static IEnumerable<string> Present<T>(T root, Func<T, IEnumerable<T>> children, Func<T, string> type, Func<T, string> name, Func<T, IEnumerable<string>> classes)
        {
            var stack = new Stack<T>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return type(n);
                if (!string.IsNullOrEmpty(name(n))) yield return "#" + name(n);
                foreach (string c in classes(n)) yield return "." + c;
                foreach (var c in children(n)) stack.Push(c);
            }
        }
    }
}

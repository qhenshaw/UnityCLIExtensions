using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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

        public UITKSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("Empty selector.");
            bool nextIsChild = false;
            foreach (string raw in Regex.Split(selector.Replace(">", " > ").Trim(), @"\s+"))
            {
                if (raw == ">") { nextIsChild = true; continue; }
                var part = new Part { child = nextIsChild };
                nextIsChild = false;
                foreach (Match m in s_Token.Matches(raw))
                {
                    if (m.Groups[1].Value == "#") part.name = m.Groups[2].Value;
                    else if (m.Groups[1].Value == ".") part.classes.Add(m.Groups[2].Value);
                    else part.type = m.Groups[2].Value;
                }
                if (raw != "*" && part.type == null && part.name == null && part.classes.Count == 0) throw new ArgumentException($"Can't read '{raw}' in the selector.");
                m_Parts.Add(part);
            }
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
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCLIExtensions.UIToolkit
{
    [Serializable]
    internal class UxmlOp
    {
        /// <summary>UXML: add, remove, move, set_attributes, add_class, remove_class. USS: set_rule, remove_rule, remove_property.</summary>
        public string op;
        /// <summary>UXML: the element(s) to change, or the parent for add/move. USS: the rule's selector, as written.</summary>
        public string selector;
        /// <summary>For add: the element type (Button, Label, VisualElement, ui:Toggle, ...).</summary>
        public string type;
        public string name;
        /// <summary>For add/add_class/remove_class: classes separated by spaces.</summary>
        public string @class;
        public string text;
        /// <summary>For add/set_attributes: attributes "name=value;name=value" (style, tooltip, label, value, ...).</summary>
        public string attributes;
        /// <summary>For add/move: the position among the parent's children (-1: last).</summary>
        public int index = -1;
        /// <summary>For move: the new parent.</summary>
        public string to;
        /// <summary>USS: properties "color: #fff; width: 120px" for set_rule; names separated by ';' for remove_property.</summary>
        public string properties;
        /// <summary>For set_rule: replace the rule's properties instead of merging.</summary>
        public bool replace;
    }

    [Serializable]
    internal class UxmlOpList
    {
        public UxmlOp[] ops;
    }

    /// <summary>
    /// <c>cliext.uitk</c>: read and edit UXML and USS files as text, checked by Unity's importer, and in Play mode query
    /// the live visual tree of the UIDocuments, click buttons and set field values.
    /// </summary>
    internal static class UITKCommands
    {
        private const string Group = "cliext.uitk";
        private const string UiNamespace = "UnityEngine.UIElements";

        // ---------------------------------------------------------------------------------------------------------
        // UXML

        private static string Resolve(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            if (path.Length == 32 && path.All(Uri.IsHexDigit)) path = AssetDatabase.GUIDToAssetPath(path);
            string full = Path.IsPathRooted(path) ? path : Path.Combine(CliExtPaths.ProjectRoot, path);
            if (!File.Exists(full)) throw new ArgumentException($"No file at '{path}'.");
            return path;
        }

        private static string FullPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(CliExtPaths.ProjectRoot, path);

        private static IEnumerable<string> ClassesOf(XElement e) => ((string)e.Attribute("class") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private static List<XElement> Select(XDocument doc, string selector)
        {
            var root = doc.Root;
            var matches = new UITKSelector(selector).Match(root, e => e.Elements().Where(x => x.Name.LocalName != "Style" && x.Name.LocalName != "Template"),
                e => e.Parent, e => e.Name.LocalName, e => (string)e.Attribute("name"), ClassesOf);
            return matches.Where(m => m != root || selector.Trim() == "UXML").ToList();
        }

        private static object DescribeXml(XElement e, int depth) => new
        {
            type = e.Name.LocalName,
            name = (string)e.Attribute("name"),
            @class = (string)e.Attribute("class"),
            text = (string)e.Attribute("text"),
            attributes = e.Attributes().Where(a => a.Name.LocalName != "name" && a.Name.LocalName != "class" && a.Name.LocalName != "text" && !a.IsNamespaceDeclaration)
                .Select(a => $"{a.Name.LocalName}={a.Value}").ToArray(),
            children = depth > 0 ? e.Elements().Select(c => DescribeXml(c, depth - 1)).ToArray() : null
        };

        [CliGroupCommand(Group, "Describe a UXML file as a tree (type, name, classes, text, other attributes) and its style sheets; or a USS file as its rules and properties.",
            Name = "inspect", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("path", "A .uxml or .uss file", Required = true)] string path)
        {
            try
            {
                path = Resolve(path);
                if (path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.Result.Write(new { path, rules = UssRules(File.ReadAllText(FullPath(path))).Select(r => new { r.selector, properties = r.properties.Select(p => $"{p.Key}: {p.Value}").ToArray() }).ToArray() });
                    return ExitCode.Success;
                }
                var doc = XDocument.Load(FullPath(path));
                cmd.Result.Write(new
                {
                    path,
                    styles = doc.Root.Descendants().Where(e => e.Name.LocalName == "Style").Select(e => (string)e.Attribute("src")).ToArray(),
                    templates = doc.Root.Descendants().Where(e => e.Name.LocalName == "Template").Select(e => $"{(string)e.Attribute("name")}: {(string)e.Attribute("src")}").ToArray(),
                    tree = doc.Root.Elements().Where(e => e.Name.LocalName != "Style" && e.Name.LocalName != "Template").Select(e => DescribeXml(e, 50)).ToArray()
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is System.Xml.XmlException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Edit a UXML or USS file with a JSON list of ops, saved once and checked by Unity's importer (its errors come back). " +
                                "UXML: add (selector=parent, type, name, class, text, attributes=\"k=v;k=v\", index), remove, move (to, index), set_attributes, add_class, remove_class; selectors are Type, #name, .class, combined and nested (\"#menu > Button.primary\"). " +
                                "USS: set_rule (selector as written, properties=\"color: #fff; width: 120px\", replace), remove_rule, remove_property. An op that fails saves nothing.",
            Name = "edit", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "A .uxml or .uss file", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            try { path = Resolve(path); }
            catch (Exception e) when (e is IOException || e is ArgumentException) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            UxmlOpList list;
            try { string s = json.Trim(); list = JsonUtility.FromJson<UxmlOpList>(s.StartsWith("[") ? "{\"ops\":" + s + "}" : s); }
            catch (Exception e) { cmd.Log.Error($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Log.Error("No ops given."); return ExitCode.InvalidUsage; }

            bool uss = path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase);
            string full = FullPath(path);
            string original = File.ReadAllText(full);
            var results = new List<object>();
            string output;
            try
            {
                if (uss)
                {
                    string text = original;
                    foreach (var op in list.ops) { text = ApplyUss(text, op, out object r); results.Add(r); }
                    output = text;
                }
                else
                {
                    var doc = XDocument.Parse(original, LoadOptions.PreserveWhitespace);
                    foreach (var op in list.ops) results.Add(ApplyUxml(doc, op));
                    output = doc.Declaration != null ? doc.Declaration + "\n" + doc.Root : doc.ToString();
                }
            }
            catch (Exception e) when (e is ArgumentException || e is System.Xml.XmlException || e is FormatException)
            {
                cmd.Result.Write(new { path, applied = results.Count, failedAt = results.Count, error = e.Message, saved = false });
                cmd.Log.Error($"Op {results.Count} ({list.ops[results.Count].op}) failed: {e.Message} Nothing was saved.");
                return ExitCode.Failed;
            }

            File.WriteAllText(full, output, new UTF8Encoding(false));
            var messages = new List<string>();
            void OnLog(string condition, string stack, LogType type) { if (type != LogType.Log) messages.Add($"{type}: {condition}"); }
            Application.logMessageReceived += OnLog;
            try { if (path.StartsWith("Assets/") || path.StartsWith("Packages/")) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); }
            finally { Application.logMessageReceived -= OnLog; }
            cmd.Result.Write(new { path, applied = results.Count, results, import = messages });
            if (messages.Any(m => m.StartsWith("Error") || m.StartsWith("Exception")))
            {
                cmd.Log.Warning("Saved, but Unity's importer reported errors; see import.");
                return ExitCode.Failed;
            }
            return ExitCode.Success;
        }

        private static object ApplyUxml(XDocument doc, UxmlOp op)
        {
            List<XElement> Targets(string selector)
            {
                if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException($"'{op.op}' needs a selector.");
                var found = Select(doc, selector);
                if (found.Count == 0) throw new ArgumentException($"Nothing matches '{selector}'.");
                return found;
            }
            void SetAttributes(XElement e, string pairs)
            {
                foreach (string pair in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"'{pair}' isn't name=value.");
                    string k = pair.Substring(0, eq).Trim(), v = pair.Substring(eq + 1).Trim();
                    e.SetAttributeValue(k, v.Length == 0 ? null : v);
                }
            }
            void Insert(XElement parent, XElement child, int index)
            {
                // Keep the file readable: each new element on its own line, indented one step under its parent.
                int depth = parent.Ancestors().Count();
                string indent = "\n" + new string(' ', 4 * (depth + 1));
                var kids = parent.Elements().ToList();
                if (kids.Count == 0)
                {
                    parent.Add(new XText(indent), child, new XText("\n" + new string(' ', 4 * depth)));
                    return;
                }
                if (index < 0 || index >= kids.Count) kids.Last().AddAfterSelf(new XText(indent), child);
                else kids[index].AddBeforeSelf(child, new XText(indent));
            }

            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add":
                {
                    var parent = string.IsNullOrWhiteSpace(op.selector) ? doc.Root : Targets(op.selector).First();
                    if (string.IsNullOrWhiteSpace(op.type)) throw new ArgumentException("add needs a type.");
                    XName name;
                    string type = op.type.Trim();
                    int colon = type.IndexOf(':');
                    if (colon > 0)
                    {
                        var ns = doc.Root.GetNamespaceOfPrefix(type.Substring(0, colon)) ?? throw new ArgumentException($"The UXML has no '{type.Substring(0, colon)}' prefix.");
                        name = ns + type.Substring(colon + 1);
                    }
                    else
                    {
                        // Built-in controls live in UnityEngine.UIElements; use the document's prefix for it if it has one.
                        XNamespace ns = doc.Root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == UiNamespace).Select(a => (XNamespace)a.Value).FirstOrDefault() ?? doc.Root.Name.Namespace;
                        name = ns + type;
                    }
                    var e = new XElement(name);
                    if (!string.IsNullOrWhiteSpace(op.name)) e.SetAttributeValue("name", op.name);
                    if (!string.IsNullOrWhiteSpace(op.@class)) e.SetAttributeValue("class", op.@class);
                    if (op.text != null) e.SetAttributeValue("text", op.text);
                    SetAttributes(e, op.attributes);
                    Insert(parent, e, op.index);
                    return new { op = "add", type = e.Name.LocalName, name = op.name, parent = parent.Name.LocalName + ((string)parent.Attribute("name") is string pn ? "#" + pn : "") };
                }
                case "remove":
                {
                    var targets = Targets(op.selector);
                    foreach (var t in targets) t.Remove();
                    return new { op = "remove", removed = targets.Count };
                }
                case "move":
                {
                    var targets = Targets(op.selector);
                    var to = Targets(op.to).First();
                    foreach (var t in targets) { if (t.DescendantsAndSelf().Contains(to)) throw new ArgumentException("Can't move an element into itself."); t.Remove(); Insert(to, t, op.index); }
                    return new { op = "move", moved = targets.Count };
                }
                case "set_attributes":
                {
                    var targets = Targets(op.selector);
                    foreach (var t in targets)
                    {
                        SetAttributes(t, op.attributes);
                        if (op.text != null) t.SetAttributeValue("text", op.text);
                        if (op.name != null) t.SetAttributeValue("name", op.name.Length == 0 ? null : op.name);
                    }
                    return new { op = "set_attributes", changed = targets.Count };
                }
                case "add_class":
                case "remove_class":
                {
                    var targets = Targets(op.selector);
                    var change = (op.@class ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    bool add = op.op.Trim().ToLowerInvariant() == "add_class";
                    foreach (var t in targets)
                    {
                        var classes = ClassesOf(t).ToList();
                        if (add) classes.AddRange(change.Where(c => !classes.Contains(c))); else classes.RemoveAll(change.Contains);
                        t.SetAttributeValue("class", classes.Count == 0 ? null : string.Join(" ", classes));
                    }
                    return new { op = op.op, changed = targets.Count };
                }
                default:
                    throw new ArgumentException($"Unknown UXML op '{op.op}'. Ops: add, remove, move, set_attributes, add_class, remove_class.");
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // USS

        private sealed class UssRule
        {
            public string selector;
            public int start, length;
            public List<KeyValuePair<string, string>> properties = new List<KeyValuePair<string, string>>();
        }

        private static readonly Regex s_Rule = new Regex(@"(?<sel>[^{}]+?)\s*\{(?<body>[^{}]*)\}", RegexOptions.Compiled | RegexOptions.Singleline);

        private static string StripComments(string s) => Regex.Replace(s, @"/\*.*?\*/", m => new string(' ', m.Length), RegexOptions.Singleline);

        private static List<UssRule> UssRules(string text)
        {
            var rules = new List<UssRule>();
            foreach (Match m in s_Rule.Matches(StripComments(text)))
            {
                // The selector group also takes the whitespace (and blanked comments) before the rule: start at the selector itself.
                string sel = m.Groups["sel"].Value;
                int lead = sel.Length - sel.TrimStart().Length;
                var r = new UssRule { selector = Regex.Replace(sel.Trim(), @"\s+", " "), start = m.Index + lead, length = m.Length - lead };
                foreach (string decl in m.Groups["body"].Value.Split(';'))
                {
                    int colon = decl.IndexOf(':');
                    if (colon > 0) r.properties.Add(new KeyValuePair<string, string>(decl.Substring(0, colon).Trim(), decl.Substring(colon + 1).Trim()));
                }
                rules.Add(r);
            }
            return rules;
        }

        private static string RenderRule(string selector, IEnumerable<KeyValuePair<string, string>> props) =>
            selector + " {\n" + string.Concat(props.Select(p => $"    {p.Key}: {p.Value};\n")) + "}";

        private static string ApplyUss(string text, UxmlOp op, out object result)
        {
            if (string.IsNullOrWhiteSpace(op.selector)) throw new ArgumentException($"'{op.op}' needs the rule's selector.");
            string selector = Regex.Replace(op.selector.Trim(), @"\s+", " ");
            var rule = UssRules(text).FirstOrDefault(r => r.selector == selector);
            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "set_rule":
                {
                    var props = rule == null || op.replace ? new List<KeyValuePair<string, string>>() : rule.properties.ToList();
                    foreach (string decl in (op.properties ?? "").Split(';'))
                    {
                        int colon = decl.IndexOf(':');
                        if (colon <= 0) { if (decl.Trim().Length > 0) throw new ArgumentException($"'{decl}' isn't property: value."); continue; }
                        string k = decl.Substring(0, colon).Trim(), v = decl.Substring(colon + 1).Trim();
                        int at = props.FindIndex(p => p.Key == k);
                        if (at >= 0) props[at] = new KeyValuePair<string, string>(k, v); else props.Add(new KeyValuePair<string, string>(k, v));
                    }
                    string rendered = RenderRule(selector, props);
                    result = new { op = "set_rule", selector, properties = props.Count, created = rule == null };
                    return rule == null ? text.TrimEnd() + "\n\n" + rendered + "\n" : text.Substring(0, rule.start) + rendered + text.Substring(rule.start + rule.length);
                }
                case "remove_rule":
                {
                    if (rule == null) throw new ArgumentException($"No rule '{selector}'. Rules: {string.Join(", ", UssRules(text).Select(r => r.selector))}");
                    result = new { op = "remove_rule", selector };
                    return text.Substring(0, rule.start) + text.Substring(rule.start + rule.length);
                }
                case "remove_property":
                {
                    if (rule == null) throw new ArgumentException($"No rule '{selector}'.");
                    var names = (op.properties ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToList();
                    var props = rule.properties.Where(p => !names.Contains(p.Key)).ToList();
                    result = new { op = "remove_property", selector, removed = rule.properties.Count - props.Count };
                    return text.Substring(0, rule.start) + RenderRule(selector, props) + text.Substring(rule.start + rule.length);
                }
                default:
                    throw new ArgumentException($"Unknown USS op '{op.op}'. Ops: set_rule, remove_rule, remove_property.");
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // The live visual tree

        private static IEnumerable<(string name, VisualElement root)> Roots(string document)
        {
            foreach (var d in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
            {
                if (d.rootVisualElement == null) continue;
                if (!string.IsNullOrWhiteSpace(document) && !string.Equals(d.name, document, StringComparison.OrdinalIgnoreCase)) continue;
                yield return (d.name, d.rootVisualElement);
            }
        }

        private static string TypeName(VisualElement e) => e.GetType().Name;

        private static string TextOf(VisualElement e)
        {
            if (e is TextElement t) return t.text;
            var valueProp = e.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
            if (valueProp != null && e.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotifyValueChanged<>)))
            {
                try { return ValueText.Format(valueProp.GetValue(e)); } catch { }
            }
            return null;
        }

        private static string PathOf(VisualElement e)
        {
            var parts = new List<string>();
            for (var x = e; x != null && x.parent != null; x = x.parent)
                parts.Add(TypeName(x) + (string.IsNullOrEmpty(x.name) ? "" : "#" + x.name));
            parts.Reverse();
            return string.Join(" > ", parts);
        }

        private static object DescribeLive(VisualElement e, int depth) => new
        {
            type = TypeName(e),
            name = string.IsNullOrEmpty(e.name) ? null : e.name,
            classes = e.GetClasses().Any() ? string.Join(" ", e.GetClasses()) : null,
            text = TextOf(e),
            rect = new[] { Mathf.Round(e.worldBound.x), Mathf.Round(e.worldBound.y), Mathf.Round(e.worldBound.width), Mathf.Round(e.worldBound.height) },
            shown = e.resolvedStyle.display != DisplayStyle.None && e.resolvedStyle.visibility != Visibility.Hidden ? (bool?)null : false,
            enabled = e.enabledInHierarchy ? (bool?)null : false,
            children = depth > 0 && e.childCount > 0 ? e.Children().Select(c => DescribeLive(c, depth - 1)).ToArray() : null,
            more = depth <= 0 && e.childCount > 0 ? (int?)e.childCount : null
        };

        private static List<(string doc, VisualElement e)> QueryLive(string document, string selector)
        {
            var sel = new UITKSelector(selector);
            var found = new List<(string, VisualElement)>();
            foreach (var (name, root) in Roots(document))
                found.AddRange(sel.Match(root, e => e.Children(), e => e.parent, TypeName, e => e.name, e => e.GetClasses()).Select(e => (name, e)));
            return found;
        }

        [CliGroupCommand(Group, "The live visual tree of the UIDocuments in the open scenes (--document narrows to one): each element's type, name, classes, text or value, screen rect, " +
                                "and whether it is hidden or disabled. --depth limits the depth.",
            Name = "tree", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Tree(
            CommandContext cmd,
            [CliArg("document", "The UIDocument's GameObject")] string document = "",
            [CliArg("depth", "How deep to go")] int depth = 8)
        {
            var roots = Roots(document).ToList();
            if (roots.Count == 0) { cmd.Log.Error("No active UIDocument" + (string.IsNullOrWhiteSpace(document) ? "." : $" named '{document}'.")); return ExitCode.NotFound; }
            foreach (var (name, root) in roots) cmd.Result.WriteRecord(new { document = name, tree = DescribeLive(root, depth) });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Find live elements by selector (Type, #name, .class, combined and nested: \"#menu > Button.primary\") in the UIDocuments; each with its path, text or value, rect and state.",
            Name = "query", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Query(
            CommandContext cmd,
            [CliArg("selector", "The selector", Required = true)] string selector,
            [CliArg("document", "The UIDocument's GameObject")] string document = "")
        {
            try
            {
                foreach (var (doc, e) in QueryLive(document, selector))
                    cmd.Result.WriteRecord(new { document = doc, path = PathOf(e), element = DescribeLive(e, 0) });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.InvalidUsage; }
        }

        [CliGroupCommand(Group, "Click the first element matching the selector (a Button, or anything with a click handler), as a submit from navigation does. Use in Play mode.",
            Name = "click", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Click(
            CommandContext cmd,
            [CliArg("selector", "Selector for the element: Type, #name, .class, combined and nested (\"#menu > Button.primary\")", Required = true)] string selector,
            [CliArg("document", "The UIDocument's GameObject")] string document = "")
        {
            try
            {
                var match = QueryLive(document, selector);
                if (match.Count == 0) { cmd.Log.Error($"Nothing matches '{selector}'."); return ExitCode.NotFound; }
                var e = match[0].e;
                if (!e.enabledInHierarchy) { cmd.Log.Error($"{PathOf(e)} is disabled."); return ExitCode.Failed; }
                using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = e; e.SendEvent(submit); }
                using (var click = ClickEvent.GetPooled()) { click.target = e; e.SendEvent(click); }
                cmd.Result.Write(new { clicked = PathOf(e), matches = match.Count });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.InvalidUsage; }
        }

        [CliGroupCommand(Group, "Set the value of the first matching field (TextField, Toggle, Slider, DropdownField, ... anything with a value) or a Label's text; the field sends its change event. Use in Play mode.",
            Name = "set_value", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetValue(
            CommandContext cmd,
            [CliArg("selector", "Selector for the element: Type, #name, .class, combined and nested (\"#menu > Button.primary\")", Required = true)] string selector,
            [CliArg("value", "The value as the field takes it: text, a number, true/false, or a dropdown choice", Required = true)] string value,
            [CliArg("document", "The UIDocument's GameObject")] string document = "")
        {
            try
            {
                var match = QueryLive(document, selector);
                if (match.Count == 0) { cmd.Log.Error($"Nothing matches '{selector}'."); return ExitCode.NotFound; }
                var e = match[0].e;
                var prop = e.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanWrite) prop.SetValue(e, ValueText.Parse(value, prop.PropertyType));
                else if (e is TextElement t) t.text = value;
                else { cmd.Log.Error($"{PathOf(e)} has no value to set."); return ExitCode.Failed; }
                cmd.Result.Write(new { element = PathOf(e), value = TextOf(e) });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }
    }
}

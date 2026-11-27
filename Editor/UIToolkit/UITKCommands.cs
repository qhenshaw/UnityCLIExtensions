using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
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

    /// <summary>What an edit did: saved, or the op that failed (nothing saved) and why.</summary>
    internal sealed class UitkEditOutcome
    {
        public bool saved;
        public int failedAt = -1;
        public string error;
        public List<object> results = new List<object>();
        /// <summary>The importer's warnings and errors for the saved file (ones it already had before the edit).</summary>
        public List<string> import = new List<string>();
    }

    /// <summary>
    /// <c>cliext.uitk</c>: read and edit UXML and USS files as text, checked by Unity's importer, and in Play mode query
    /// the live visual tree of the UIDocuments, click buttons and set field values.
    /// </summary>
    internal static class UITKCommands
    {
        private const string Group = "cliext.uitk";
        private const string UiNamespace = "UnityEngine.UIElements";
        private static readonly string[] s_UxmlOps = { "add", "remove", "move", "set_attributes", "add_class", "remove_class" };
        private static readonly string[] s_UssOps = { "set_rule", "remove_rule", "remove_property" };

        // ---------------------------------------------------------------------------------------------------------
        // Paths

        /// <summary>
        /// A .uxml or .uss under Assets/ or Packages/ (a GUID, or a full path inside the project, also works): invalid
        /// usage for another path or file type, not found, naming how to make one, when no file is there.
        /// </summary>
        internal static string Resolve(string path)
        {
            string given = (path ?? "").Trim();
            string p = AnimationErrors.AssetPath(given);
            if (p.Length == 0)
            {
                if (given.Length > 0) throw new AnimNotFoundException($"No asset has the GUID '{given}'. Pass the .uxml or .uss path instead.");
                throw new AnimUsageException("Pass --path, a .uxml or .uss file such as Assets/UI/Menu.uxml.");
            }
            string root = CliExtPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (Path.IsPathRooted(p) && p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p = p.Substring(root.Length);
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                throw new AnimUsageException($"'{given}' isn't under Assets/ or Packages/. Give the file's asset path, such as Assets/UI/Menu.uxml.");
            string full = FileUtil.GetPhysicalPath(p);
            bool exists = File.Exists(full);
            bool uss = p.EndsWith(".uss", StringComparison.OrdinalIgnoreCase);
            if (!uss && !p.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                var main = exists ? AssetDatabase.LoadMainAssetAtPath(p) : null;
                throw new AnimUsageException($"'{p}' is {(main != null ? AnimationErrors.A(main.GetType().Name) : exists ? "another kind of file" : "not a .uxml or .uss path")}, not a .uxml or .uss file.");
            }
            if (!exists)
                throw new AnimNotFoundException($"No {(uss ? ".uss" : ".uxml")} file at '{p}'. Create one in the Project window " +
                                                $"(Create > UI Toolkit > {(uss ? "Style Sheet" : "UI Document")}), or write the file, then run this again.");
            return p;
        }

        private static string FullPath(string path) => FileUtil.GetPhysicalPath(path);

        private static ExitCode Fail(CommandContext cmd, Exception e)
        {
            cmd.Fail(AnimationErrors.Message(e));
            return AnimationErrors.Exit(e);
        }

        // ---------------------------------------------------------------------------------------------------------
        // UXML

        private static IEnumerable<string> ClassesOf(XElement e) => ((string)e.Attribute("class") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private static IEnumerable<XElement> TreeChildren(XElement e) => e.Elements().Where(x => x.Name.LocalName != "Style" && x.Name.LocalName != "Template");

        private static List<XElement> Select(XDocument doc, string selector, out UITKSelector parsed)
        {
            var root = doc.Root;
            parsed = new UITKSelector(selector);
            var matches = parsed.Match(root, TreeChildren, e => e.Parent, e => e.Name.LocalName, e => (string)e.Attribute("name"), ClassesOf);
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

        private static XDocument ParseUxml(string text, string path)
        {
            try { return XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
            catch (System.Xml.XmlException e) { throw new ArgumentException($"'{path}' isn't valid UXML ({e.Message}). Fix or replace the file."); }
        }

        [CliGroupCommand(Group, "Describe a UXML file as a tree (type, name, classes, text, other attributes) and its style sheets; or a USS file as its rules and properties.",
            Name = "inspect", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("path", "A .uxml or .uss file under Assets/ or Packages/", Required = true)] string path)
        {
            try
            {
                path = Resolve(path);
                if (path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.Result.Write(new { path, rules = UssRules(File.ReadAllText(FullPath(path))).Select(r => new { r.selector, properties = r.properties.Select(p => $"{p.Key}: {p.Value}").ToArray() }).ToArray() });
                    return ExitCode.Success;
                }
                var doc = ParseUxml(File.ReadAllText(FullPath(path)), path);
                cmd.Result.Write(new
                {
                    path,
                    styles = doc.Root.Descendants().Where(e => e.Name.LocalName == "Style").Select(e => (string)e.Attribute("src")).ToArray(),
                    templates = doc.Root.Descendants().Where(e => e.Name.LocalName == "Template").Select(e => $"{(string)e.Attribute("name")}: {(string)e.Attribute("src")}").ToArray(),
                    tree = TreeChildren(doc.Root).Select(e => DescribeXml(e, 50)).ToArray()
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException) { return Fail(cmd, e); }
        }

        [CliGroupCommand(Group, "Edit a UXML or USS file with a JSON list of ops, saved once and checked by Unity's importer. " +
                                "UXML: add (selector=parent, type, name, class, text, attributes=\"k=v;k=v\", index), remove, move (to, index), set_attributes (attributes, text, name), add_class, remove_class; selectors are Type, #name, .class, combined and nested (\"#menu > Button.primary\"). " +
                                "USS: set_rule (selector as written, properties=\"color: #fff; width: 120px\", replace), remove_rule, remove_property (properties=\"color; width\"). " +
                                "An op that fails saves nothing, and so does one that would change nothing, or an importer error or new importer warning (an unknown USS property, a bad value). cliext.uitk inspect shows the file.",
            Name = "edit", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "A .uxml or .uss file under Assets/ or Packages/", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            try { path = Resolve(path); }
            catch (Exception e) when (e is ArgumentException || e is IOException) { return Fail(cmd, e); }
            UxmlOpList list;
            try { string s = json.Trim(); list = JsonUtility.FromJson<UxmlOpList>(s.StartsWith("[") ? "{\"ops\":" + s + "}" : s); }
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given: pass a JSON array such as [{\"op\":\"remove\",\"selector\":\"#start\"}]."); return ExitCode.InvalidUsage; }

            UitkEditOutcome outcome;
            try { outcome = EditFile(path, list.ops); }
            catch (Exception e) when (e is ArgumentException || e is IOException) { return Fail(cmd, e); }
            if (!outcome.saved)
            {
                cmd.Result.Write(new { path, applied = 0, failedAt = outcome.failedAt, error = outcome.error, saved = false, import = outcome.import });
                string which = outcome.failedAt >= 0 && outcome.failedAt < list.ops.Length ? $"Op {outcome.failedAt} ({list.ops[outcome.failedAt].op}) failed: " : "";
                cmd.Fail($"{which}{outcome.error} Nothing was saved.");
                return ExitCode.Failed;
            }
            cmd.Result.Write(new { path, applied = outcome.results.Count, results = outcome.results, saved = true, import = outcome.import });
            return ExitCode.Success;
        }

        /// <summary>
        /// Applies <paramref name="ops"/> to the file at <paramref name="path"/> (resolved) on a copy of its text and
        /// writes it only when every op worked, then imports it. When the import reports an error, or a warning the
        /// file didn't have before, the file goes back to its old text and is imported again, so the file and the
        /// imported VisualTreeAsset/StyleSheet are as before.
        /// </summary>
        internal static UitkEditOutcome EditFile(string path, IList<UxmlOp> ops)
        {
            var outcome = new UitkEditOutcome();
            bool uss = path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase);
            string full = FullPath(path);
            string original = File.ReadAllText(full);
            // Ops work on "\n" text; the file keeps its own line endings (CRLF files on Windows stay CRLF), so an op
            // that changes nothing is seen as such and an edit doesn't rewrite every line ending.
            bool crlf = original.Contains("\r\n");
            string output;
            XDocument doc = uss ? null : ParseUxml(original, path);
            string text = original.Replace("\r\n", "\n");
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i] ?? new UxmlOp();
                try
                {
                    if (uss)
                    {
                        string next = ApplyUss(text, op, path, out object r);
                        if (next == text) throw new ArgumentException($"{op.op} would change nothing: the rule is already like that.");
                        text = next;
                        outcome.results.Add(r);
                    }
                    else
                    {
                        string before = doc.ToString(SaveOptions.DisableFormatting);
                        object r = ApplyUxml(doc, op, path);
                        if (doc.ToString(SaveOptions.DisableFormatting) == before) throw new ArgumentException($"{op.op} would change nothing: the elements are already like that.");
                        outcome.results.Add(r);
                    }
                }
                catch (Exception e)
                {
                    outcome.failedAt = i;
                    outcome.error = OneLine(AnimationErrors.Message(e));
                    outcome.results.Clear();
                    return outcome;
                }
            }
            output = uss ? text : doc.Declaration != null ? doc.Declaration + "\n" + doc.Root : doc.ToString();
            output = output.Replace("\r\n", "\n");
            if (crlf) output = output.Replace("\n", "\r\n");

            bool imported = path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(full, output, new UTF8Encoding(false));
            if (!imported) { outcome.saved = true; return outcome; }
            var after = Import(path);
            if (after.Count == 0) { outcome.saved = true; return outcome; }

            // Something to report: put the old text back, and see which messages the file already had.
            File.WriteAllText(full, original, new UTF8Encoding(false));
            var baseline = new HashSet<string>(Import(path).Select(Normalize));
            var added = after.Where(m => !baseline.Contains(Normalize(m))).ToList();
            if (added.Count > 0)
            {
                outcome.error = $"Unity's importer rejected the result: {OneLine(added[0])}";
                outcome.import = added.Select(OneLine).ToList();
                return outcome;
            }
            File.WriteAllText(full, output, new UTF8Encoding(false));
            outcome.import = Import(path).Select(OneLine).ToList();
            outcome.saved = true;
            return outcome;
        }

        /// <summary>Imports the asset, returning the warnings and errors Unity logged meanwhile.</summary>
        private static List<string> Import(string path)
        {
            var messages = new List<string>();
            void OnLog(string condition, string stack, LogType type) { if (type != LogType.Log) messages.Add($"{type}: {condition}"); }
            Application.logMessageReceived += OnLog;
            try { AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); }
            finally { Application.logMessageReceived -= OnLog; }
            return messages;
        }

        // The same message on another line, or with its source line shown, is the same message.
        private static string Normalize(string m) => Regex.Replace(OneLine(m), @"\(line \d+(, column \d+)?\)", "");

        private static string OneLine(string m)
        {
            string first = (m ?? "").Split('\n')[0].Trim();
            // An importer message can carry an exception's stack trace: keep the message.
            int at = first.IndexOf("   at ", StringComparison.Ordinal);
            return at > 0 ? first.Substring(0, at).Trim() : first;
        }

        private static string InspectHint(string path) => $"cliext.uitk inspect --path {path}";

        private static string UnknownOp(string op, string[] known, string kind)
        {
            var close = AnimationErrors.Close(op, known);
            return $"Unknown {kind} op '{op}'." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "") + $" Ops: {string.Join(", ", known)}.";
        }

        internal static object ApplyUxml(XDocument doc, UxmlOp op, string path)
        {
            string kind = (op.op ?? "").Trim().ToLowerInvariant();
            List<XElement> Targets(string selector, string field = "selector")
            {
                if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException($"{kind} needs {field}: Type, #name or .class, such as #menu.");
                var found = Select(doc, selector, out var parsed);
                if (found.Count == 0)
                    throw new ArgumentException(parsed.NoMatch(selector, UITKSelector.Present(doc.Root, TreeChildren, e => e.Name.LocalName, e => (string)e.Attribute("name"), ClassesOf), InspectHint(path)));
                return found;
            }
            void SetAttributes(XElement e, string pairs)
            {
                foreach (string pair in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (pair.Trim().Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"'{pair.Trim()}' isn't name=value: give attributes as \"tooltip=Hi;style=width: 20px\".");
                    string k = pair.Substring(0, eq).Trim(), v = pair.Substring(eq + 1).Trim();
                    if (!Regex.IsMatch(k, @"^[A-Za-z_][\w\-.]*$")) throw new ArgumentException($"'{k}' isn't an attribute name.");
                    e.SetAttributeValue(k, v.Length == 0 ? null : v);
                }
            }
            void Detach(XElement e)
            {
                // Take the indentation before it too, so no blank line is left behind.
                if (e.PreviousNode is XText ws && !(ws is XCData) && ws.Value.Trim().Length == 0) ws.Remove();
                e.Remove();
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

            switch (kind)
            {
                case "add":
                {
                    var parent = string.IsNullOrWhiteSpace(op.selector) ? doc.Root : Targets(op.selector).First();
                    if (string.IsNullOrWhiteSpace(op.type)) throw new ArgumentException("add needs a type (VisualElement, Label, Button, ui:Toggle, ...).");
                    XName name;
                    string type = op.type.Trim();
                    int colon = type.IndexOf(':');
                    if (colon > 0)
                    {
                        var ns = doc.Root.GetNamespaceOfPrefix(type.Substring(0, colon)) ?? throw new ArgumentException($"The UXML has no '{type.Substring(0, colon)}' prefix: declare it on the root (xmlns:{type.Substring(0, colon)}=\"...\"), or leave the prefix out.");
                        name = ns + type.Substring(colon + 1);
                    }
                    else
                    {
                        // Built-in controls live in UnityEngine.UIElements; use the document's prefix for it if it has one.
                        XNamespace ns = doc.Root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == UiNamespace).Select(a => (XNamespace)a.Value).FirstOrDefault() ?? doc.Root.Name.Namespace;
                        name = ns + type;
                    }
                    CheckElementType(name);
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
                    if (targets.Contains(doc.Root)) throw new ArgumentException("Can't remove the UXML root: remove its children instead (selector \"UXML > *\").");
                    foreach (var t in targets) Detach(t);
                    return new { op = "remove", removed = targets.Count };
                }
                case "move":
                {
                    var targets = Targets(op.selector);
                    var to = Targets(op.to, "to (the new parent)").First();
                    if (targets.Contains(doc.Root)) throw new ArgumentException("Can't move the UXML root.");
                    foreach (var t in targets) { if (t.DescendantsAndSelf().Contains(to)) throw new ArgumentException("Can't move an element into itself."); Detach(t); Insert(to, t, op.index); }
                    return new { op = "move", moved = targets.Count };
                }
                case "set_attributes":
                {
                    if (string.IsNullOrWhiteSpace(op.attributes) && op.text == null && op.name == null)
                        throw new ArgumentException("set_attributes needs attributes (\"k=v;k=v\", an empty value removes one), text or name.");
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
                    var change = (op.@class ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (change.Length == 0) throw new ArgumentException($"{kind} needs class (names separated by spaces).");
                    var targets = Targets(op.selector);
                    bool add = kind == "add_class";
                    foreach (var t in targets)
                    {
                        var classes = ClassesOf(t).ToList();
                        if (add) classes.AddRange(change.Where(c => !classes.Contains(c))); else classes.RemoveAll(change.Contains);
                        t.SetAttributeValue("class", classes.Count == 0 ? null : string.Join(" ", classes));
                    }
                    return new { op = kind, changed = targets.Count };
                }
                default:
                    if (s_UssOps.Contains(kind)) throw new ArgumentException($"'{kind}' is a USS op: pass the .uss file. UXML ops: {string.Join(", ", s_UxmlOps)}.");
                    throw new ArgumentException(string.IsNullOrEmpty(kind) ? $"An op needs \"op\": {string.Join(", ", s_UxmlOps)}." : UnknownOp(kind, s_UxmlOps, "UXML"));
            }
        }

        private static readonly string[] s_UxmlKeywords = { "Instance", "Template", "Style", "AttributeOverrides", "UXML" };

        /// <summary>Throws, with "Did you mean", when no VisualElement type answers to the UXML element name.</summary>
        internal static void CheckElementType(XName name)
        {
            string local = name.LocalName, ns = name.NamespaceName;
            if (s_UxmlKeywords.Contains(local)) return;
            var types = TypeCache.GetTypesDerivedFrom<VisualElement>().Append(typeof(VisualElement)).Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition).ToList();
            string Full(Type t) => (t.FullName ?? t.Name).Replace('+', '.');
            string want = string.IsNullOrEmpty(ns) ? local : ns + "." + local;
            bool Answers(Type t)
            {
                if (Full(t) == want) return true;
                if (string.IsNullOrEmpty(ns) && t.Namespace == UiNamespace && t.Name == local) return true;
                // [UxmlElement("custom-name")]
                var attr = t.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "UxmlElementAttribute");
                string custom = attr?.GetType().GetProperty("name")?.GetValue(attr) as string;
                return !string.IsNullOrEmpty(custom) && (string.IsNullOrEmpty(t.Namespace) ? custom : t.Namespace + "." + custom) == want;
            }
            if (types.Any(Answers)) return;
            string space = string.IsNullOrEmpty(ns) ? UiNamespace : ns;
            var candidates = types.Where(t => t.Namespace == space).Select(t => t.Name).Distinct().ToList();
            var close = AnimationErrors.Close(local, candidates);
            throw new ArgumentException($"No element type '{local}' in {space}." +
                                        (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : " Use a VisualElement type such as VisualElement, Label, Button, TextField, Toggle or Slider, or a custom element's namespace and name."));
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

        internal static string ApplyUss(string text, UxmlOp op, string path, out object result)
        {
            string kind = (op.op ?? "").Trim().ToLowerInvariant();
            if (!s_UssOps.Contains(kind))
            {
                if (s_UxmlOps.Contains(kind)) throw new ArgumentException($"'{kind}' is a UXML op: pass the .uxml file. USS ops: {string.Join(", ", s_UssOps)}.");
                throw new ArgumentException(string.IsNullOrEmpty(kind) ? $"An op needs \"op\": {string.Join(", ", s_UssOps)}." : UnknownOp(kind, s_UssOps, "USS"));
            }
            if (string.IsNullOrWhiteSpace(op.selector)) throw new ArgumentException($"{kind} needs the rule's selector, as written in the file (such as .primary or #menu > Button).");
            string selector = Regex.Replace(op.selector.Trim(), @"\s+", " ");
            if (selector.IndexOfAny(new[] { '{', '}', ';' }) >= 0) throw new ArgumentException($"'{selector}' isn't a USS selector: leave out braces and semicolons.");
            var rules = UssRules(text);
            var rule = rules.FirstOrDefault(r => r.selector == selector);
            string NoRule() => $"No rule '{selector}' in the style sheet." + AnimationErrors.Hint(selector, rules.Select(r => r.selector), "Rules", InspectHint(path));
            switch (kind)
            {
                case "set_rule":
                {
                    if (string.IsNullOrWhiteSpace(op.properties)) throw new ArgumentException("set_rule needs properties, such as \"color: #fff; width: 120px\".");
                    var props = rule == null || op.replace ? new List<KeyValuePair<string, string>>() : rule.properties.ToList();
                    foreach (string decl in op.properties.Split(';'))
                    {
                        int colon = decl.IndexOf(':');
                        if (colon <= 0) { if (decl.Trim().Length > 0) throw new ArgumentException($"'{decl.Trim()}' isn't property: value."); continue; }
                        string k = decl.Substring(0, colon).Trim(), v = decl.Substring(colon + 1).Trim();
                        if (v.Length == 0) throw new ArgumentException($"'{k}' has no value: give \"{k}: <value>\", or remove it with remove_property.");
                        if (v.IndexOfAny(new[] { '{', '}' }) >= 0) throw new ArgumentException($"'{v}' isn't a USS value.");
                        int at = props.FindIndex(p => p.Key == k);
                        if (at >= 0) props[at] = new KeyValuePair<string, string>(k, v); else props.Add(new KeyValuePair<string, string>(k, v));
                    }
                    string rendered = RenderRule(selector, props);
                    result = new { op = "set_rule", selector, properties = props.Count, created = rule == null };
                    return rule == null ? text.TrimEnd() + "\n\n" + rendered + "\n" : text.Substring(0, rule.start) + rendered + text.Substring(rule.start + rule.length);
                }
                case "remove_rule":
                {
                    if (rule == null) throw new ArgumentException(NoRule());
                    result = new { op = "remove_rule", selector };
                    return text.Substring(0, rule.start) + text.Substring(rule.start + rule.length);
                }
                default: // remove_property
                {
                    if (rule == null) throw new ArgumentException(NoRule());
                    var names = (op.properties ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
                    if (names.Count == 0) throw new ArgumentException("remove_property needs properties: the names, such as \"color; width\".");
                    foreach (string n in names)
                        if (!rule.properties.Any(p => p.Key == n))
                            throw new ArgumentException($"Rule '{selector}' has no property '{n}'." + AnimationErrors.Hint(n, rule.properties.Select(p => p.Key), "Properties", InspectHint(path)));
                    var props = rule.properties.Where(p => !names.Contains(p.Key)).ToList();
                    result = new { op = "remove_property", selector, removed = rule.properties.Count - props.Count };
                    return text.Substring(0, rule.start) + RenderRule(selector, props) + text.Substring(rule.start + rule.length);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // The live visual tree

        private const string LiveHelp = "Add a UIDocument with Panel Settings and a Source Asset to the scene, or enter Play mode first (editor.playmode start).";

        /// <summary>The UIDocuments with a live panel; not found, naming the documents there are, when --document names none of them.</summary>
        private static List<(string name, VisualElement root)> Roots(string document)
        {
            var all = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude)
                .Where(d => d.rootVisualElement != null && d.rootVisualElement.panel != null)
                .Select(d => (d.name, d.rootVisualElement)).OrderBy(d => d.name, StringComparer.Ordinal).ToList();
            if (all.Count == 0) throw new AnimNotFoundException($"No active UIDocument with a live panel in the open scenes. {LiveHelp}");
            if (string.IsNullOrWhiteSpace(document)) return all;
            var one = all.Where(d => string.Equals(d.name, document.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (one.Count == 0)
                throw new AnimNotFoundException($"No active UIDocument on a GameObject named '{document.Trim()}'." + AnimationErrors.Hint(document, all.Select(d => d.name), "Documents", "cliext.uitk tree"));
            return one;
        }

        private static string TypeName(VisualElement e) => e.GetType().Name;

        /// <summary>The most derived public <c>value</c> property (some fields redeclare it, which makes GetProperty ambiguous).</summary>
        private static PropertyInfo ValueProperty(Type t)
        {
            for (var x = t; x != null && x != typeof(object); x = x.BaseType)
            {
                var p = x.GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            return null;
        }

        private static bool HasValue(VisualElement e) =>
            ValueProperty(e.GetType()) != null && e.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotifyValueChanged<>));

        private static string TextOf(VisualElement e)
        {
            if (HasValue(e))
            {
                try { return ValueText.Format(ValueProperty(e.GetType()).GetValue(e)); } catch { }
            }
            if (e is TextElement t) return t.text;
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

        private static string ShortName(VisualElement e) => TypeName(e) + (string.IsNullOrEmpty(e.name) ? "" : "#" + e.name);

        private static bool Shown(VisualElement e)
        {
            for (var x = e; x != null; x = x.parent)
                if (x.resolvedStyle.display == DisplayStyle.None) return false;
            return e.resolvedStyle.visibility != Visibility.Hidden;
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

        /// <summary>
        /// "The selector matched N elements; only the first was …", naming them (up to 5), or null for one match: an
        /// ambiguous selector acts on the first match only, which may not be the element meant.
        /// </summary>
        internal static string AmbiguityWarning(string selector, IList<VisualElement> matches, string verb)
        {
            if (matches.Count <= 1) return null;
            var shown = matches.Take(5).Select(PathOf).ToList();
            return $"'{selector}' matched {matches.Count} elements; only the first was {verb}: {string.Join("; ", shown)}{(matches.Count > 5 ? "; ..." : "")}. " +
                   "Narrow the selector (#name, or a parent: \"#menu > Slider\") to pick one.";
        }

        private static void WarnIfAmbiguous(CommandContext cmd, string selector, List<(string doc, VisualElement e)> match, string verb)
        {
            string w = AmbiguityWarning(selector, match.Select(m => m.e).ToList(), verb);
            if (w != null) cmd.Log.Warning(w);
        }

        /// <summary>The live elements matching <paramref name="selector"/>; not found, saying which part the tree lacks, when none do.</summary>
        private static List<(string doc, VisualElement e)> QueryLive(string document, string selector)
        {
            var sel = new UITKSelector(selector);
            var roots = Roots(document);
            var found = new List<(string, VisualElement)>();
            foreach (var (name, root) in roots)
                found.AddRange(sel.Match(root, e => e.Children(), e => e.parent, TypeName, e => e.name, e => e.GetClasses()).Select(e => (name, e)));
            if (found.Count == 0)
            {
                var present = roots.SelectMany(r => UITKSelector.Present(r.root, e => e.Children(), TypeName, e => e.name, e => e.GetClasses()));
                string tree = string.IsNullOrWhiteSpace(document) ? "cliext.uitk tree" : $"cliext.uitk tree --document {document.Trim()}";
                throw new AnimNotFoundException(sel.NoMatch(selector, present, tree));
            }
            return found;
        }

        /// <summary>Errors from the live commands: a bad selector is invalid usage (2), a missing document or element not found (13).</summary>
        private static ExitCode LiveFail(CommandContext cmd, Exception e)
        {
            cmd.Fail(AnimationErrors.Message(e));
            return e is AnimNotFoundException ? ExitCode.NotFound : e is ArgumentException ? ExitCode.InvalidUsage : ExitCode.Failed;
        }

        /// <summary>Click and set_value act on the running game's UI: exit 6 naming editor.playmode start in Edit mode.</summary>
        private static bool NeedPlayMode(CommandContext cmd, string command)
        {
            if (EditorApplication.isPlaying) return true;
            cmd.Fail($"cliext.uitk {command} acts on the running game's UI: enter Play mode first (editor.playmode start).");
            return false;
        }

        [CliGroupCommand(Group, "The live visual tree of the UIDocuments in the open scenes (--document narrows to one): each element's type, name, classes, text or value, screen rect, " +
                                "and whether it is hidden or disabled. --depth limits the depth. Works in Edit mode too, for documents with a live panel.",
            Name = "tree", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Tree(
            CommandContext cmd,
            [CliArg("document", "The UIDocument's GameObject")] string document = "",
            [CliArg("depth", "How deep to go (0: the roots only)")] int depth = 8)
        {
            if (depth < 0) { cmd.Fail($"--depth must be 0 or more, not {depth}."); return ExitCode.InvalidUsage; }
            try
            {
                foreach (var (name, root) in Roots(document)) cmd.Result.WriteRecord(new { document = name, tree = DescribeLive(root, depth) });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { return LiveFail(cmd, e); }
        }

        [CliGroupCommand(Group, "Find live elements by selector (Type, #name, .class, combined and nested: \"#menu > Button.primary\") in the UIDocuments; each with its path, text or value, rect and state. " +
                                "No match exits 13 naming the part the tree lacks.",
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
            catch (ArgumentException e) { return LiveFail(cmd, e); }
        }

        private static readonly Type[] s_ClickEvents = { typeof(ClickEvent), typeof(NavigationSubmitEvent) };

        /// <summary>
        /// Whether <paramref name="e"/> has a ClickEvent or NavigationSubmitEvent callback (read from UI Toolkit's
        /// internals); null when they can't be read, so the caller doesn't refuse a click it can't judge.
        /// </summary>
        private static bool? HasClickCallback(VisualElement e)
        {
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var registry = typeof(CallbackEventHandler).GetField("m_CallbackRegistry", F)?.GetValue(e);
                if (registry == null) return false;
                foreach (string listName in new[] { "m_TrickleDownCallbacks", "m_BubbleUpCallbacks" })
                {
                    var dyn = registry.GetType().GetField(listName, F)?.GetValue(registry);
                    if (dyn == null) return null;
                    foreach (string inner in new[] { "m_Callbacks", "m_TemporaryCallbacks" })
                    {
                        var list = dyn.GetType().GetField(inner, F)?.GetValue(dyn);
                        if (list == null) continue;
                        var array = list.GetType().GetField("m_Array", F)?.GetValue(list) as Array;
                        var count = list.GetType().GetField("m_Count", F)?.GetValue(list) as int?;
                        if (array == null || count == null) return null;
                        for (int i = 0; i < count.Value && i < array.Length; i++)
                        {
                            var cb = array.GetValue(i);
                            var type = cb?.GetType().GetProperty("eventType", F)?.GetValue(cb) as Type;
                            if (type == null) return null;
                            if (s_ClickEvents.Contains(type)) return true;
                        }
                    }
                }
                return false;
            }
            catch { return null; }
        }

        [CliGroupCommand(Group, "Click the first element matching the selector, as a submit from navigation does: a Button (or the Button it is inside) runs its clicked handlers, " +
                                "and ClickEvent callbacks on the element and its parents run. Fails on a disabled or hidden element, and on one nothing would answer. Use in Play mode.",
            Name = "click", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Click(
            CommandContext cmd,
            [CliArg("selector", "Selector for the element: Type, #name, .class, combined and nested (\"#menu > Button.primary\")", Required = true)] string selector,
            [CliArg("document", "The UIDocument's GameObject")] string document = "")
        {
            if (!NeedPlayMode(cmd, "click")) return ExitCode.Failed;
            try
            {
                var match = QueryLive(document, selector);
                var e = match[0].e;
                // A Label or icon inside a Button clicks the Button.
                var button = e as Button ?? e.GetFirstAncestorOfType<Button>();
                var target = button ?? e;
                if (!target.enabledInHierarchy) { cmd.Fail($"{PathOf(target)} is disabled: a player can't click it."); return ExitCode.Failed; }
                if (!Shown(target)) { cmd.Fail($"{PathOf(target)} is hidden (display: none or visibility: hidden): a player can't click it."); return ExitCode.Failed; }
                if (button == null)
                {
                    bool? any = false;
                    for (var x = e; x != null && any == false; x = x.parent) any = HasClickCallback(x);
                    if (any == false)
                    {
                        cmd.Fail($"{PathOf(e)} isn't a Button and has no ClickEvent callback, so a click would do nothing. Click a Button (cliext.uitk query --selector Button lists them).");
                        return ExitCode.Failed;
                    }
                }
                using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = target; target.SendEvent(submit); }
                using (var click = ClickEvent.GetPooled()) { click.target = target; target.SendEvent(click); }
                WarnIfAmbiguous(cmd, selector, match, "clicked");
                cmd.Result.Write(new { clicked = PathOf(target), matches = match.Count });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { return LiveFail(cmd, e); }
        }

        [CliGroupCommand(Group, "Set the value of the first matching field (TextField, Toggle, Slider, DropdownField, ... anything with a value) or a Label's text; the field sends its change event. " +
                                "Fails on an element with no value (use click for a Button), a value the field can't take (out of a slider's range, not a dropdown choice), a disabled field, or the value it already has. Returns matches (how many elements the selector matched) and warns, naming them, when it is more than one. Use in Play mode.",
            Name = "set_value", Tags = new[] { "cliext", "ui" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetValue(
            CommandContext cmd,
            [CliArg("selector", "Selector for the element: Type, #name, .class, combined and nested (\"#menu > Button.primary\")", Required = true)] string selector,
            [CliArg("value", "The value as the field takes it: text, a number, true/false, or a dropdown choice", Required = true)] string value,
            [CliArg("document", "The UIDocument's GameObject")] string document = "")
        {
            if (!NeedPlayMode(cmd, "set_value")) return ExitCode.Failed;
            try
            {
                var match = QueryLive(document, selector);
                var e = match[0].e;
                string before = TextOf(e);
                if (HasValue(e))
                {
                    var prop = ValueProperty(e.GetType());
                    if (!prop.CanWrite || prop.GetSetMethod() == null) throw new AnimUsageException($"{PathOf(e)}'s value is read-only.");
                    object parsed = ValueText.Parse(value, prop.PropertyType);
                    CheckFieldTakes(e, parsed);
                    if (!e.enabledInHierarchy) { cmd.Fail($"{PathOf(e)} is disabled: a player can't change it."); return ExitCode.Failed; }
                    if (ValueText.Format(parsed) == before) { cmd.Fail($"{PathOf(e)} is already {before}: nothing changed and no change event was sent."); return ExitCode.Failed; }
                    prop.SetValue(e, parsed);
                }
                else if (e is Label l)
                {
                    if (l.text == value) { cmd.Fail($"{PathOf(e)} already reads '{value}': nothing changed."); return ExitCode.Failed; }
                    l.text = value;
                }
                else
                {
                    string hint = e is Button ? " Use cliext.uitk click for a Button." : " set_value takes a field (TextField, Toggle, Slider, DropdownField, ...) or a Label.";
                    throw new AnimUsageException($"{PathOf(e)} ({TypeName(e)}) has no value to set.{hint}");
                }
                string after = TextOf(e);
                if (HasValue(e) && after == before)
                {
                    cmd.Fail($"{PathOf(e)} didn't take '{value}': it still reads {before}.");
                    return ExitCode.Failed;
                }
                WarnIfAmbiguous(cmd, selector, match, "set");
                cmd.Result.Write(new { element = PathOf(e), before, value = after, matches = match.Count });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is TargetInvocationException)
            {
                // A value the field's type can't take (a bad number or bool, an unknown enum name) is invalid usage, like a bad selector.
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                return LiveFail(cmd, inner);
            }
        }

        /// <summary>Invalid usage when a slider's range or a dropdown's choices leave out the value.</summary>
        private static void CheckFieldTakes(VisualElement e, object value)
        {
            var t = e.GetType();
            var low = t.GetProperty("lowValue", BindingFlags.Instance | BindingFlags.Public);
            var high = t.GetProperty("highValue", BindingFlags.Instance | BindingFlags.Public);
            if (low != null && high != null && value is IComparable c)
            {
                object lo = low.GetValue(e), hi = high.GetValue(e);
                if (lo is IComparable a && hi is IComparable b && lo.GetType() == value.GetType() && hi.GetType() == value.GetType())
                {
                    var min = a.CompareTo(b) <= 0 ? lo : hi;
                    var max = a.CompareTo(b) <= 0 ? hi : lo;
                    if (c.CompareTo(min) < 0 || c.CompareTo(max) > 0)
                        throw new AnimUsageException($"{ValueText.Format(value)} is outside {ShortName(e)}'s range {ValueText.Format(min)} to {ValueText.Format(max)}.");
                }
            }
            var choices = t.GetProperty("choices", BindingFlags.Instance | BindingFlags.Public)?.GetValue(e) as IEnumerable;
            if (choices != null && !(value is Enum))
            {
                var names = choices.Cast<object>().Select(ValueText.Format).ToList();
                if (names.Count > 0 && !names.Contains(ValueText.Format(value)))
                {
                    var close = AnimationErrors.Close(ValueText.Format(value), names);
                    throw new AnimUsageException($"'{ValueText.Format(value)}' isn't one of {ShortName(e)}'s choices." +
                                                 (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : $" Choices: {string.Join(", ", names.Take(20))}."));
                }
            }
        }
    }
}

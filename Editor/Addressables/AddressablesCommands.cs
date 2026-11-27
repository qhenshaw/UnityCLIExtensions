using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace UnityCLIExtensions.Addressables
{
    [Serializable]
    internal class AddressablesOp
    {
        /// <summary>add_group, remove_group, rename_group, set_group, add_entry, remove_entry, move_entry, set_address, set_labels, add_label, remove_label.</summary>
        public string op;
        public string group;
        /// <summary>An asset path, or a folder (its assets become one entry each with add_entry and folder: true... a folder entry otherwise).</summary>
        public string path;
        public string address;
        /// <summary>Labels separated by ';'. For set_labels, the entry's full label set.</summary>
        public string labels;
        public string name;
        /// <summary>For set_group: "bundleMode=PackSeparately;compression=LZ4;includeInBuild=true".</summary>
        public string set;
    }

    [Serializable]
    internal class AddressablesOpList
    {
        public AddressablesOp[] ops;
    }

    /// <summary>
    /// <c>cliext.addressables</c>: Addressables groups, entries, addresses and labels; content builds; and the Analyze
    /// rules (duplicate dependencies and others) with their results as data. Built when the Addressables package is installed.
    /// </summary>
    internal static class AddressablesCommands
    {
        private const string Group = "cliext.addressables";

        private static AddressableAssetSettings Settings(bool create = false) =>
            AddressableAssetSettingsDefaultObject.GetSettings(create)
            ?? throw new ArgumentException("This project has no Addressables settings yet. Run cliext.addressables init.");

        private static AddressableAssetGroup FindGroup(AddressableAssetSettings s, string name) =>
            s.groups.FirstOrDefault(g => g != null && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No group '{name}'. Groups: {string.Join(", ", s.groups.Where(g => g != null).Select(g => g.Name))}");

        private static string Guid(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID((path ?? "").Trim(), AssetPathToGUIDOptions.OnlyExistingAssets);
            if (string.IsNullOrEmpty(guid)) throw new ArgumentException($"No asset at '{path}'.");
            return guid;
        }

        private static object DescribeGroup(AddressableAssetGroup g)
        {
            var bundled = g.GetSchema<BundledAssetGroupSchema>();
            return new
            {
                name = g.Name,
                isDefault = g.Default ? true : (bool?)null,
                entries = g.entries.Count,
                includeInBuild = bundled?.IncludeInBuild,
                bundleMode = bundled?.BundleMode.ToString(),
                compression = bundled?.Compression.ToString(),
                buildPath = bundled?.BuildPath.GetValue(g.Settings),
                loadPath = bundled?.LoadPath.GetValue(g.Settings),
                schemas = g.Schemas.Where(x => x != null).Select(x => x.GetType().Name).ToArray()
            };
        }

        [CliGroupCommand(Group, "Addressables at a glance: whether settings exist, the active profile and play mode script, labels, and every group with its entry count, packing, compression and paths.",
            Name = "status", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Status(CommandContext cmd)
        {
            var s = AddressableAssetSettingsDefaultObject.GetSettings(false);
            if (s == null) { cmd.Result.Write(new { initialized = false, next = "cliext.addressables init" }); return ExitCode.Success; }
            cmd.Result.Write(new
            {
                initialized = true,
                settings = AssetDatabase.GetAssetPath(s),
                profile = s.profileSettings.GetProfileName(s.activeProfileId),
                playModeScript = s.ActivePlayModeDataBuilder?.Name,
                labels = s.GetLabels().ToArray(),
                groups = s.groups.Where(g => g != null).Select(DescribeGroup).ToArray()
            });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Create the Addressables settings (AddressableAssetsData) if the project has none, as the Groups window's Create button does.",
            Name = "init", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Init(CommandContext cmd)
        {
            bool existed = AddressableAssetSettingsDefaultObject.GetSettings(false) != null;
            var s = Settings(create: true);
            cmd.Result.Write(new { settings = AssetDatabase.GetAssetPath(s), created = !existed, groups = s.groups.Where(g => g != null).Select(g => g.Name).ToArray() });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "The Addressable entries: address, asset path, group and labels; --group, --label and --filter (part of the address or path) narrow it.",
            Name = "entries", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Entries(
            CommandContext cmd,
            [CliArg("group", "Only this group")] string group = "",
            [CliArg("label", "Only entries with this label")] string label = "",
            [CliArg("filter", "Only addresses or paths containing this")] string filter = "")
        {
            try
            {
                var s = Settings();
                foreach (var g in s.groups.Where(g => g != null && (group == "" || string.Equals(g.Name, group, StringComparison.OrdinalIgnoreCase))))
                    foreach (var e in g.entries)
                    {
                        if (label != "" && !e.labels.Contains(label)) continue;
                        if (filter != "" && e.address.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && e.AssetPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        cmd.Result.WriteRecord(new { address = e.address, path = e.AssetPath, group = g.Name, labels = e.labels.ToArray(), folder = e.IsFolder ? true : (bool?)null });
                    }
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
        }

        [CliGroupCommand(Group, "Edit Addressables with a JSON list of ops, saved once: add_group (name, set), remove_group, rename_group (group, name), set_group (group, set=\"bundleMode=PackSeparately;compression=LZ4;includeInBuild=true\"), " +
                                "add_entry (path, group, address, labels), remove_entry, move_entry (path, group), set_address (path, address), set_labels (path, labels: the full set), add_label / remove_label (name: project labels). An op that fails saves nothing: groups, entries and labels are left as they were.",
            Name = "edit", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            AddressablesOpList list;
            try { string t = json.Trim(); list = JsonUtility.FromJson<AddressablesOpList>(t.StartsWith("[") ? "{\"ops\":" + t + "}" : t); }
            catch (Exception e) { cmd.Log.Error($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Log.Error("No ops given."); return ExitCode.InvalidUsage; }

            AddressableAssetSettings s;
            try { s = Settings(); } catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            // The settings, its groups and their schemas live in the settings folder; groups kept elsewhere are watched too.
            string settingsPath = AssetDatabase.GetAssetPath(s);
            var watched = new List<string> { settingsPath };
            foreach (var g in s.groups.Where(g => g != null))
            {
                watched.Add(AssetDatabase.GetAssetPath(g));
                watched.AddRange(g.Schemas.Where(x => x != null).Select(x => AssetDatabase.GetAssetPath(x)));
            }
            using var rollback = AssetRollback.Begin(watched, new[] { Path.GetDirectoryName(settingsPath) });
            var results = new List<object>();
            for (int i = 0; i < list.ops.Length; i++)
            {
                try { results.Add(Apply(s, list.ops[i])); }
                catch (Exception e) when (e is ArgumentException || e is FormatException)
                {
                    rollback.Rollback();
                    cmd.Result.Write(new { applied = 0, failedAt = i, error = e.Message, saved = false });
                    cmd.Log.Error($"Op {i} ({list.ops[i].op}) failed: {e.Message} Nothing was saved.");
                    return ExitCode.Failed;
                }
            }
            AssetDatabase.SaveAssets();
            cmd.Result.Write(new { applied = results.Count, results });
            return ExitCode.Success;
        }

        private static object Apply(AddressableAssetSettings s, AddressablesOp op)
        {
            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add_group":
                {
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("add_group needs a name.");
                    if (s.groups.Any(g => g != null && g.Name == op.name)) throw new ArgumentException($"Group '{op.name}' exists.");
                    var g2 = s.CreateGroup(op.name, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                    SetGroup(g2, op.set);
                    return new { op = "add_group", group = DescribeGroup(g2) };
                }
                case "remove_group":
                {
                    var g = FindGroup(s, op.group);
                    if (g.Default) throw new ArgumentException("That's the default group; make another the default first.");
                    s.RemoveGroup(g);
                    return new { op = "remove_group", group = op.group };
                }
                case "rename_group":
                {
                    var g = FindGroup(s, op.group);
                    g.Name = op.name;
                    return new { op = "rename_group", group = g.Name };
                }
                case "set_group":
                {
                    var g = FindGroup(s, op.group);
                    SetGroup(g, op.set);
                    return new { op = "set_group", group = DescribeGroup(g) };
                }
                case "add_entry":
                case "move_entry":
                {
                    var g = string.IsNullOrWhiteSpace(op.group) ? s.DefaultGroup : FindGroup(s, op.group);
                    var e = s.CreateOrMoveEntry(Guid(op.path), g, false, true);
                    if (!string.IsNullOrWhiteSpace(op.address)) e.SetAddress(op.address);
                    foreach (string l in Split(op.labels)) { if (!s.GetLabels().Contains(l)) s.AddLabel(l); e.SetLabel(l, true, true); }
                    return new { op = op.op, address = e.address, path = e.AssetPath, group = g.Name, labels = e.labels.ToArray() };
                }
                case "remove_entry":
                {
                    if (!s.RemoveAssetEntry(Guid(op.path))) throw new ArgumentException($"'{op.path}' isn't Addressable.");
                    return new { op = "remove_entry", path = op.path };
                }
                case "set_address":
                {
                    var e = s.FindAssetEntry(Guid(op.path)) ?? throw new ArgumentException($"'{op.path}' isn't Addressable.");
                    e.SetAddress(op.address);
                    return new { op = "set_address", path = op.path, address = e.address };
                }
                case "set_labels":
                {
                    var e = s.FindAssetEntry(Guid(op.path)) ?? throw new ArgumentException($"'{op.path}' isn't Addressable.");
                    var want = Split(op.labels).ToList();
                    foreach (string l in e.labels.ToList()) if (!want.Contains(l)) e.SetLabel(l, false);
                    foreach (string l in want) { if (!s.GetLabels().Contains(l)) s.AddLabel(l); e.SetLabel(l, true, true); }
                    return new { op = "set_labels", path = op.path, labels = e.labels.ToArray() };
                }
                case "add_label":
                    s.AddLabel(op.name);
                    return new { op = "add_label", name = op.name };
                case "remove_label":
                    s.RemoveLabel(op.name);
                    return new { op = "remove_label", name = op.name };
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: add_group, remove_group, rename_group, set_group, add_entry, remove_entry, move_entry, set_address, set_labels, add_label, remove_label.");
            }
        }

        private static IEnumerable<string> Split(string s) => (s ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());

        private static void SetGroup(AddressableAssetGroup g, string pairs)
        {
            var bundled = g.GetSchema<BundledAssetGroupSchema>();
            foreach (string pair in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{pair}' isn't name=value.");
                string k = pair.Substring(0, eq).Trim().ToLowerInvariant(), v = pair.Substring(eq + 1).Trim();
                if (bundled == null) throw new ArgumentException($"{g.Name} has no BundledAssetGroupSchema.");
                switch (k)
                {
                    case "bundlemode": bundled.BundleMode = (BundledAssetGroupSchema.BundlePackingMode)Enum.Parse(typeof(BundledAssetGroupSchema.BundlePackingMode), v, true); break;
                    case "compression": bundled.Compression = (BundledAssetGroupSchema.BundleCompressionMode)Enum.Parse(typeof(BundledAssetGroupSchema.BundleCompressionMode), v, true); break;
                    case "includeinbuild": bundled.IncludeInBuild = (bool)ValueText.Parse(v, typeof(bool)); break;
                    default: throw new ArgumentException($"Unknown group setting '{k}': bundleMode, compression, includeInBuild.");
                }
            }
            EditorUtility.SetDirty(bundled);
        }

        // ---------------------------------------------------------------------------------------------------------

        [CliGroupCommand(Group, "Build the Addressables content for the active build target (as Build > New Build > Default Build Script). Returns the duration, output path, bundle count and any error. " +
                                "--clean true clears the build cache first. Pass the CLI a --timeout fit for the project's size.",
            Name = "build", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Build(CommandContext cmd, [CliArg("clean", "Clear the previous build first")] bool clean = false)
        {
            try { Settings(); } catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { cmd.Log.Error("Stop Play mode first."); return ExitCode.Failed; }
            if (clean) AddressableAssetSettings.CleanPlayerContent();
            // The first build asks, in a modal dialog, whether to turn on the build report; answer it as seen (no
            // change to the setting) so the build doesn't wait on it.
            var informed = typeof(ProjectConfigData).GetProperty("UserHasBeenInformedAboutBuildReportSettingPreBuild",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            try { if (informed != null && informed.GetValue(null) is false) informed.SetValue(null, true); } catch { }
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            bool ok = string.IsNullOrEmpty(result.Error);
            cmd.Result.Write(new
            {
                success = ok,
                error = ok ? null : result.Error,
                seconds = Math.Round(result.Duration, 2),
                output = result.OutputPath,
                bundles = result.AssetBundleBuildResults?.Count,
                target = EditorUserBuildSettings.activeBuildTarget.ToString()
            });
            if (!ok) { cmd.Log.Error(result.Error); return ExitCode.Failed; }
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Run the Addressables Analyze rules (duplicate bundle dependencies, duplicate scene and Resources dependencies, the bundle layout; --rule narrows to one by part of its name) " +
                                "and return each result with its severity. Nothing is fixed. Exit 6 when a rule reports errors.",
            Name = "analyze", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Analyze(CommandContext cmd, [CliArg("rule", "Only rules whose name contains this")] string rule = "", [CliArg("limit", "Results per rule")] int limit = 100)
        {
            AddressableAssetSettings s;
            try { s = Settings(); } catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            int errors = 0;
            foreach (var type in TypeCache.GetTypesDerivedFrom<AnalyzeRule>().Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
            {
                var r = (AnalyzeRule)Activator.CreateInstance(type);
                if (rule != "" && r.ruleName.IndexOf(rule, StringComparison.OrdinalIgnoreCase) < 0) continue;
                List<AnalyzeRule.AnalyzeResult> found;
                try { found = r.RefreshAnalysis(s); }
                catch (Exception e) { cmd.Result.WriteRecord(new { rule = r.ruleName, error = e.Message }); continue; }
                finally { try { r.ClearAnalysis(); } catch { } }
                var real = found.Where(x => x.resultName != "No issues found").ToList();
                errors += real.Count(x => x.severity == MessageType.Error);
                cmd.Result.WriteRecord(new
                {
                    rule = r.ruleName,
                    issues = real.Count,
                    results = real.Take(limit).Select(x => new { severity = x.severity.ToString(), name = x.resultName.Replace(AnalyzeRule.kDelimiter.ToString(), " > ") }).ToArray()
                });
            }
            if (errors > 0) { cmd.Log.Error($"{errors} error result(s)."); return ExitCode.Failed; }
            return ExitCode.Success;
        }
    }
}

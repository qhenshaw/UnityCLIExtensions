using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCLIExtensions.Addressables
{
    [Serializable]
    internal class AddressablesOp
    {
        /// <summary>add_group, remove_group, rename_group, set_group, add_entry, remove_entry, move_entry, set_address, set_labels, add_label, remove_label.</summary>
        public string op;
        public string group;
        /// <summary>The asset's path (Assets/... or Packages/...) or GUID; a folder makes a folder entry.</summary>
        public string path;
        public string address;
        /// <summary>Labels separated by ';'. For set_labels, the entry's full label set.</summary>
        public string labels;
        public string name;
        /// <summary>For add_group and set_group: "bundleMode=PackSeparately;compression=LZ4;includeInBuild=true".</summary>
        public string set;
    }

    [Serializable]
    internal class AddressablesOpList
    {
        public AddressablesOp[] ops;
    }

    /// <summary>A parsed <c>set</c> of add_group and set_group: the settings it names, the others null.</summary>
    internal sealed class GroupSettings
    {
        public BundledAssetGroupSchema.BundlePackingMode? bundleMode;
        public BundledAssetGroupSchema.BundleCompressionMode? compression;
        public bool? includeInBuild;
    }

    /// <summary>
    /// <c>cliext.addressables</c>: Addressables groups, entries, addresses and labels; content builds; and the Analyze
    /// rules (duplicate dependencies and others) with their results as data. Built when the Addressables package is installed.
    /// </summary>
    internal static class AddressablesCommands
    {
        private const string Group = "cliext.addressables";
        internal const string InitFirst = "This project has no Addressables settings yet: run cliext.addressables init first.";
        internal const string Ops = "add_group, remove_group, rename_group, set_group, add_entry, remove_entry, move_entry, set_address, set_labels, add_label, remove_label";
        private static readonly string[] s_SettingNames = { "bundleMode", "compression", "includeInBuild" };

        private static bool TryGetSettings(CommandContext cmd, out AddressableAssetSettings s)
        {
            s = AddressableAssetSettingsDefaultObject.GetSettings(false);
            if (s != null) return true;
            cmd.Fail(InitFirst);
            return false;
        }

        private static IEnumerable<AddressableAssetGroup> Groups(AddressableAssetSettings s) => s.groups.Where(g => g != null);

        internal static AddressableAssetGroup FindGroup(AddressableAssetSettings s, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new AnimUsageException("Give a group name. cliext.addressables status lists the groups.");
            return Groups(s).FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new AnimNotFoundException($"No group '{name}'.{AnimationErrors.Hint(name, Groups(s).Select(g => g.Name), "Groups", "cliext.addressables status")}");
        }

        internal static string FindLabel(AddressableAssetSettings s, string name)
        {
            var labels = s.GetLabels();
            return labels.FirstOrDefault(l => l == name)
                   ?? throw new AnimNotFoundException($"No label '{name}'.{AnimationErrors.Hint(name, labels, "Labels", "cliext.addressables status")}");
        }

        /// <summary>The GUID of an op's asset: usage error for no path or one outside Assets/ and Packages/, not found for a missing asset.</summary>
        internal static string AssetGuid(string path, string op)
        {
            string given = (path ?? "").Trim();
            if (given.Length == 0) throw new AnimUsageException($"{op} needs path: the asset's path, such as Assets/Prefabs/Enemy.prefab.");
            string p = AnimationErrors.AssetPath(given);
            if (p.Length == 0) throw new AnimNotFoundException($"No asset has the GUID {given}. Give the asset's path (asset find lists assets).");
            if (!p.StartsWith("Assets/", StringComparison.Ordinal) && !p.StartsWith("Packages/", StringComparison.Ordinal))
                throw new AnimUsageException($"path '{given}' isn't under Assets/ or Packages/: give the asset's project path, such as Assets/Prefabs/Enemy.prefab.");
            string guid = AssetDatabase.AssetPathToGUID(p, AssetPathToGUIDOptions.OnlyExistingAssets);
            if (string.IsNullOrEmpty(guid)) throw new AnimNotFoundException($"No asset at '{p}'. Check the path (asset find --name <name> lists assets).");
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
                includeInBuild = bundled != null ? g.IncludeInBuild : (bool?)null,
                bundleMode = bundled?.BundleMode.ToString(),
                compression = bundled?.Compression.ToString(),
                buildPath = bundled?.BuildPath.GetValue(g.Settings),
                loadPath = bundled?.LoadPath.GetValue(g.Settings),
                schemas = g.Schemas.Where(x => x != null).Select(x => x.GetType().Name).ToArray()
            };
        }

        // The Addressables build and the bundle Analyze rules ask in a modal dialog to save unsaved scenes first.
        private static bool RefuseDirtyScenes(CommandContext cmd, string what)
        {
            var dirty = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty) dirty.Add(string.IsNullOrEmpty(scene.path) ? "(untitled)" : scene.path);
            }
            if (dirty.Count == 0) return false;
            cmd.Fail($"{string.Join(", ", dirty)} {(dirty.Count == 1 ? "has" : "have")} unsaved changes, and {what} would stop at Unity's \"Unsaved Scenes\" dialog. " +
                     "Save them (cliext.scene save_open --scene <path>) or discard them (reopen the scene from disk), then run it again.");
            return true;
        }

        [CliGroupCommand(Group, "Addressables at a glance: whether settings exist, the active profile and play mode script, labels, and every group with its entry count, packing, compression and paths. " +
                                "Without settings it says so (initialized: false) and exits 0.",
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
                groups = Groups(s).Select(DescribeGroup).ToArray()
            });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Create the Addressables settings (Assets/AddressableAssetsData, registered in ProjectSettings/EditorBuildSettings.asset) if the project has none, as the Groups window's Create button does. " +
                                "Exit 6 when they exist already. Every other command but status needs this first.",
            Name = "init", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Init(CommandContext cmd)
        {
            var existing = AddressableAssetSettingsDefaultObject.GetSettings(false);
            if (existing != null)
            {
                cmd.Fail($"Addressables settings exist already at '{AssetDatabase.GetAssetPath(existing)}': nothing would change. cliext.addressables status shows them.");
                return ExitCode.Failed;
            }
            var s = AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (s == null) { cmd.Fail("Unity didn't create the Addressables settings: see the Console (cliext.compile diagnostics), then run init again."); return ExitCode.Failed; }
            cmd.Result.Write(new { settings = AssetDatabase.GetAssetPath(s), created = true, groups = Groups(s).Select(g => g.Name).ToArray() });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "The Addressable entries: address, asset path, group and labels; --group, --label and --filter (part of the address or path) narrow it. " +
                                "One record per entry; none when nothing matches --filter. An unknown group or label exits 13 (cliext.addressables status lists them); no settings exits 6.",
            Name = "entries", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Entries(
            CommandContext cmd,
            [CliArg("group", "Only this group")] string group = "",
            [CliArg("label", "Only entries with this label")] string label = "",
            [CliArg("filter", "Only addresses or paths containing this")] string filter = "")
        {
            if (!TryGetSettings(cmd, out var s)) return ExitCode.Failed;
            AddressableAssetGroup only = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(group)) only = FindGroup(s, group);
                if (!string.IsNullOrWhiteSpace(label)) label = FindLabel(s, label.Trim());
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            foreach (var g in Groups(s).Where(g => only == null || g == only))
                foreach (var e in g.entries)
                {
                    if (!string.IsNullOrEmpty(label) && !e.labels.Contains(label)) continue;
                    if (filter != "" && e.address.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && e.AssetPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    cmd.Result.WriteRecord(new { address = e.address, path = e.AssetPath, group = g.Name, labels = e.labels.OrderBy(l => l).ToArray(), folder = e.IsFolder ? true : (bool?)null });
                }
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Edit Addressables with a JSON list of ops, saved once: add_group (name, optional set), remove_group (group), rename_group (group, name), " +
                                "set_group (group, set=\"bundleMode=PackSeparately;compression=LZ4;includeInBuild=true\"), add_entry (path, optional group (default group), address, labels), " +
                                "remove_entry (path), move_entry (path, group), set_address (path, address), set_labels (path, labels: the full set, ';'-separated), add_label / remove_label (name: project labels). " +
                                "Labels an entry op names that the project hasn't got are added. Every op's arguments are checked before anything changes, and an op that fails saves nothing: " +
                                "an unknown op, group, label or asset, a bad value, or an op that would change nothing (an asset that is already an entry, a rename to the same name, a set to the values it has) " +
                                "is reported as failedAt with its error, exit 6, and the settings, groups, entries and labels are left as they were, on disk and in memory. No settings exits 6; bad JSON or no ops exits 2.",
            Name = "edit", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            AddressablesOpList list;
            try { string t = json.Trim(); list = JsonUtility.FromJson<AddressablesOpList>(t.StartsWith("[") ? "{\"ops\":" + t + "}" : t); }
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given: pass a JSON array such as [{\"op\":\"add_entry\",\"path\":\"Assets/Prefabs/Enemy.prefab\"}]."); return ExitCode.InvalidUsage; }

            if (!TryGetSettings(cmd, out var s)) return ExitCode.Failed;

            ExitCode Refuse(int i, string error)
            {
                cmd.Result.Write(new { applied = 0, failedAt = i, error, saved = false });
                cmd.Fail($"Op {i} ({list.ops[i].op}) failed: {error} Nothing was changed.");
                return ExitCode.Failed;
            }

            // Every op's own arguments first (op name, fields, values, assets), before anything changes.
            for (int i = 0; i < list.ops.Length; i++)
            {
                try { Check(list.ops[i]); }
                catch (Exception e) { return Refuse(i, AnimationErrors.Message(e)); }
            }

            // The settings, its groups and their schemas live in the settings folder; groups kept elsewhere are watched too.
            string settingsPath = AssetDatabase.GetAssetPath(s);
            var watched = new List<string> { settingsPath };
            foreach (var g in Groups(s))
            {
                watched.Add(AssetDatabase.GetAssetPath(g));
                watched.AddRange(g.Schemas.Where(x => x != null).Select(x => AssetDatabase.GetAssetPath(x)));
            }
            var results = new List<object>();
            using (var rollback = AssetRollback.Begin(watched, new[] { Path.GetDirectoryName(settingsPath) }))
            {
                for (int i = 0; i < list.ops.Length; i++)
                {
                    try { results.Add(Apply(s, list.ops[i])); }
                    catch (Exception e)
                    {
                        rollback.Rollback();
                        return Refuse(i, AnimationErrors.Message(e));
                    }
                }
            }
            // Save the Addressables assets only, not whatever else the project has unsaved.
            s = AddressableAssetSettingsDefaultObject.GetSettings(false);
            AssetDatabase.SaveAssetIfDirty(s);
            foreach (var g in Groups(s))
            {
                AssetDatabase.SaveAssetIfDirty(g);
                foreach (var schema in g.Schemas.Where(x => x != null)) AssetDatabase.SaveAssetIfDirty(schema);
            }
            cmd.Result.Write(new { applied = results.Count, results, saved = true });
            return ExitCode.Success;
        }

        /// <summary>
        /// Checks what an op needs that doesn't depend on the settings: a known op, its fields, its values and its
        /// asset. Throws a one-line error.
        /// </summary>
        internal static void Check(AddressablesOp op)
        {
            string name = (op.op ?? "").Trim().ToLowerInvariant();
            void Need(string value, string field, string what)
            {
                if (string.IsNullOrWhiteSpace(value)) throw new AnimUsageException($"{name} needs {field}: {what}.");
            }
            switch (name)
            {
                case "add_group":
                    Need(op.name, "name", "the new group's name");
                    CheckGroupName(op.name);
                    ParseSettings(op.set, false);
                    break;
                case "remove_group":
                    Need(op.group, "group", "the group's name (cliext.addressables status lists them)");
                    break;
                case "rename_group":
                    Need(op.group, "group", "the group's name (cliext.addressables status lists them)");
                    Need(op.name, "name", "the new name");
                    CheckGroupName(op.name);
                    break;
                case "set_group":
                    Need(op.group, "group", "the group's name (cliext.addressables status lists them)");
                    Need(op.set, "set", "settings such as \"bundleMode=PackSeparately;compression=LZ4;includeInBuild=true\"");
                    ParseSettings(op.set, true);
                    break;
                case "add_entry":
                    AssetGuid(op.path, name);
                    foreach (string l in Split(op.labels)) CheckLabelName(l);
                    break;
                case "move_entry":
                    AssetGuid(op.path, name);
                    Need(op.group, "group", "the group to move it to");
                    break;
                case "remove_entry":
                    AssetGuid(op.path, name);
                    break;
                case "set_address":
                    AssetGuid(op.path, name);
                    Need(op.address, "address", "the new address");
                    break;
                case "set_labels":
                    AssetGuid(op.path, name);
                    foreach (string l in Split(op.labels)) CheckLabelName(l);
                    break;
                case "add_label":
                case "remove_label":
                    Need(op.name, "name", "the label");
                    CheckLabelName(op.name.Trim());
                    break;
                default:
                    var close = AnimationErrors.Close(name, Ops.Split(new[] { ", " }, StringSplitOptions.None));
                    throw new AnimUsageException($"Unknown op '{op.op}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Ops: {Ops}.");
            }
        }

        private static void CheckGroupName(string name)
        {
            char bad = name.FirstOrDefault(c => c == '/' || c == '\\' || Path.GetInvalidFileNameChars().Contains(c));
            if (bad != default) throw new AnimUsageException($"Group name '{name}' has '{bad}', which a group's file name can't: use letters, digits, spaces, '-' or '_'.");
        }

        private static void CheckLabelName(string label)
        {
            if (label.Contains("[") || label.Contains("]")) throw new AnimUsageException($"Label '{label}' has '[' or ']', which Addressables labels can't.");
        }

        /// <summary>Parses "bundleMode=...;compression=...;includeInBuild=..." with one-line errors.</summary>
        internal static GroupSettings ParseSettings(string pairs, bool required)
        {
            var result = new GroupSettings();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string pair = raw.Trim();
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0 || eq == pair.Length - 1) throw new AnimUsageException($"'{pair}' isn't name=value: give settings such as bundleMode=PackSeparately;compression=LZ4.");
                string k = pair.Substring(0, eq).Trim(), v = pair.Substring(eq + 1).Trim();
                if (!seen.Add(k)) throw new AnimUsageException($"set names {k} twice.");
                switch (k.ToLowerInvariant())
                {
                    case "bundlemode": result.bundleMode = Prefixed(k, () => AnimationErrors.ParseEnum<BundledAssetGroupSchema.BundlePackingMode>(v, "bundle packing mode")); break;
                    case "compression": result.compression = Prefixed(k, () => AnimationErrors.ParseEnum<BundledAssetGroupSchema.BundleCompressionMode>(v, "bundle compression mode")); break;
                    case "includeinbuild": result.includeInBuild = Prefixed(k, () => (bool)ValueText.Parse(v, typeof(bool))); break;
                    default:
                        var close = AnimationErrors.Close(k, s_SettingNames);
                        throw new AnimUsageException($"No group setting '{k}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Settings: {string.Join(", ", s_SettingNames)}.");
                }
            }
            if (required && seen.Count == 0) throw new AnimUsageException("set_group needs set: settings such as \"bundleMode=PackSeparately;compression=LZ4;includeInBuild=true\".");
            return result;
        }

        private static T Prefixed<T>(string key, Func<T> parse)
        {
            try { return parse(); }
            catch (ArgumentException e) { throw new AnimUsageException($"{key}: {e.Message}"); }
        }

        private static string Location(AddressableAssetEntry e) => $"in group {e.parentGroup?.Name}, address '{e.address}'";

        internal static object Apply(AddressableAssetSettings s, AddressablesOp op)
        {
            string name = (op.op ?? "").Trim().ToLowerInvariant();
            switch (name)
            {
                case "add_group":
                {
                    string n = op.name.Trim();
                    var same = Groups(s).FirstOrDefault(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (same != null) throw new ArgumentException($"Group '{same.Name}' exists already: use set_group or rename_group, or pick another name.");
                    var g2 = s.CreateGroup(n, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                    if (g2 == null || g2.Name != n) throw new InvalidOperationException($"Addressables didn't create the group '{n}'.");
                    Set(g2, ParseSettings(op.set, false), false);
                    return new { op = "add_group", group = DescribeGroup(g2) };
                }
                case "remove_group":
                {
                    var g = FindGroup(s, op.group);
                    if (g.Default) throw new ArgumentException($"'{g.Name}' is the default group: Addressables needs one. Make another group the default in the Groups window first.");
                    s.RemoveGroup(g);
                    return new { op = "remove_group", group = g.Name };
                }
                case "rename_group":
                {
                    var g = FindGroup(s, op.group);
                    string n = op.name.Trim();
                    if (g.Name == n) throw new ArgumentException($"Group '{g.Name}' has that name already: nothing would change.");
                    var other = Groups(s).FirstOrDefault(x => x != g && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (other != null) throw new ArgumentException($"Group '{other.Name}' exists already: pick another name.");
                    string old = g.Name;
                    g.Name = n;
                    if (g.Name != n) throw new InvalidOperationException($"Unity didn't rename group '{old}' to '{n}' (its asset file couldn't be moved).");
                    return new { op = "rename_group", group = g.Name, before = old };
                }
                case "set_group":
                {
                    var g = FindGroup(s, op.group);
                    if (!Set(g, ParseSettings(op.set, true), true)) throw new ArgumentException($"Group '{g.Name}' has {op.set.Trim()} already: nothing would change.");
                    return new { op = "set_group", group = DescribeGroup(g) };
                }
                case "add_entry":
                {
                    string guid = AssetGuid(op.path, name);
                    var g = string.IsNullOrWhiteSpace(op.group) ? s.DefaultGroup : FindGroup(s, op.group);
                    var existing = s.FindAssetEntry(guid);
                    if (existing != null) throw new ArgumentException($"'{existing.AssetPath}' is Addressable already ({Location(existing)}): use move_entry, set_address or set_labels.");
                    var e = s.CreateOrMoveEntry(guid, g, false, true) ?? throw new InvalidOperationException($"Addressables didn't make '{op.path}' an entry (it can't be Addressable: an Editor-only asset or a script?).");
                    if (!string.IsNullOrWhiteSpace(op.address)) e.SetAddress(op.address.Trim());
                    foreach (string l in Split(op.labels)) { if (!s.GetLabels().Contains(l)) s.AddLabel(l); e.SetLabel(l, true, true); }
                    return new { op = "add_entry", address = e.address, path = e.AssetPath, group = g.Name, labels = e.labels.OrderBy(l => l).ToArray() };
                }
                case "move_entry":
                {
                    string guid = AssetGuid(op.path, name);
                    var e = s.FindAssetEntry(guid) ?? throw new ArgumentException($"'{op.path}' isn't Addressable: use add_entry.");
                    var g = FindGroup(s, op.group);
                    if (e.parentGroup == g) throw new ArgumentException($"'{e.AssetPath}' is in group {g.Name} already: nothing would change.");
                    s.MoveEntry(e, g);
                    return new { op = "move_entry", address = e.address, path = e.AssetPath, group = g.Name };
                }
                case "remove_entry":
                {
                    string guid = AssetGuid(op.path, name);
                    if (s.FindAssetEntry(guid) == null || !s.RemoveAssetEntry(guid)) throw new ArgumentException($"'{op.path}' isn't Addressable: nothing to remove. cliext.addressables entries lists the entries.");
                    return new { op = "remove_entry", path = op.path };
                }
                case "set_address":
                {
                    var e = s.FindAssetEntry(AssetGuid(op.path, name)) ?? throw new ArgumentException($"'{op.path}' isn't Addressable: use add_entry with an address.");
                    string a = op.address.Trim();
                    if (e.address == a) throw new ArgumentException($"'{e.AssetPath}' has the address '{a}' already: nothing would change.");
                    e.SetAddress(a);
                    return new { op = "set_address", path = e.AssetPath, address = e.address };
                }
                case "set_labels":
                {
                    var e = s.FindAssetEntry(AssetGuid(op.path, name)) ?? throw new ArgumentException($"'{op.path}' isn't Addressable: use add_entry with labels.");
                    var want = Split(op.labels).Distinct().ToList();
                    if (new HashSet<string>(want).SetEquals(e.labels))
                        throw new ArgumentException($"'{e.AssetPath}' has the labels [{string.Join(";", want)}] already: nothing would change.");
                    foreach (string l in e.labels.ToList()) if (!want.Contains(l)) e.SetLabel(l, false);
                    foreach (string l in want) { if (!s.GetLabels().Contains(l)) s.AddLabel(l); e.SetLabel(l, true, true); }
                    return new { op = "set_labels", path = e.AssetPath, labels = e.labels.OrderBy(l => l).ToArray() };
                }
                case "add_label":
                {
                    string l = op.name.Trim();
                    if (s.GetLabels().Contains(l)) throw new ArgumentException($"The project has the label '{l}' already: nothing would change.");
                    s.AddLabel(l);
                    return new { op = "add_label", name = l };
                }
                case "remove_label":
                {
                    string l = FindLabel(s, op.name.Trim());
                    // Entries keep a removed label until the next build; take it off them too, so they match the project's labels.
                    foreach (var g in Groups(s)) foreach (var e in g.entries.Where(x => x.labels.Contains(l)).ToList()) e.SetLabel(l, false);
                    s.RemoveLabel(l);
                    return new { op = "remove_label", name = l };
                }
                default:
                    Check(op);
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: {Ops}.");
            }
        }

        internal static IEnumerable<string> Split(string s) => (s ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);

        // Applies the named settings; returns whether any changed. With needChange, a group that would not change is left alone.
        private static bool Set(AddressableAssetGroup g, GroupSettings set, bool needChange)
        {
            var bundled = g.GetSchema<BundledAssetGroupSchema>();
            bool any = set.bundleMode.HasValue || set.compression.HasValue || set.includeInBuild.HasValue;
            if (!any) return false;
            if (bundled == null) throw new ArgumentException($"Group '{g.Name}' has no BundledAssetGroupSchema, so it has no bundle settings.");
            bool changes = set.bundleMode.HasValue && bundled.BundleMode != set.bundleMode.Value
                           || set.compression.HasValue && bundled.Compression != set.compression.Value
                           || set.includeInBuild.HasValue && g.IncludeInBuild != set.includeInBuild.Value;
            if (!changes && needChange) return false;
            if (set.bundleMode.HasValue) bundled.BundleMode = set.bundleMode.Value;
            if (set.compression.HasValue) bundled.Compression = set.compression.Value;
            if (set.includeInBuild.HasValue) g.IncludeInBuild = set.includeInBuild.Value;
            EditorUtility.SetDirty(bundled);
            EditorUtility.SetDirty(g);
            return changes;
        }

        // ---------------------------------------------------------------------------------------------------------

        [CliGroupCommand(Group, "Build the Addressables content for the active build target (as Build > New Build > Default Build Script). Returns the duration, output path, bundle count and any error. " +
                                "--clean true clears the build cache first. Pass the CLI a --timeout fit for the project's size. Like a player build, it runs the render pipeline's build step, which can update the pipeline assets' shader prefiltering (URP). No settings, Play mode or an open scene with unsaved changes " +
                                "(the build would stop at Unity's save dialog) exits 6 before building; so does a failed build.",
            Name = "build", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Build(CommandContext cmd, [CliArg("clean", "Clear the previous build first")] bool clean = false)
        {
            if (!TryGetSettings(cmd, out _)) return ExitCode.Failed;
            if (EditorApplication.isPlayingOrWillChangePlaymode) { cmd.Fail("Stop Play mode first (editor.playmode stop), then build."); return ExitCode.Failed; }
            if (RefuseDirtyScenes(cmd, "the build")) return ExitCode.Failed;
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
            if (!ok) { cmd.Fail($"The Addressables build failed: {result.Error}"); return ExitCode.Failed; }
            return ExitCode.Success;
        }

        // The rules the Analyze window shows: the ones registered with AnalyzeSystem.RegisterNewRule (fresh instances, so
        // the window's results are left alone). Every AnalyzeRule type would also list base classes such as BundleRuleBase.
        private static List<AnalyzeRule> Rules()
        {
            IEnumerable<Type> types = null;
            try
            {
                var registered = typeof(AnalyzeSystem).GetProperty("Rules", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null) as IEnumerable<AnalyzeRule>;
                types = registered?.Where(r => r != null).Select(r => r.GetType()).Distinct().ToList();
            }
            catch (Exception) { }
            if (types == null || !types.Any())
                types = TypeCache.GetTypesDerivedFrom<AnalyzeRule>().Where(t => !t.IsAbstract && t != typeof(BundleRuleBase));
            return types.Where(t => t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (AnalyzeRule)Activator.CreateInstance(t)).OrderBy(r => r.ruleName, StringComparer.Ordinal).ToList();
        }

        // The render pipeline assets of the graphics and quality settings: the bundle rules run a content build, and the
        // render pipeline's build step (URP's shader prefiltering) rewrites them.
        private static List<string> PipelineAssetPaths()
        {
            var assets = new List<UnityEngine.Object> { UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline };
            for (int i = 0; i < QualitySettings.names.Length; i++) assets.Add(QualitySettings.GetRenderPipelineAssetAt(i));
            return assets.Where(a => a != null).Select(AssetDatabase.GetAssetPath).Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)).Distinct().ToList();
        }

        /// <summary>
        /// A rule's "nothing found" result, which isn't an issue: severity None and a name ending in "No issues found"
        /// (the base rule's plain "No issues found", or the bundle rules' "&lt;rule&gt; - No issues found.").
        /// </summary>
        internal static bool IsNoIssues(AnalyzeRule.AnalyzeResult r) =>
            r != null && r.severity == MessageType.None && (r.resultName ?? "").Trim().TrimEnd('.').EndsWith("No issues found", StringComparison.OrdinalIgnoreCase);

        [CliGroupCommand(Group, "Run the Addressables Analyze rules (duplicate bundle dependencies, duplicate scene and Resources dependencies, the bundle layout; --rule narrows to one by part of its name) " +
                                "and return each result with its severity. Nothing is fixed or changed: the render pipeline assets that the bundle rules' content build rewrites (URP's shader prefiltering) are put back after. Exit 6 when a rule reports errors or fails to run. A --rule no rule's name contains exits 13 with the rule names; " +
                                "--limit below 1 exits 2; no settings, or an open scene with unsaved changes (the rules would stop at Unity's save dialog), exits 6.",
            Name = "analyze", Tags = new[] { "cliext", "addressables" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Analyze(CommandContext cmd, [CliArg("rule", "Only rules whose name contains this")] string rule = "", [CliArg("limit", "Results per rule")] int limit = 100)
        {
            if (limit < 1) { cmd.Fail($"--limit {limit}: give 1 or more."); return ExitCode.InvalidUsage; }
            if (!TryGetSettings(cmd, out var s)) return ExitCode.Failed;
            var rules = Rules();
            var run = rules.Where(r => rule == "" || r.ruleName.IndexOf(rule, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (run.Count == 0)
            {
                cmd.Fail($"No Analyze rule's name contains '{rule}'.{AnimationErrors.Hint(rule, rules.Select(r => r.ruleName), "Rules", "cliext.addressables analyze")}");
                return ExitCode.NotFound;
            }
            if (RefuseDirtyScenes(cmd, "the Analyze rules")) return ExitCode.Failed;
            int errors = 0;
            var failed = new List<string>();
            // Analyze changes nothing: the pipeline assets' bytes are put back (and reimported) after the rules.
            var pipeline = PipelineAssetPaths().Where(File.Exists).ToDictionary(p => p, File.ReadAllBytes);
            try
            {
                foreach (var r in run)
                {
                    List<AnalyzeRule.AnalyzeResult> found;
                    try { found = r.RefreshAnalysis(s); }
                    catch (Exception e) { cmd.Result.WriteRecord(new { rule = r.ruleName, error = AnimationErrors.Message(e) }); failed.Add(r.ruleName); continue; }
                    finally { try { r.ClearAnalysis(); } catch { } }
                    var real = found.Where(x => !IsNoIssues(x)).ToList();
                    errors += real.Count(x => x.severity == MessageType.Error);
                    cmd.Result.WriteRecord(new
                    {
                        rule = r.ruleName,
                        issues = real.Count,
                        results = real.Take(limit).Select(x => new { severity = x.severity.ToString(), name = x.resultName.Replace(AnalyzeRule.kDelimiter.ToString(), " > ") }).ToArray()
                    });
                }
            }
            finally
            {
                foreach (var (p, bytes) in pipeline)
                {
                    if (File.ReadAllBytes(p).AsSpan().SequenceEqual(bytes)) continue;
                    File.WriteAllBytes(p, bytes);
                    AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
            if (failed.Count > 0) { cmd.Fail($"{failed.Count} rule(s) failed to run: {string.Join(", ", failed)} (their records say why)."); return ExitCode.Failed; }
            if (errors > 0) { cmd.Fail($"{errors} error result(s): see the records' results."); return ExitCode.Failed; }
            return ExitCode.Success;
        }
    }
}

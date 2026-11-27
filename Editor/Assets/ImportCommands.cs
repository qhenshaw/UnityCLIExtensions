using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEditor.Presets;
using UnityEngine;

namespace UnityCLIExtensions.Assets
{
    /// <summary>
    /// <c>cliext.import</c> and <c>cliext.presets</c>: import settings across many assets at once (Pipeline's asset
    /// set_import_settings changes one), and default presets per folder with an audit of the assets that break them.
    /// </summary>
    /// <remarks>
    /// Every argument, and every setting on every matching asset, is resolved before anything changes. A folder, asset or
    /// preset that isn't there is not found (exit 13) and says how to find or make one; a path outside Assets/ or
    /// Packages/, a wrong asset type, an unknown setting or enum name and a bad value are invalid usage (exit 2), with
    /// "Did you mean" and the valid names; a change that would change nothing exits 6. A batch is all or nothing: when
    /// applying fails on one asset, the .meta files of the assets it already changed are written back and reimported.
    /// </remarks>
    internal static class ImportCommands
    {
        private const string FindHint = "asset find --search-in <folder> lists what is there";

        private static ExitCode Report(CommandContext cmd, Exception e)
        {
            cmd.Fail(AnimationErrors.Message(e));
            return AnimationErrors.Exit(e);
        }

        private static bool IsArgError(Exception e) => e is ArgumentException || e is FormatException || e is OverflowException;

        // ---------------------------------------------------------------------------------------------------------
        // Paths

        /// <summary>A project path (Assets/... or Packages/...), normalized; anything else is invalid usage.</summary>
        internal static string ProjectPath(string path, string arg)
        {
            string p = AnimationErrors.AssetPath(path).TrimEnd('/');
            if (p.Length == 0) throw new AnimUsageException($"{arg}: give a project path such as Assets/Art/Textures.");
            if (!(p == "Assets" || p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal)))
                throw new AnimUsageException($"{arg} '{path}' isn't under Assets/ or Packages/: give a project path such as Assets/Art/Textures.");
            return p;
        }

        /// <summary>A folder that exists: outside Assets/ or Packages/ or a file is invalid usage, a missing one is not found.</summary>
        internal static string Folder(string folder, string arg = "--folder")
        {
            string f = ProjectPath(folder, arg);
            if (AssetDatabase.IsValidFolder(f)) return f;
            if (AssetDatabase.LoadMainAssetAtPath(f) != null)
                throw new AnimUsageException($"{arg} '{f}' is a file, not a folder: give the folder that holds it ({Path.GetDirectoryName(f)?.Replace('\\', '/')}), with --filter {Path.GetFileName(f)} to pick it.");
            throw new AnimNotFoundException($"{arg}: no folder '{f}'. Check the path ({FindHint}), or make it with asset create_folder.");
        }

        private static readonly string[] CommonTypes = { "Texture2D", "Sprite", "Model", "AudioClip", "Material", "Prefab", "Shader", "VideoClip", "Font", "TextAsset" };

        /// <summary>An asset type name for FindAssets' t: filter (a UnityEngine.Object type, Model or Prefab); empty for any.</summary>
        internal static string AssetType(string type)
        {
            string t = (type ?? "").Trim();
            if (t.Length == 0) return "";
            var names = TypeCache.GetTypesDerivedFrom<UnityEngine.Object>().Select(x => x.Name).Concat(new[] { "Object", "Model", "Prefab" }).Distinct().ToList();
            string hit = names.FirstOrDefault(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var close = AnimationErrors.Close(t, names);
            throw new AnimUsageException($"--type: no asset type '{t}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Common types: {string.Join(", ", CommonTypes)}.");
        }

        private static IEnumerable<string> AssetsIn(string folder, string type, string filter)
        {
            string t = string.IsNullOrWhiteSpace(type) ? "" : $"t:{type}";
            var paths = AssetDatabase.FindAssets(t, new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var pattern = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(filter.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                paths = paths.Where(p => pattern.IsMatch(Path.GetFileName(p)));
            }
            return paths.Where(p => !AssetDatabase.IsValidFolder(p)).OrderBy(p => p, StringComparer.Ordinal);
        }

        private static string Matching(string folder, string type, string filter) =>
            $"under '{folder}'{(string.IsNullOrEmpty(type) ? "" : $" of --type {type}")}{(string.IsNullOrWhiteSpace(filter) ? "" : $" matching --filter {filter.Trim()}")}";

        // ---------------------------------------------------------------------------------------------------------
        // Settings by name

        private static readonly string[] Hidden = { "name", "hideFlags" };

        private static bool Parsable(Type t) => t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Color);

        private static bool Obsolete(MemberInfo m) => m.GetCustomAttribute<ObsoleteAttribute>() != null;

        /// <summary>The settings a batch can set on <paramref name="t"/>: public writable properties and fields of simple types, by name.</summary>
        internal static SortedDictionary<string, MemberInfo> Settable(Type t)
        {
            var result = new SortedDictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (p.CanWrite && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0 && !Obsolete(p) && Parsable(p.PropertyType) && !Hidden.Contains(p.Name))
                    result[p.Name] = p;
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (!f.IsInitOnly && !Obsolete(f) && Parsable(f.FieldType) && !Hidden.Contains(f.Name) && !result.ContainsKey(f.Name))
                    result[f.Name] = f;
            return result;
        }

        private static Type TypeOf(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
        private static object Get(MemberInfo m, object target) => m is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)m).GetValue(target);
        private static void Set(MemberInfo m, object target, object value) { if (m is PropertyInfo p) p.SetValue(target, value); else ((FieldInfo)m).SetValue(target, value); }

        /// <summary>"name=value;..." as pairs; a pair without a name and '=' is invalid usage.</summary>
        internal static List<KeyValuePair<string, string>> Pairs(string set)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (string pair in (set ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.Trim().Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0 || pair.Substring(0, eq).Trim().Length == 0) throw new AnimUsageException($"--set: '{pair.Trim()}' isn't name=value (e.g. maxTextureSize=1024).");
                pairs.Add(new KeyValuePair<string, string>(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim()));
            }
            if (pairs.Count == 0) throw new AnimUsageException("--set: give name=value pairs, e.g. \"maxTextureSize=1024;mipmapEnabled=false\".");
            var twice = pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (twice != null) throw new AnimUsageException($"--set names {twice.Key} twice: give each setting once.");
            return pairs;
        }

        /// <summary>The setting named <paramref name="name"/> on <paramref name="t"/>, or invalid usage naming the close and the valid ones.</summary>
        internal static MemberInfo Setting(Type t, string name)
        {
            var all = Settable(t);
            if (all.TryGetValue(name, out var m)) return m;
            var close = AnimationErrors.Close(name, all.Keys);
            throw new AnimUsageException($"--set: {t.Name} has no setting '{name}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Settings: {string.Join(", ", all.Keys)}.");
        }

        private static readonly int[] TextureSizes = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };

        /// <summary>
        /// <paramref name="text"/> as a value of the setting: a one-line invalid usage naming the setting and the form,
        /// and for an enum its names with "Did you mean".
        /// </summary>
        internal static object Value(MemberInfo member, string text)
        {
            Type type = TypeOf(member);
            string name = member.Name, t = (text ?? "").Trim();
            object value;
            if (type.IsEnum && !type.IsDefined(typeof(FlagsAttribute), false))
            {
                var valid = Enum.GetNames(type).Where(n => !Obsolete(type.GetField(n))).ToList();
                string hit = Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
                if (hit != null) value = Enum.Parse(type, hit);
                else if (long.TryParse(t, out long n) && Enum.IsDefined(type, Enum.ToObject(type, n))) value = Enum.ToObject(type, n);
                else
                {
                    var close = AnimationErrors.Close(t, valid);
                    throw new AnimUsageException($"{name}: '{t}' isn't a {type.Name}.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Use one of: {string.Join(", ", valid)}.");
                }
            }
            else
            {
                try { value = ValueText.Parse(text, type); }
                catch (ArgumentException e) { throw new AnimUsageException($"{name}: {e.Message}"); }
            }
            if (string.Equals(name, "maxTextureSize", StringComparison.Ordinal) && value is int size && !TextureSizes.Contains(size))
                throw new AnimUsageException($"maxTextureSize: {size} isn't a texture size: give one of {string.Join(", ", TextureSizes)}.");
            if (string.Equals(name, "compressionQuality", StringComparison.Ordinal) && value is int q && (q < 0 || q > 100))
                throw new AnimUsageException($"compressionQuality: {q} is out of range: give 0 to 100.");
            return value;
        }

        // Texture platform names TextureImporter.GetPlatformTextureSettings takes (iOS is stored as iPhone).
        private static readonly string[] Platforms = { "Default", "Standalone", "Android", "iPhone", "WebGL", "tvOS", "VisionOS", "Windows Store Apps", "PS4", "PS5", "XboxOne", "GameCoreScarlett", "Nintendo Switch", "Server" };

        /// <summary>A texture platform name, or null for none or Default; anything else is invalid usage naming the platforms.</summary>
        internal static string Platform(string platform)
        {
            string p = (platform ?? "").Trim();
            if (p.Length == 0 || string.Equals(p, "Default", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(p, "iOS", StringComparison.OrdinalIgnoreCase)) return "iPhone";
            string hit = Platforms.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var close = AnimationErrors.Close(p, Platforms.Concat(new[] { "iOS" }));
            throw new AnimUsageException($"--platform: no texture platform '{p}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Platforms: {string.Join(", ", Platforms.Select(n => n == "iPhone" ? "iOS" : n))}.");
        }

        /// <summary>One asset's planned change: the target object (importer or platform settings) and the values to set.</summary>
        private sealed class Plan
        {
            public string path;
            public AssetImporter importer;
            public TextureImporterPlatformSettings platform;
            public readonly List<(MemberInfo member, object value)> sets = new List<(MemberInfo, object)>();
            public readonly List<string> changes = new List<string>();
        }

        /// <summary>
        /// Checks every setting on every asset and returns what would change, without changing anything. An asset whose
        /// importer lacks a setting, or a bad value, is invalid usage naming the asset (so a mixed folder fails as a whole).
        /// </summary>
        private static List<Plan> PlanBatch(List<string> paths, List<KeyValuePair<string, string>> pairs, string platform, string folder, out int noImporter)
        {
            noImporter = 0;
            var importers = new List<(string path, AssetImporter importer)>();
            foreach (string p in paths)
            {
                var importer = AssetImporter.GetAtPath(p);
                if (importer == null) { noImporter++; continue; }
                importers.Add((p, importer));
            }
            if (importers.Count == 0) throw new AnimNotFoundException($"No asset under '{folder}' has an importer: there is nothing to set.");
            var byType = importers.GroupBy(i => i.importer.GetType()).OrderByDescending(g => g.Count()).ToList();
            if (platform != null)
            {
                var other = byType.FirstOrDefault(g => !typeof(TextureImporter).IsAssignableFrom(g.Key));
                if (other != null)
                    throw new AnimUsageException($"--platform overrides are for textures, and '{other.First().path}' is {AnimationErrors.A(AssetDatabase.GetMainAssetTypeAtPath(other.First().path)?.Name ?? other.Key.Name)} ({other.Key.Name}): add --type Texture2D (or --filter) to leave the others out.");
            }
            Type TargetType(Type importerType) => platform != null ? typeof(TextureImporterPlatformSettings) : importerType;

            // Each setting must exist on every importer type in the batch.
            var members = new Dictionary<(Type, string), MemberInfo>();
            foreach (var pair in pairs)
            {
                var have = byType.Where(g => Settable(TargetType(g.Key)).ContainsKey(pair.Key)).ToList();
                if (have.Count == 0) Setting(TargetType(byType[0].Key), pair.Key); // throws with the names
                var lacking = byType.Where(g => !have.Contains(g)).ToList();
                if (lacking.Count > 0)
                {
                    var g0 = lacking[0];
                    string kind = AssetDatabase.GetMainAssetTypeAtPath(have[0].First().path)?.Name ?? have[0].Key.Name;
                    string lackKind = AssetDatabase.GetMainAssetTypeAtPath(g0.First().path)?.Name ?? g0.Key.Name;
                    throw new AnimUsageException($"'{g0.First().path}' ({AnimationErrors.A(lackKind)}{(g0.Count() > 1 ? $", and {g0.Count() - 1} more with {AnimationErrors.A(g0.Key.Name)}" : "")}) has no setting '{pair.Key}', which the {have[0].Key.Name} assets in the batch have: " +
                                                 $"narrow the batch with --type {(have[0].Key == typeof(ModelImporter) ? "Model" : kind)} or --filter. Nothing was changed.");
                }
                foreach (var g in byType) members[(g.Key, pair.Key)] = Setting(TargetType(g.Key), pair.Key);
            }

            var values = new Dictionary<MemberInfo, object>();
            var plans = new List<Plan>();
            foreach (var (path, importer) in importers)
            {
                var plan = new Plan { path = path, importer = importer };
                object target = importer;
                if (platform != null)
                {
                    plan.platform = ((TextureImporter)importer).GetPlatformTextureSettings(platform);
                    target = plan.platform;
                }
                bool setsOverridden = false;
                foreach (var pair in pairs)
                {
                    var member = members[(importer.GetType(), pair.Key)];
                    if (!values.TryGetValue(member, out object value)) values[member] = value = Value(member, pair.Value);
                    if (member.Name == "overridden") setsOverridden = true;
                    object current = Get(member, target);
                    if (Equals(current, value) || ValueText.Format(current) == ValueText.Format(value)) continue;
                    plan.sets.Add((member, value));
                    plan.changes.Add($"{(platform != null ? platform + " " : "")}{member.Name}: {ValueText.Format(current)} -> {ValueText.Format(value)}");
                }
                if (plan.platform != null && !plan.platform.overridden && !setsOverridden)
                {
                    plan.sets.Add((typeof(TextureImporterPlatformSettings).GetProperty("overridden"), true));
                    plan.changes.Add($"{platform} overridden: false -> true");
                }
                if (plan.sets.Count > 0) plans.Add(plan);
            }
            return plans;
        }

        [CliGroupCommand("cliext.import", "Set import settings on every matching asset under --folder (--type Texture2D, Model, AudioClip, ...; --filter *.png), only where they differ, and reimport those. " +
                                          "--set \"maxTextureSize=1024;mipmapEnabled=false\" takes the importer's own property names (TextureImporter, ModelImporter: globalScale, meshCompression, materialImportMode, animationType, ...). " +
                                          "--platform Standalone|Android|iOS|WebGL|... sets a texture platform override (the platform's maxTextureSize, format, compressionQuality, ...). --dry-run true lists the changes only. " +
                                          "All or nothing: every setting is checked on every matching asset first, so an unknown setting or a bad value, or an asset whose importer lacks the setting (narrow with --type or --filter), changes nothing; " +
                                          "if applying fails part-way, the assets already changed get their .meta files back. Exit 13 when the folder is missing or nothing matches, 2 for a bad setting, value, type, platform or a path outside Assets/ or Packages/, 6 when nothing would change.",
            Name = "batch", Tags = new[] { "cliext", "assets", "import" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Batch(
            CommandContext cmd,
            [CliArg("folder", "Folder to search, subfolders included (Assets/...)", Required = true)] string folder,
            [CliArg("set", "Settings: name=value;name=value", Required = true)] string set,
            [CliArg("type", "Asset type filter (Texture2D, Model, AudioClip, ...)")] string type = "",
            [CliArg("filter", "File name filter (*.png, Hero_*)")] string filter = "",
            [CliArg("platform", "Texture platform override (Standalone, Android, iOS, WebGL, ...)")] string platform = "",
            [CliArg("dry-run", "List changes without applying them")] bool dryRun = false)
        {
            try
            {
                string f = Folder(folder);
                string t = AssetType(type);
                string plat = Platform(platform);
                var pairs = Pairs(set);
                var paths = AssetsIn(f, t, filter).ToList();
                if (paths.Count == 0) throw new AnimNotFoundException($"No assets {Matching(f, t, filter)}: nothing to set. Check --type and --filter ({FindHint}).");
                var plans = PlanBatch(paths, pairs, plat, f, out int noImporter);
                if (plans.Count == 0)
                {
                    cmd.Fail($"All {paths.Count - noImporter} matching asset(s) {Matching(f, t, filter)} already have {string.Join(";", pairs.Select(p => p.Key + "=" + p.Value))}: nothing would change.");
                    return ExitCode.Failed;
                }
                if (dryRun)
                {
                    foreach (var plan in plans) cmd.Result.WriteRecord(new { path = plan.path, changes = plan.changes });
                    cmd.Log.Info($"Would change {plans.Count} of {paths.Count} asset(s); nothing was changed.");
                    return ExitCode.Success;
                }
                ApplyAll(plans.Select(p => p.path), () =>
                {
                    foreach (var plan in plans)
                    {
                        object target = (object)plan.platform ?? plan.importer;
                        foreach (var (member, value) in plan.sets) Set(member, target, value);
                        if (plan.platform != null) ((TextureImporter)plan.importer).SetPlatformTextureSettings(plan.platform);
                        EditorUtility.SetDirty(plan.importer);
                        plan.importer.SaveAndReimport();
                    }
                }, () =>
                {
                    // Unity can refuse a value the type allows; then the batch is put back as a whole.
                    foreach (var plan in plans)
                    {
                        var importer = AssetImporter.GetAtPath(plan.path);
                        object target = plan.platform != null ? (object)((TextureImporter)importer).GetPlatformTextureSettings(plan.platform.name) : importer;
                        foreach (var (member, value) in plan.sets)
                        {
                            object now = Get(member, target);
                            if (!Equals(now, value) && ValueText.Format(now) != ValueText.Format(value))
                                return $"Unity didn't keep {member.Name} = {ValueText.Format(value)} on '{plan.path}' (it is {ValueText.Format(now)}; is its .meta file locked or read-only?)";
                        }
                    }
                    return null;
                });
                foreach (var plan in plans) cmd.Result.WriteRecord(new { path = plan.path, changes = plan.changes });
                cmd.Log.Info($"Changed {plans.Count} of {paths.Count} asset(s).");
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
            catch (BatchFailedException e) { cmd.Fail(e.Message); return ExitCode.Failed; }
        }

        private sealed class BatchFailedException : Exception
        {
            public BatchFailedException(string message) : base(message) { }
        }

        /// <summary>
        /// Runs <paramref name="apply"/> on <paramref name="paths"/> and then <paramref name="verify"/> (null when every
        /// change held). When either fails, writes back every asset's original .meta, reimports it and throws, so the
        /// batch leaves nothing half-changed.
        /// </summary>
        private static void ApplyAll(IEnumerable<string> paths, Action apply, Func<string> verify)
        {
            var saved = new Dictionary<string, byte[]>();
            foreach (string p in paths)
            {
                try { saved[p] = File.ReadAllBytes(MetaFile(p)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                { throw new BatchFailedException($"Can't read '{p}.meta' to back it up ({e.Message}); nothing was changed."); }
            }
            string failure = null;
            try
            {
                AssetDatabase.StartAssetEditing();
                try { apply(); }
                finally { AssetDatabase.StopAssetEditing(); }
                failure = verify();
            }
            catch (Exception e) { failure = AnimationErrors.Message(e); }
            if (failure == null) return;
            foreach (var kv in saved)
            {
                try { File.WriteAllBytes(MetaFile(kv.Key), kv.Value); } catch (IOException) { }
            }
            AssetDatabase.StartAssetEditing();
            try { foreach (string p in saved.Keys) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate); }
            finally { AssetDatabase.StopAssetEditing(); }
            throw new BatchFailedException($"{failure}. The {saved.Count} asset(s) of the batch were put back as they were (their .meta files restored); nothing was changed.");
        }

        private static string MetaFile(string assetPath) => Path.GetFullPath(FileUtil.GetPhysicalPath(assetPath)) + ".meta";

        // ---------------------------------------------------------------------------------------------------------
        // Presets

        /// <summary>The preset at <paramref name="path"/>: a missing one is not found (how to make one), another asset type is invalid usage.</summary>
        internal static Preset LoadPreset(string path, string arg = "--preset")
        {
            string p = ProjectPath(path, arg);
            var main = AssetDatabase.LoadMainAssetAtPath(p);
            if (main == null) throw new AnimNotFoundException($"{arg}: no preset at '{p}'. Make one with cliext.presets create --from <asset> --out {(p.EndsWith(".preset") ? p : p + ".preset")}, or find one with asset find --type Preset.");
            if (!(main is Preset preset)) throw new AnimUsageException($"{arg} '{p}' is {AnimationErrors.A(main.GetType().Name)}, not a Preset: give a .preset (cliext.presets create makes one from an asset).");
            if (!preset.IsValid()) throw new AnimUsageException($"{arg} '{p}' is a preset of a type that no longer exists ({preset.GetTargetFullTypeName()}).");
            return preset;
        }

        private static string TypeName(PresetType t) => t.GetManagedTypeName() ?? t.ToString();

        [CliGroupCommand("cliext.presets", "The default presets (Project Settings > Preset Manager) per type, with each one's folder filter and whether it is enabled.",
            Name = "list", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            foreach (var type in Preset.GetAllDefaultTypes())
                foreach (var d in Preset.GetDefaultPresetsForType(type))
                    cmd.Result.WriteRecord(new { type = TypeName(type), preset = d.preset != null ? AssetDatabase.GetAssetPath(d.preset) : null, filter = d.filter, enabled = d.enabled });
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.presets", "Make a preset from an asset's importer (or any object asset) and save it at --out (.preset). " +
                                           "Exit 13 when --from or --out's folder is missing, 2 for a folder as --from or a path outside Assets/ or Packages/, 6 when --out already exists (pick another path, or asset delete it first).",
            Name = "create", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("from", "The asset whose import settings to capture", Required = true)] string from,
            [CliArg("out", "Where to save the .preset", Required = true)] string outPath)
        {
            try
            {
                string src = ProjectPath(from, "--from");
                if (AssetDatabase.IsValidFolder(src)) throw new AnimUsageException($"--from '{src}' is a folder: give an asset in it (a texture, model, audio clip, ...) whose import settings to capture.");
                if (AssetDatabase.LoadMainAssetAtPath(src) == null) throw new AnimNotFoundException($"--from: no asset at '{src}'. Check the path ({FindHint}).");
                string dst = ProjectPath(outPath, "--out");
                if (!dst.EndsWith(".preset", StringComparison.OrdinalIgnoreCase)) dst += ".preset";
                string dir = Path.GetDirectoryName(dst)?.Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(dir)) throw new AnimNotFoundException($"--out: no folder '{dir}'. Create it first (asset create_folder --path {dir}).");
                if (AssetDatabase.LoadMainAssetAtPath(dst) != null || File.Exists(dst))
                {
                    cmd.Fail($"--out '{dst}' already exists: pick another path, or delete it first (asset delete --asset {dst}) to replace it.");
                    return ExitCode.Failed;
                }
                UnityEngine.Object source = AssetImporter.GetAtPath(src);
                if (source == null || source.GetType() == typeof(AssetImporter)) source = AssetDatabase.LoadMainAssetAtPath(src);
                var preset = new Preset(source);
                if (!preset.IsValid()) throw new AnimUsageException($"--from '{src}': a preset can't be made from {AnimationErrors.A(source.GetType().Name)}.");
                AssetDatabase.CreateAsset(preset, dst);
                cmd.Result.Write(new { preset = dst, type = TypeName(preset.GetPresetType()), properties = preset.PropertyModifications.Length });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        /// <summary>
        /// The Preset Manager filter for --filter: empty for every asset, a search filter (with ':') as given, and a
        /// folder or asset path as a glob. A path with nothing there is not found.
        /// </summary>
        internal static string DefaultFilter(string filter)
        {
            string f = (filter ?? "").Trim();
            bool drive = System.Text.RegularExpressions.Regex.IsMatch(f, @"^[A-Za-z]:[\\/]");
            if (f.Length == 0 || (f.Contains(":") && !drive)) return f;
            string p = ProjectPath(f, "--filter");
            if (AssetDatabase.IsValidFolder(p)) return $"glob:\"{p}/**\"";
            if (AssetDatabase.LoadMainAssetAtPath(p) != null) return $"glob:\"{p}\"";
            throw new AnimNotFoundException($"--filter: no folder '{p}'. Create it (asset create_folder), or pass a search filter such as glob:\"Assets/Art/UI/**/*.png\".");
        }

        [CliGroupCommand("cliext.presets", "Make a preset a default for its type, for assets matching --filter (a folder such as Assets/Art/UI, or a search filter such as glob:\"Assets/Art/UI/**/*.png\"; empty for every asset). " +
                                           "New and reimported assets there get it; it goes first in its type's list. --remove true takes it out of the defaults (only the entry with --filter when one is given). " +
                                           "Exit 13 when the preset or the --filter folder is missing, 2 when --preset isn't a preset or its type can't have defaults, 6 when it is already the first default with that filter, or with --remove isn't a default.",
            Name = "set_default", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetDefault(
            CommandContext cmd,
            [CliArg("preset", "The .preset asset", Required = true)] string presetPath,
            [CliArg("filter", "Folder or search filter")] string filter = "",
            [CliArg("remove", "Remove it from the defaults instead")] bool remove = false)
        {
            try
            {
                var preset = LoadPreset(presetPath);
                var type = preset.GetPresetType();
                if (!type.IsValidDefault()) throw new AnimUsageException($"--preset '{AssetDatabase.GetAssetPath(preset)}' is a preset of {TypeName(type)}, which can't have default presets.");
                bool anyFilter = string.IsNullOrWhiteSpace(filter);
                string f = DefaultFilter(filter);
                var current = Preset.GetDefaultPresetsForType(type).ToList();
                bool Same(DefaultPreset d) => d.preset == preset && (remove && anyFilter || d.filter == f);
                string presetName = AssetDatabase.GetAssetPath(preset);
                var list = current.Where(d => !Same(d)).ToList();
                if (remove)
                {
                    if (list.Count == current.Count)
                    {
                        cmd.Fail($"'{presetName}' isn't a default preset{(anyFilter ? "" : $" with filter {f}")}: nothing would change. cliext.presets list lists the defaults.");
                        return ExitCode.Failed;
                    }
                }
                else
                {
                    if (current.Count > 0 && current[0].preset == preset && current[0].filter == f && current[0].enabled)
                    {
                        cmd.Fail($"'{presetName}' is already the first default for {TypeName(type)}{(f.Length > 0 ? $" with filter {f}" : "")}: nothing would change.");
                        return ExitCode.Failed;
                    }
                    list.Insert(0, new DefaultPreset(f, preset, true));
                }
                if (!Preset.SetDefaultPresetsForType(type, list.ToArray())) { cmd.Fail($"Unity refused the default presets for {TypeName(type)}; nothing was changed."); return ExitCode.Failed; }
                cmd.Result.Write(new { type = TypeName(type), defaults = list.Select(d => new { preset = AssetDatabase.GetAssetPath(d.preset), d.filter, d.enabled }).ToArray() });
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        [CliGroupCommand("cliext.presets", "Apply a preset to every matching asset under --folder that takes it (its importer, then reimport), or with --dry-run true list what would change. " +
                                           "Only the settings that differ are applied; per-asset data (sprite rects, user data, asset bundle) is left as it is. All or nothing: if one asset fails, the ones already changed get their .meta files back. " +
                                           "Exit 13 when the preset or folder is missing or no asset matches, 2 when no asset there takes the preset's type, 6 when nothing would change.",
            Name = "apply", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Apply(
            CommandContext cmd,
            [CliArg("preset", "The .preset asset", Required = true)] string presetPath,
            [CliArg("folder", "Folder whose assets get the preset, subfolders included", Required = true)] string folder,
            [CliArg("filter", "File name filter")] string filter = "",
            [CliArg("dry-run", "List changes only")] bool dryRun = false)
        {
            try
            {
                var preset = LoadPreset(presetPath);
                string f = Folder(folder);
                var paths = AssetsIn(f, "", filter).ToList();
                if (paths.Count == 0) throw new AnimNotFoundException($"No assets {Matching(f, "", filter)}: nothing to apply the preset to. Check --filter ({FindHint}).");
                var takers = new List<(string path, AssetImporter importer)>();
                var others = new SortedSet<string>();
                foreach (string p in paths)
                {
                    var importer = AssetImporter.GetAtPath(p);
                    if (importer != null && preset.CanBeAppliedTo(importer)) takers.Add((p, importer));
                    else others.Add(importer != null ? importer.GetType().Name : "no importer");
                }
                string presetName = AssetDatabase.GetAssetPath(preset);
                if (takers.Count == 0)
                    throw new AnimUsageException($"'{presetName}' is a preset of {TypeName(preset.GetPresetType())}, and no asset {Matching(f, "", filter)} takes it (they use {string.Join(", ", others)}): give a preset made from one of them (cliext.presets create).");
                var plans = takers.Select(t => (t.path, t.importer, diff: Diff(preset, t.importer))).Where(x => x.diff.Count > 0).ToList();
                if (plans.Count == 0)
                {
                    cmd.Fail($"All {takers.Count} asset(s) {Matching(f, "", filter)} that take '{presetName}' already match it: nothing would change.");
                    return ExitCode.Failed;
                }
                if (dryRun)
                {
                    foreach (var plan in plans) cmd.Result.WriteRecord(new { path = plan.path, differences = plan.diff.Select(d => d.text).ToArray() });
                    cmd.Log.Info($"Would change {plans.Count} of {takers.Count} asset(s) that take the preset; nothing was changed.");
                    return ExitCode.Success;
                }
                ApplyAll(plans.Select(p => p.path), () =>
                {
                    foreach (var plan in plans)
                    {
                        if (!preset.ApplyTo(plan.importer, plan.diff.Select(d => d.property).ToArray()))
                            throw new InvalidOperationException($"Unity didn't apply '{presetName}' to '{plan.path}'");
                        plan.importer.SaveAndReimport();
                    }
                }, () =>
                {
                    foreach (var plan in plans)
                    {
                        var left = Diff(preset, AssetImporter.GetAtPath(plan.path));
                        if (left.Count > 0) return $"'{plan.path}' still differs from the preset after applying it ({left[0].text})";
                    }
                    return null;
                });
                foreach (var plan in plans) cmd.Result.WriteRecord(new { path = plan.path, differences = plan.diff.Select(d => d.text).ToArray() });
                cmd.Log.Info($"Changed {plans.Count} of {takers.Count} asset(s) that take the preset.");
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
            catch (BatchFailedException e) { cmd.Fail(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand("cliext.presets", "Audit: for every asset under --folder whose type has default presets, the settings where its importer breaks the preset that applies to it (by the presets' folder filters). Nothing is changed. " +
                                           "Exit 6 when any asset breaks a preset, 13 when the folder is missing, 2 for a path outside Assets/ or Packages/ or a --limit under 1.",
            Name = "audit", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Audit(
            CommandContext cmd,
            [CliArg("folder", "Folder to audit")] string folder = "Assets",
            [CliArg("limit", "Differences listed per asset")] int limit = 12)
        {
            try
            {
                string f = Folder(folder);
                if (limit < 1) throw new AnimUsageException($"--limit {limit}: give 1 or more differences to list per asset.");
                int breaking = 0, checkedCount = 0;
                foreach (string p in AssetsIn(f, "", ""))
                {
                    var importer = AssetImporter.GetAtPath(p);
                    if (importer == null) continue;
                    var presets = Preset.GetDefaultPresetsForObject(importer);
                    if (presets == null || presets.Length == 0) continue;
                    checkedCount++;
                    // Several defaults can match; the Preset Manager applies them in order, so the last one wins a property.
                    var diff = presets.SelectMany(pr => Diff(pr, importer).Select(d => $"{d.text} ({AssetDatabase.GetAssetPath(pr)})")).ToList();
                    if (diff.Count == 0) continue;
                    breaking++;
                    cmd.Result.WriteRecord(new { path = p, differences = diff.Take(limit).ToArray(), more = diff.Count > limit ? diff.Count - limit : (int?)null });
                }
                string summary = $"{breaking} of {checkedCount} asset(s) under '{f}' with default presets break them.";
                if (breaking > 0) { cmd.Fail(summary + " Fix them with cliext.presets apply (or reimport them)."); return ExitCode.Failed; }
                cmd.Log.Info(checkedCount == 0 ? $"No asset under '{f}' has a default preset (cliext.presets list lists them)." : summary);
                return ExitCode.Success;
            }
            catch (Exception e) when (IsArgError(e)) { return Report(cmd, e); }
        }

        /// <summary>Properties where <paramref name="target"/> differs from <paramref name="preset"/>, with the text "path: is X, preset Y".</summary>
        internal static List<(string property, string text)> Diff(Preset preset, UnityEngine.Object target)
        {
            var current = new Dictionary<string, string>();
            foreach (var m in new Preset(target).PropertyModifications)
                current[m.propertyPath] = m.objectReference != null ? AssetDatabase.GetAssetPath(m.objectReference) : m.value;
            var excluded = new HashSet<string>(preset.excludedProperties ?? Array.Empty<string>());
            var diff = new List<(string, string)>();
            foreach (var m in preset.PropertyModifications)
            {
                if (excluded.Any(e => m.propertyPath == e || m.propertyPath.StartsWith(e + "."))) continue;
                if (PerAsset(m.propertyPath)) continue;
                string want = m.objectReference != null ? AssetDatabase.GetAssetPath(m.objectReference) : m.value;
                if (!current.TryGetValue(m.propertyPath, out string have) || have != want) diff.Add((m.propertyPath, $"{m.propertyPath}: is {have ?? "(missing)"}, preset {want}"));
            }
            return diff;
        }

        /// <summary>Per-asset data that a preset can't sensibly fix (and apply leaves alone).</summary>
        internal static bool PerAsset(string propertyPath) =>
            propertyPath.StartsWith("m_FileIDToRecycleName") || propertyPath.StartsWith("m_InternalIDToNameTable") || propertyPath.StartsWith("m_UsedFileIDs") ||
            propertyPath.StartsWith("m_SpriteSheet") || propertyPath.StartsWith("m_UserData") || propertyPath.StartsWith("m_AssetBundle") || propertyPath.Contains("m_ExternalObjects");
    }
}

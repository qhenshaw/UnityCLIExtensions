using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Presets;
using UnityEngine;

namespace UnityCLIExtensions.Assets
{
    /// <summary>
    /// <c>cliext.import</c> and <c>cliext.presets</c>: import settings across many assets at once (Pipeline's asset
    /// set_import_settings changes one), and default presets per folder with an audit of the assets that break them.
    /// </summary>
    internal static class ImportCommands
    {
        private static IEnumerable<string> AssetsIn(string folder, string type, string filter)
        {
            folder = (folder ?? "Assets").Trim().TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(folder)) throw new ArgumentException($"No folder '{folder}'.");
            string t = string.IsNullOrWhiteSpace(type) ? "" : $"t:{type}";
            var paths = AssetDatabase.FindAssets(t, new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var pattern = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                paths = paths.Where(p => pattern.IsMatch(Path.GetFileName(p)));
            }
            return paths.Where(p => !AssetDatabase.IsValidFolder(p));
        }

        /// <summary>Applies name=value pairs to an importer (or a platform's settings) and returns what changed as "name: old -> new".</summary>
        private static List<string> SetOn(AssetImporter importer, string pairs, string platform, bool apply)
        {
            var changes = new List<string>();
            object target = importer;
            TextureImporterPlatformSettings platformSettings = null;
            if (!string.IsNullOrWhiteSpace(platform) && platform != "Default")
            {
                if (!(importer is TextureImporter ti)) throw new ArgumentException("Per-platform settings here are for textures.");
                platformSettings = ti.GetPlatformTextureSettings(platform);
                target = platformSettings;
            }
            foreach (string pair in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{pair}' isn't name=value.");
                string name = pair.Substring(0, eq).Trim(), text = pair.Substring(eq + 1).Trim();
                var type = target.GetType();
                var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                var field = prop == null ? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase) : null;
                if ((prop == null || !prop.CanWrite) && field == null)
                    throw new ArgumentException($"{type.Name} has no settable '{name}'.");
                Type valueType = prop != null ? prop.PropertyType : field.FieldType;
                object current = prop != null ? prop.GetValue(target) : field.GetValue(target);
                object value = ValueText.Parse(text, valueType);
                if (Equals(current, value) || ValueText.Format(current) == ValueText.Format(value)) continue;
                changes.Add($"{name}: {ValueText.Format(current)} -> {ValueText.Format(value)}");
                if (!apply) continue;
                if (prop != null) prop.SetValue(target, value); else field.SetValue(target, value);
            }
            if (apply && platformSettings != null && changes.Count > 0)
            {
                platformSettings.overridden = true;
                ((TextureImporter)importer).SetPlatformTextureSettings(platformSettings);
            }
            return changes;
        }

        [CliGroupCommand("cliext.import", "Set import settings on every matching asset under --folder (--type Texture2D, Model, AudioClip, ...; --filter *.png), only where they differ, and reimport those. " +
                                          "--set \"maxTextureSize=1024;mipmapEnabled=false\" takes the importer's own property names (TextureImporter, ModelImporter: globalScale, meshCompression, materialImportMode, animationType, ...). " +
                                          "--platform Android|iOS|Standalone|WebGL sets a texture platform override. --dry-run true lists the changes only.",
            Name = "batch", Tags = new[] { "cliext", "assets", "import" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Batch(
            CommandContext cmd,
            [CliArg("folder", "Folder to search", Required = true)] string folder,
            [CliArg("set", "Settings: name=value;name=value", Required = true)] string set,
            [CliArg("type", "Asset type filter (Texture2D, Model, AudioClip, ...)")] string type = "",
            [CliArg("filter", "File name filter (*.png, Hero_*)")] string filter = "",
            [CliArg("platform", "Texture platform override")] string platform = "",
            [CliArg("dry-run", "List changes without applying them")] bool dryRun = false)
        {
            try
            {
                var paths = AssetsIn(folder, type, filter).ToList();
                int changed = 0, skipped = 0;
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (string p in paths)
                    {
                        var importer = AssetImporter.GetAtPath(p);
                        if (importer == null) { skipped++; continue; }
                        List<string> changes;
                        try { changes = SetOn(importer, set, platform, !dryRun); }
                        catch (ArgumentException e) { cmd.Result.WriteRecord(new { path = p, skipped = e.Message }); skipped++; continue; }
                        if (changes.Count == 0) continue;
                        changed++;
                        cmd.Result.WriteRecord(new { path = p, changes });
                        if (!dryRun) { EditorUtility.SetDirty(importer); importer.SaveAndReimport(); }
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                cmd.Log.Info($"{(dryRun ? "Would change" : "Changed")} {changed} of {paths.Count} asset(s); {skipped} skipped.");
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Presets

        [CliGroupCommand("cliext.presets", "The default presets (Project Settings > Preset Manager) per type, with each one's folder filter and whether it is enabled.",
            Name = "list", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            foreach (var type in Preset.GetAllDefaultTypes())
                foreach (var d in Preset.GetDefaultPresetsForType(type))
                    cmd.Result.WriteRecord(new { type = type.GetManagedTypeName() ?? type.ToString(), preset = d.preset != null ? AssetDatabase.GetAssetPath(d.preset) : null, filter = d.filter, enabled = d.enabled });
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.presets", "Make a preset from an asset's importer (or any object asset) and save it at --out (.preset).",
            Name = "create", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("from", "The asset whose import settings to capture", Required = true)] string from,
            [CliArg("out", "Where to save the .preset", Required = true)] string outPath)
        {
            UnityEngine.Object source = AssetImporter.GetAtPath(from);
            if (source == null || source.GetType() == typeof(AssetImporter)) source = AssetDatabase.LoadMainAssetAtPath(from);
            if (source == null) { cmd.Log.Error($"No asset at '{from}'."); return ExitCode.NotFound; }
            var preset = new Preset(source);
            if (!outPath.EndsWith(".preset")) outPath += ".preset";
            AssetDatabase.CreateAsset(preset, outPath);
            cmd.Result.Write(new { preset = outPath, type = preset.GetPresetType().GetManagedTypeName(), properties = preset.PropertyModifications.Length });
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.presets", "Make a preset a default for its type, for assets matching --filter (a folder such as Assets/Art/UI, or a search filter such as glob:\"Assets/Art/UI/**/*.png\"). " +
                                           "New and reimported assets there get it. --remove true takes it out of the defaults.",
            Name = "set_default", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode SetDefault(
            CommandContext cmd,
            [CliArg("preset", "The .preset asset", Required = true)] string presetPath,
            [CliArg("filter", "Folder or search filter")] string filter = "",
            [CliArg("remove", "Remove it from the defaults instead")] bool remove = false)
        {
            var preset = AssetDatabase.LoadAssetAtPath<Preset>(presetPath);
            if (preset == null) { cmd.Log.Error($"No preset at '{presetPath}'."); return ExitCode.NotFound; }
            var type = preset.GetPresetType();
            var list = Preset.GetDefaultPresetsForType(type).Where(d => d.preset != preset).ToList();
            string f = (filter ?? "").Trim();
            if (f.Length > 0 && !f.Contains(":") && AssetDatabase.IsValidFolder(f.TrimEnd('/'))) f = $"glob:\"{f.TrimEnd('/')}/**\"";
            if (!remove) list.Insert(0, new DefaultPreset(f, preset, true));
            if (!Preset.SetDefaultPresetsForType(type, list.ToArray())) { cmd.Log.Error("Unity refused the default presets."); return ExitCode.Failed; }
            cmd.Result.Write(new { type = type.GetManagedTypeName(), defaults = list.Select(d => new { preset = AssetDatabase.GetAssetPath(d.preset), d.filter, d.enabled }).ToArray() });
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.presets", "Apply a preset to every matching asset under --folder (its importer, then reimport), or with --dry-run true list what would change.",
            Name = "apply", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Apply(
            CommandContext cmd,
            [CliArg("preset", "The .preset asset", Required = true)] string presetPath,
            [CliArg("folder", "Folder", Required = true)] string folder,
            [CliArg("filter", "File name filter")] string filter = "",
            [CliArg("dry-run", "List changes only")] bool dryRun = false)
        {
            var preset = AssetDatabase.LoadAssetAtPath<Preset>(presetPath);
            if (preset == null) { cmd.Log.Error($"No preset at '{presetPath}'."); return ExitCode.NotFound; }
            try
            {
                int changed = 0;
                foreach (string p in AssetsIn(folder, "", filter))
                {
                    var importer = AssetImporter.GetAtPath(p);
                    if (importer == null || !preset.CanBeAppliedTo(importer)) continue;
                    var diff = Diff(preset, importer);
                    if (diff.Count == 0) continue;
                    changed++;
                    cmd.Result.WriteRecord(new { path = p, differences = diff });
                    if (!dryRun && preset.ApplyTo(importer)) importer.SaveAndReimport();
                }
                cmd.Log.Info($"{(dryRun ? "Would change" : "Changed")} {changed} asset(s).");
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand("cliext.presets", "Audit: for every asset under --folder whose type has default presets, the settings where its importer breaks the preset that applies to it (by the presets' folder filters). Nothing is changed. Exit 6 when any asset breaks a preset.",
            Name = "audit", Tags = new[] { "cliext", "assets", "presets" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Audit(
            CommandContext cmd,
            [CliArg("folder", "Folder to audit")] string folder = "Assets",
            [CliArg("limit", "Differences listed per asset")] int limit = 12)
        {
            try
            {
                int breaking = 0, checkedCount = 0;
                foreach (string p in AssetsIn(folder, "", ""))
                {
                    var importer = AssetImporter.GetAtPath(p);
                    if (importer == null) continue;
                    var presets = Preset.GetDefaultPresetsForObject(importer);
                    if (presets == null || presets.Length == 0) continue;
                    checkedCount++;
                    // Several defaults can match; the Preset Manager applies them in order, so the last one wins a property.
                    var diff = presets.SelectMany(pr => Diff(pr, importer).Select(d => $"{d} ({AssetDatabase.GetAssetPath(pr)})")).ToList();
                    if (diff.Count == 0) continue;
                    breaking++;
                    cmd.Result.WriteRecord(new { path = p, differences = diff.Take(limit).ToArray(), more = diff.Count > limit ? diff.Count - limit : (int?)null });
                }
                cmd.Log.Info($"{breaking} of {checkedCount} asset(s) with default presets break them.");
                return breaking > 0 ? ExitCode.Failed : ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        /// <summary>Properties where <paramref name="target"/> differs from <paramref name="preset"/>, as "path: is X, preset Y".</summary>
        private static List<string> Diff(Preset preset, UnityEngine.Object target)
        {
            var current = new Preset(target).PropertyModifications.ToDictionary(m => m.propertyPath, m => m.objectReference != null ? AssetDatabase.GetAssetPath(m.objectReference) : m.value);
            var excluded = new HashSet<string>(preset.excludedProperties ?? Array.Empty<string>());
            var diff = new List<string>();
            foreach (var m in preset.PropertyModifications)
            {
                if (excluded.Any(e => m.propertyPath == e || m.propertyPath.StartsWith(e + "."))) continue;
                // Per-asset data that a preset can't sensibly fix.
                if (m.propertyPath.StartsWith("m_FileIDToRecycleName") || m.propertyPath.StartsWith("m_InternalIDToNameTable") || m.propertyPath.StartsWith("m_UsedFileIDs") ||
                    m.propertyPath.StartsWith("m_SpriteSheet") || m.propertyPath.StartsWith("m_UserData") || m.propertyPath.StartsWith("m_AssetBundle") || m.propertyPath.Contains("m_ExternalObjects"))
                    continue;
                string want = m.objectReference != null ? AssetDatabase.GetAssetPath(m.objectReference) : m.value;
                if (!current.TryGetValue(m.propertyPath, out string have) || have != want) diff.Add($"{m.propertyPath}: is {have ?? "(missing)"}, preset {want}");
            }
            return diff;
        }
    }
}

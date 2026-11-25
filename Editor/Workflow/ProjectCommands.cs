using System;
using System.Linq;

using Unity.Pipeline.Commands;

namespace UnityCLIExtensions
{
    /// <summary>Commands for tests that a change affects, asset dependencies, and scene and prefab diffs.</summary>
    internal static class ProjectCommands
    {
        private const string TestGroup = "cliext.test";
        private const string AssetGroup = "cliext.asset";
        private const string DiffGroup = "cliext.diff";

        // ---------------------------------------------------------------------------------------------------------
        // cliext.test

        [CliGroupCommand(TestGroup, "Which test assemblies a change can affect: each changed script or .asmdef maps to its assembly, and every test assembly that is it or depends on it is listed, " +
                                    "with the Pipeline command that runs it. The changes come from --files, or from git (uncommitted changes against --since, default HEAD, plus untracked files). " +
                                    "--all lists every test assembly. Tools~/cliext-test.ps1 runs the list in one call.",
            Tags = new[] { "cliext", "tests" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Affected(
            CommandContext cmd,
            [CliArg("files", "Comma-separated project-relative paths of the changed files (default: from git)")] string files = "",
            [CliArg("since", "Git revision to diff against when --files is empty")] string since = "HEAD",
            [CliArg("all", "List every test assembly instead")] bool all = false)
        {
            AffectedTestsPlan plan;
            if (all)
            {
                plan = new AffectedTestsPlan { testAssemblies = AffectedTests.AllTestAssemblies() };
            }
            else
            {
                string[] changed;
                if (!string.IsNullOrWhiteSpace(files))
                {
                    changed = files.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToArray();
                }
                else
                {
                    try { changed = AffectedTests.ChangedFilesFromGit(since).ToArray(); }
                    catch (Exception e)
                    {
                        cmd.Log.Error($"Could not read the changes from git ({e.Message}). Pass --files, or --all true.");
                        return ExitCode.Failed;
                    }
                }
                plan = AffectedTests.Plan(changed);
            }

            // One cliext.test run per assembly (an exact assembly match). Edit Mode waits for its result; Play Mode
            // reloads the domain, so it returns a run id to read with cliext.test result --wait true.
            var commands = plan.testAssemblies.Select(t => t.mode == "EditMode"
                ? $"cliext.test run --mode edit --filter-type assembly --filter {t.name}"
                : $"cliext.test run --mode play --filter-type assembly --filter {t.name}").ToArray();
            if (plan.otherChanges.Length > 0)
                cmd.Log.Warning($"{plan.otherChanges.Length} changed file(s) are not scripts (scenes, prefabs, data); tests that load them are not picked out. Pass --all true to run everything.");
            if (plan.testAssemblies.Count == 0)
                cmd.Log.Info("No test assembly is affected.");

            cmd.Result.Write(new
            {
                plan.changedFiles,
                plan.changedAssemblies,
                plan.otherChanges,
                plan.testAssemblies,
                commands
            });
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.asset

        [CliGroupCommand(AssetGroup, "What an asset uses: the materials, textures, meshes, scripts, prefabs and other assets it references, directly or (--recursive) through others.",
            Tags = new[] { "cliext", "assets" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Uses(
            CommandContext cmd,
            [CliArg("path", "Asset path or GUID", Required = true)] string path,
            [CliArg("recursive", "Include what its dependencies use")] bool recursive = false,
            [CliArg("include-editor-scripts", "Also list Editor-only scripts, such as the version-tracking script HDRP adds to every material")] bool includeEditorScripts = false)
        {
            string resolved = AssetDependencies.Resolve(path);
            if (resolved == null) { cmd.Log.Error($"No asset at '{path}'."); return ExitCode.NotFound; }
            foreach (var asset in AssetDependencies.Uses(resolved, recursive, includeEditorScripts)) cmd.Result.WriteRecord(asset);
            return ExitCode.Success;
        }

        [CliGroupCommand(AssetGroup, "What uses an asset: the scenes, prefabs, materials, ScriptableObjects and other assets under Assets/ that reference it, directly or (--recursive) through others. " +
                                     "For a script, the scenes and prefabs with that component. The first call builds an index of the project (seconds on a large project); later calls reuse it until an asset changes.",
            Tags = new[] { "cliext", "assets" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode UsedBy(
            CommandContext cmd,
            [CliArg("path", "Asset path or GUID", Required = true)] string path,
            [CliArg("recursive", "Include assets that use it through others")] bool recursive = false,
            [CliArg("include-packages", "Also search the assets inside packages")] bool includePackages = false)
        {
            string resolved = AssetDependencies.Resolve(path);
            if (resolved == null) { cmd.Log.Error($"No asset at '{path}'."); return ExitCode.NotFound; }
            foreach (var asset in AssetDependencies.UsedBy(resolved, recursive, includePackages)) cmd.Result.WriteRecord(asset);
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.diff

        [CliGroupCommand(DiffGroup, "Snapshot a scene or prefab (every object, component and serialized property, including unsaved changes in an open scene), to compare with later. A scene that isn't open is opened additively and closed again.",
            Tags = new[] { "cliext", "scenes", "prefabs" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Snapshot(
            CommandContext cmd,
            [CliArg("path", "Scene (.unity) or prefab (.prefab) path", Required = true)] string path,
            [CliArg("name", "Name to save it under (default: the path)")] string name = "")
        {
            var snapshot = HierarchySnapshot.Take(path, name);
            cmd.Result.Write(new
            {
                snapshot.name, snapshot.assetPath, snapshot.kind, snapshot.takenUtc,
                objects = snapshot.objects.Count,
                components = snapshot.objects.Sum(o => o.components.Count - 1)
            });
            return ExitCode.Success;
        }

        [CliGroupCommand(DiffGroup, "Compare a snapshot with the scene or prefab now: objects added, removed or renamed (moved), components added or removed, and changed properties with their old and new values. --update saves the current state as the new snapshot.",
            Tags = new[] { "cliext", "scenes", "prefabs" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Compare(
            CommandContext cmd,
            [CliArg("name", "The snapshot's name (its path, unless one was given)", Required = true)] string name,
            [CliArg("update", "Then replace the snapshot with the current state")] bool update = false)
        {
            HierarchySnapshot.Snapshot before, after;
            try { foreach (var change in HierarchySnapshot.CompareWithCurrent(name, out before, out after)) cmd.Result.WriteRecord(change); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            if (update) HierarchySnapshot.Take(before.assetPath, before.name);
            return ExitCode.Success;
        }

        [CliGroupCommand(DiffGroup, "Delete a saved snapshot by name, or every snapshot with --all.",
            Tags = new[] { "cliext", "scenes", "prefabs" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Delete(
            CommandContext cmd,
            [CliArg("name", "The snapshot's name (its path, unless one was given)")] string name = "",
            [CliArg("all", "Delete every snapshot")] bool all = false)
        {
            if (all)
            {
                cmd.Result.Write(new { deleted = HierarchySnapshot.DeleteAll() });
                return ExitCode.Success;
            }
            if (string.IsNullOrWhiteSpace(name)) { cmd.Log.Error("Pass --name, or --all true."); return ExitCode.Failed; }
            if (!HierarchySnapshot.Delete(name)) { cmd.Log.Error($"No snapshot named '{name}'. See cliext.diff list."); return ExitCode.NotFound; }
            cmd.Result.Write(new { deleted = 1, name });
            return ExitCode.Success;
        }

        [CliGroupCommand(DiffGroup, "The saved snapshots.", Tags = new[] { "cliext", "scenes", "prefabs" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            foreach (var s in HierarchySnapshot.All().OrderBy(s => s.name))
                cmd.Result.WriteRecord(new { s.name, s.assetPath, s.kind, s.takenUtc, objects = s.objects.Count });
            return ExitCode.Success;
        }
    }
}

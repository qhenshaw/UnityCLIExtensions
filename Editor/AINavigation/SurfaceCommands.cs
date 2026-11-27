using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unity.AI.Navigation;
using Unity.AI.Navigation.Editor;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace UnityCLIExtensions.AINavigation
{
    /// <summary><c>cliext.navmesh surfaces</c> and <c>bake</c>: NavMeshSurface components (AI Navigation), which Pipeline's navmesh bake_surfaces leaves as a stub.</summary>
    internal static class SurfaceCommands
    {
        [CliGroupCommand("cliext.navmesh", "The NavMeshSurface components in the open scenes: agent type, what they collect (all, volume, children), layers, the baked data asset and whether it is current.",
            Name = "surfaces", Tags = new[] { "cliext", "navmesh" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Surfaces(CommandContext cmd)
        {
            foreach (var s in UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include))
                cmd.Result.WriteRecord(new
                {
                    gameObject = s.name,
                    scene = s.gameObject.scene.name,
                    enabled = s.isActiveAndEnabled,
                    agent = NavMesh.GetSettingsNameFromID(s.agentTypeID),
                    collect = s.collectObjects.ToString(),
                    geometry = s.useGeometry.ToString(),
                    layers = s.layerMask.value == ~0 ? "Everything" : string.Join(", ", Enumerable.Range(0, 32).Where(i => (s.layerMask.value & (1 << i)) != 0).Select(LayerMask.LayerToName).Where(n => n.Length > 0)),
                    defaultArea = s.defaultArea,
                    data = s.navMeshData != null ? AssetDatabase.GetAssetPath(s.navMeshData) : null,
                    baking = NavMeshAssetManager.instance.IsSurfaceBaking(s) ? true : (bool?)null
                });
            return ExitCode.Success;
        }

        /// <summary>
        /// The active surfaces <paramref name="surface"/> names (a GameObject name, or all), or null with the error:
        /// none in the open scenes or an unknown name is not found (13, with "Did you mean" and the surfaces); a surface in
        /// an unsaved scene is a failure (6), since its data would be saved straight into Assets/.
        /// </summary>
        internal static NavMeshSurface[] Resolve(string surface, out string error, out ExitCode code)
        {
            error = null; code = ExitCode.Success;
            string name = string.IsNullOrWhiteSpace(surface) ? "all" : surface.Trim();
            var all = UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Exclude);
            if (all.Length == 0)
            {
                error = "The open scenes have no active NavMeshSurface: add one to the floor (a NavMeshSurface component), then bake.";
                code = ExitCode.NotFound;
                return null;
            }
            var surfaces = string.Equals(name, "all", StringComparison.OrdinalIgnoreCase) ? all : all.Where(s => string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (surfaces.Length == 0)
            {
                error = $"No active NavMeshSurface on '{name}'." + AnimationErrors.Hint(name, all.Select(s => s.name), "Surfaces", "cliext.navmesh surfaces") + " --surface all bakes every one.";
                code = ExitCode.NotFound;
                return null;
            }
            var unsaved = surfaces.FirstOrDefault(s => string.IsNullOrEmpty(s.gameObject.scene.path));
            if (unsaved != null)
            {
                error = $"'{unsaved.name}' is in an unsaved scene: save the scene first (the baked data goes in a folder next to it; unsaved, it would land in Assets/).";
                code = ExitCode.Failed;
                return null;
            }
            return surfaces;
        }

        [CliGroupCommand("cliext.navmesh", "Bake NavMeshSurfaces (--surface a GameObject name, or all) the way the Bake button does, saving each surface's data next to its scene " +
                                           "(Assets/Scenes/Level.unity -> Assets/Scenes/Level/NavMesh-<surface>.asset), and wait for it. Edit mode only: in Play mode it exits 6 (editor.playmode stop). " +
                                           "No surface of that name exits 13 (cliext.navmesh surfaces lists them); a surface in an unsaved scene exits 6 (save the scene first).",
            Name = "bake", Tags = new[] { "cliext", "navmesh" }, MainThreadRequired = true, Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> Bake(
            CommandContext cmd,
            [CliArg("surface", "The surface's GameObject, or all")] string surface = "all",
            [CliArg("timeout", "Seconds to wait")] double timeoutInSeconds = 300)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                cmd.Fail("cliext.navmesh bake works in Edit mode (the baked data is saved as an asset): stop Play mode first (editor.playmode stop).");
                return ExitCode.Failed;
            }
            if (!(timeoutInSeconds > 0)) { cmd.Fail($"--timeout {timeoutInSeconds}: give more than 0 seconds."); return ExitCode.InvalidUsage; }
            var surfaces = Resolve(surface, out string error, out ExitCode code);
            if (surfaces == null) { cmd.Fail(error); return code; }
            var clock = Stopwatch.StartNew();
            // Started on a surface whose data asset is still referenced, the bake writes a second asset
            // ("NavMesh-Room 1.asset") and leaves the old one behind. Clear first, as Clear then Bake does.
            // A surface on a prefab instance may share its data with the prefab asset: leave those to Unity's own
            // handling (a new asset beside the old one, as the Bake button makes).
            var clearable = surfaces.Where(s => s.navMeshData != null && AssetDatabase.Contains(s.navMeshData) && !PrefabUtility.IsPartOfPrefabInstance(s)).ToArray();
            var previous = clearable.Select(s => AssetDatabase.GetAssetPath(s.navMeshData)).Distinct().ToList();
            if (clearable.Length > 0) NavMeshAssetManager.instance.ClearSurfaces(clearable.Cast<UnityEngine.Object>().ToArray());
            foreach (string p in previous) if (AssetDatabase.LoadMainAssetAtPath(p) != null) AssetDatabase.DeleteAsset(p);
            int prefabSurfaces = surfaces.Count(s => PrefabUtility.IsPartOfPrefabInstance(s));
            if (prefabSurfaces > 0) cmd.Log.Warning($"{prefabSurfaces} surface(s) are on prefab instances: the bake writes their data to a new asset and leaves the old one, as the Bake button does.");
            NavMeshAssetManager.instance.StartBakingSurfaces(surfaces.Cast<UnityEngine.Object>().ToArray());
            while (surfaces.Any(s => NavMeshAssetManager.instance.IsSurfaceBaking(s)))
            {
                if (clock.Elapsed.TotalSeconds > timeoutInSeconds - 1) { cmd.Fail($"Still baking after {clock.Elapsed.TotalSeconds:0} s."); return ExitCode.TimedOut; }
                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Delay(100);
            }
            foreach (var s in surfaces)
            {
                var data = s.navMeshData;
                cmd.Result.WriteRecord(new { surface = s.name, data = data != null ? AssetDatabase.GetAssetPath(data) : null, bounds = data != null ? ValueText.Format(data.sourceBounds.size) : null });
            }
            cmd.Log.Info($"Baked {surfaces.Length} surface(s) in {clock.Elapsed.TotalSeconds:0.0} s.");
            return ExitCode.Success;
        }
    }
}

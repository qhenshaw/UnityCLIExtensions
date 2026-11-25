using System;
using System.Linq;
using Unity.Cinemachine;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCLIExtensions.Cinemachine
{
    /// <summary>
    /// <c>cliext.cinemachine</c>: the Cinemachine 3 cameras and brains in the open scenes, which one is live and what is
    /// blending, and creating and changing cameras (targets, priority, lens, rig) and blends. Built with Cinemachine 3.
    /// </summary>
    internal static class CinemachineCommands
    {
        private const string Group = "cliext.cinemachine";

        // CinemachineBlenderSettings.kBlendFromAnyCameraLabel, which is internal.
        private const string AnyCamera = "**ANY CAMERA**";

        [CliGroupCommand(Group, "The Cinemachine cameras in the open scenes (priority, targets, field of view, rig components, enabled, live) and the brains (default blend, custom blends, " +
                                "the live camera and the blend in progress). In Play mode this is the live state.",
            Name = "list", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            var cameras = UnityEngine.Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include).OrderByDescending(c => c.Priority.Value).ToArray();
            var brains = UnityEngine.Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include);
            cmd.Result.Write(new
            {
                playing = Application.isPlaying,
                brains = brains.Select(b => new
                {
                    camera = b.gameObject.name,
                    defaultBlend = $"{b.DefaultBlend.Style} {ValueText.Format(b.DefaultBlend.Time)}s",
                    customBlends = b.CustomBlends != null ? AssetDatabase.GetAssetPath(b.CustomBlends) : null,
                    live = b.ActiveVirtualCamera?.Name,
                    blending = b.IsBlending && b.ActiveBlend != null ? new
                    {
                        from = b.ActiveBlend.CamA?.Name,
                        to = b.ActiveBlend.CamB?.Name,
                        weight = Math.Round(b.ActiveBlend.BlendWeight, 3),
                        time = Math.Round(b.ActiveBlend.TimeInBlend, 2),
                        duration = b.ActiveBlend.Duration
                    } : null
                }).ToArray(),
                cameras = cameras.Select(Describe).ToArray(),
                warnings = brains.Length == 0 && cameras.Length > 0 ? new[] { "No CinemachineBrain: add one to the Unity Camera (cliext.cinemachine create adds it)." } : null
            });
            return ExitCode.Success;
        }

        private static object Describe(CinemachineCamera c) => new
        {
            name = c.name,
            path = PathOf(c.transform),
            enabled = c.isActiveAndEnabled,
            priority = c.Priority.Value,
            live = Application.isPlaying ? c.IsLive : (bool?)null,
            follow = c.Target.TrackingTarget != null ? PathOf(c.Target.TrackingTarget) : null,
            lookAt = c.Target.CustomLookAtTarget && c.Target.LookAtTarget != null ? PathOf(c.Target.LookAtTarget) : null,
            fov = c.Lens.FieldOfView,
            orthographicSize = c.Lens.Orthographic ? (float?)c.Lens.OrthographicSize : null,
            components = c.GetComponents<CinemachineComponentBase>().Select(x => x.GetType().Name)
                .Concat(c.GetComponents<CinemachineExtension>().Select(x => x.GetType().Name)).ToArray(),
            blendHint = (int)c.BlendHint == 0 ? null : c.BlendHint.ToString()
        };

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        // ---------------------------------------------------------------------------------------------------------

        [CliGroupCommand(Group, "Create a CinemachineCamera with a rig: follow (CinemachineFollow + RotationComposer), orbital (OrbitalFollow + RotationComposer), " +
                                "thirdperson (ThirdPersonFollow), lookat (HardLookAt, fixed position), fixed (no procedural motion). Adds a CinemachineBrain to the main Camera if it has none. In Play mode the camera lasts until Play mode ends.",
            Name = "create", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("name", "The camera's name", Required = true)] string name,
            [CliArg("rig", "follow, orbital, thirdperson, lookat or fixed")] string rig = "follow",
            [CliArg("follow", "Tracking target (GameObject name or path)")] string follow = "",
            [CliArg("lookat", "Look-at target, when it differs from follow")] string lookAt = "",
            [CliArg("priority", "Priority")] int priority = 10,
            [CliArg("fov", "Vertical field of view (0: keep the default)")] float fov = 0,
            [CliArg("position", "World position [x,y,z]")] string position = "")
        {
            try
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "cliext.cinemachine create");
                if (!string.IsNullOrWhiteSpace(position)) go.transform.position = (Vector3)ValueText.Parse(position, typeof(Vector3));
                var cam = go.AddComponent<CinemachineCamera>();
                cam.Priority.Value = priority;
                if (fov > 0) cam.Lens.FieldOfView = fov;
                if (!string.IsNullOrWhiteSpace(follow)) cam.Target.TrackingTarget = FindTransform(follow);
                if (!string.IsNullOrWhiteSpace(lookAt)) { cam.Target.CustomLookAtTarget = true; cam.Target.LookAtTarget = FindTransform(lookAt); }
                switch ((rig ?? "").Trim().ToLowerInvariant())
                {
                    case "follow": go.AddComponent<CinemachineFollow>(); go.AddComponent<CinemachineRotationComposer>(); break;
                    case "orbital": go.AddComponent<CinemachineOrbitalFollow>(); go.AddComponent<CinemachineRotationComposer>(); break;
                    case "thirdperson": go.AddComponent<CinemachineThirdPersonFollow>(); break;
                    case "lookat": go.AddComponent<CinemachineHardLookAt>(); break;
                    case "fixed": break;
                    default: throw new ArgumentException($"Unknown rig '{rig}': follow, orbital, thirdperson, lookat, fixed.");
                }
                string brain = EnsureBrain();
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(go.scene);
                cmd.Result.Write(new { created = Describe(cam), brainAdded = brain });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Change a CinemachineCamera: priority, follow and look-at targets (empty string to clear with \"none\"), field of view, enabled. In Play mode a priority change re-picks the live camera.",
            Name = "set", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Set(
            CommandContext cmd,
            [CliArg("camera", "The camera (name or path)", Required = true)] string camera,
            [CliArg("priority", "Priority")] string priority = "",
            [CliArg("follow", "Tracking target, or none")] string follow = "",
            [CliArg("lookat", "Look-at target, or none")] string lookAt = "",
            [CliArg("fov", "Vertical field of view")] string fov = "",
            [CliArg("enabled", "Enable or disable the camera")] string enabled = "")
        {
            try
            {
                var cam = FindCamera(camera);
                Undo.RecordObject(cam, "cliext.cinemachine set");
                if (priority != "") cam.Priority.Value = int.Parse(priority);
                if (follow != "") cam.Target.TrackingTarget = follow == "none" ? null : FindTransform(follow);
                if (lookAt != "")
                {
                    cam.Target.CustomLookAtTarget = lookAt != "none";
                    cam.Target.LookAtTarget = lookAt == "none" ? null : FindTransform(lookAt);
                }
                if (fov != "") cam.Lens.FieldOfView = (float)ValueText.Parse(fov, typeof(float));
                if (enabled != "") cam.enabled = (bool)ValueText.Parse(enabled, typeof(bool));
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
                cmd.Result.Write(Describe(cam));
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Set the brain's default blend (--style Cut, EaseInOut, EaseIn, EaseOut, HardIn, HardOut, Linear --time seconds), or with --from and --to a custom blend " +
                                "between two cameras (by name; leave one out for any camera) in the brain's Custom Blends asset (created at --asset when it has none).",
            Name = "blend", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Blend(
            CommandContext cmd,
            [CliArg("style", "The blend curve", Required = true)] string style,
            [CliArg("time", "Seconds")] float time = 1,
            [CliArg("from", "Custom blend: from this camera")] string from = "",
            [CliArg("to", "Custom blend: to this camera")] string to = "",
            [CliArg("asset", "Where to create a Custom Blends asset if the brain has none")] string asset = "")
        {
            var brain = UnityEngine.Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include).FirstOrDefault();
            if (brain == null) { cmd.Log.Error("No CinemachineBrain in the open scenes."); return ExitCode.NotFound; }
            CinemachineBlendDefinition.Styles s;
            try { s = (CinemachineBlendDefinition.Styles)Enum.Parse(typeof(CinemachineBlendDefinition.Styles), style, true); }
            catch { cmd.Log.Error($"Unknown style '{style}': {string.Join(", ", Enum.GetNames(typeof(CinemachineBlendDefinition.Styles)))}"); return ExitCode.InvalidUsage; }
            var def = new CinemachineBlendDefinition(s, time);
            if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
            {
                Undo.RecordObject(brain, "cliext.cinemachine blend");
                brain.DefaultBlend = def;
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(brain.gameObject.scene);
                cmd.Result.Write(new { brain = brain.name, defaultBlend = $"{s} {time}s" });
                return ExitCode.Success;
            }
            var settings = brain.CustomBlends;
            if (settings == null)
            {
                if (string.IsNullOrWhiteSpace(asset)) { cmd.Log.Error("The brain has no Custom Blends asset: pass --asset Assets/.../Blends.asset to create one."); return ExitCode.Failed; }
                settings = ScriptableObject.CreateInstance<CinemachineBlenderSettings>();
                AssetDatabase.CreateAsset(settings, asset);
                Undo.RecordObject(brain, "cliext.cinemachine blend");
                brain.CustomBlends = settings;
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(brain.gameObject.scene);
            }
            string a = string.IsNullOrWhiteSpace(from) ? AnyCamera : from;
            string b = string.IsNullOrWhiteSpace(to) ? AnyCamera : to;
            var list = (settings.CustomBlends ?? Array.Empty<CinemachineBlenderSettings.CustomBlend>()).Where(x => !(x.From == a && x.To == b)).ToList();
            list.Add(new CinemachineBlenderSettings.CustomBlend { From = a, To = b, Blend = def });
            Undo.RecordObject(settings, "cliext.cinemachine blend");
            settings.CustomBlends = list.ToArray();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            cmd.Result.Write(new { asset = AssetDatabase.GetAssetPath(settings), from = a, to = b, blend = $"{s} {time}s", count = list.Count });
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------

        private static CinemachineCamera FindCamera(string name)
        {
            var all = UnityEngine.Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include);
            return all.FirstOrDefault(c => PathOf(c.transform) == name) ?? all.FirstOrDefault(c => string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No CinemachineCamera '{name}'. Cameras: {string.Join(", ", all.Select(c => c.name))}");
        }

        private static Transform FindTransform(string name)
        {
            var go = GameObject.Find(name) ?? Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t.gameObject.scene.IsValid() && (t.hideFlags & HideFlags.HideInHierarchy) == 0)
                .FirstOrDefault(t => string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))?.gameObject;
            return go != null ? go.transform : throw new ArgumentException($"No GameObject '{name}' in the open scenes.");
        }

        /// <summary>Adds a CinemachineBrain to the main Camera when no brain exists. Returns its camera's name, or null.</summary>
        private static string EnsureBrain()
        {
            if (UnityEngine.Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include).Length > 0) return null;
            var camera = Camera.main ?? UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude).FirstOrDefault();
            if (camera == null) return null;
            Undo.AddComponent<CinemachineBrain>(camera.gameObject);
            return camera.name;
        }
    }
}

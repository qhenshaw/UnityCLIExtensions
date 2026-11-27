using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Cinemachine;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
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

        internal static readonly string[] Rigs = { "follow", "orbital", "thirdperson", "lookat", "fixed" };

        [CliGroupCommand(Group, "The Cinemachine cameras in the open scenes (priority, targets, field of view, rig components, enabled) and the brains (default blend, custom blends). " +
                                "Which camera is live and the blend in progress are Play mode state: in Edit mode live and blending are null and a note says so; enter Play mode (editor.playmode start) to read them.",
            Name = "list", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode List(CommandContext cmd)
        {
            bool playing = Application.isPlaying;
            var cameras = UnityEngine.Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include).OrderByDescending(c => c.Priority.Value).ToArray();
            var brains = UnityEngine.Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include);
            var warnings = new List<string>();
            if (brains.Length == 0 && cameras.Length > 0) warnings.Add("No CinemachineBrain: add one to the Unity Camera (cliext.cinemachine create adds it).");
            cmd.Result.Write(new
            {
                playing,
                note = playing ? null : "Edit mode: live and blending are Play mode state. Enter Play mode (editor.playmode start) to read which camera is live and what is blending.",
                brains = brains.Select(b => new
                {
                    camera = b.gameObject.name,
                    defaultBlend = BlendText(b.DefaultBlend),
                    customBlends = b.CustomBlends != null ? AssetDatabase.GetAssetPath(b.CustomBlends) : null,
                    custom = b.CustomBlends != null && b.CustomBlends.CustomBlends != null
                        ? b.CustomBlends.CustomBlends.Select(x => $"{Label(x.From)} -> {Label(x.To)}: {BlendText(x.Blend)}").ToArray() : null,
                    live = playing ? b.ActiveVirtualCamera?.Name : null,
                    blending = playing && b.IsBlending && b.ActiveBlend != null ? new
                    {
                        from = b.ActiveBlend.CamA?.Name,
                        to = b.ActiveBlend.CamB?.Name,
                        weight = Math.Round(b.ActiveBlend.BlendWeight, 3),
                        time = Math.Round(b.ActiveBlend.TimeInBlend, 2),
                        duration = b.ActiveBlend.Duration
                    } : null
                }).ToArray(),
                cameras = cameras.Select(Describe).ToArray(),
                warnings = warnings.Count > 0 ? warnings.ToArray() : null
            });
            return ExitCode.Success;
        }

        private static string Label(string cam) => cam == AnyCamera ? "any" : cam;

        internal static string BlendText(CinemachineBlendDefinition b) => $"{b.Style} {ValueText.Format(b.Time)}s";

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

        internal static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        // ---------------------------------------------------------------------------------------------------------

        [CliGroupCommand(Group, "Create a CinemachineCamera with a rig: follow (CinemachineFollow + RotationComposer), orbital (OrbitalFollow + RotationComposer), " +
                                "thirdperson (ThirdPersonFollow), lookat (HardLookAt, fixed position), fixed (no procedural motion). Adds a CinemachineBrain to the main Camera if it has none. " +
                                "In Play mode the camera lasts until Play mode ends. Everything is checked before anything is made: an unknown --follow or --lookat exits 13; " +
                                "an unknown --rig, a bad --position or --fov exits 2; a camera of that name already existing exits 6 (change it with cliext.cinemachine set).",
            Name = "create", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Create(
            CommandContext cmd,
            [CliArg("name", "The camera's name", Required = true)] string name,
            [CliArg("rig", "follow, orbital, thirdperson, lookat or fixed")] string rig = "follow",
            [CliArg("follow", "Tracking target (GameObject name or path)")] string follow = "",
            [CliArg("lookat", "Look-at target, when it differs from follow")] string lookAt = "",
            [CliArg("priority", "Priority, an integer; the highest live camera wins")] int priority = 10,
            [CliArg("fov", "Vertical field of view in degrees, more than 0 and less than 180 (0: keep the default)")] float fov = 0,
            [CliArg("position", "World position [x,y,z]")] string position = "")
        {
            try
            {
                // Resolve and check everything first, so a bad argument makes nothing.
                name = (name ?? "").Trim();
                if (name.Length == 0) throw new AnimUsageException("--name is empty: give the camera a name.");
                string r = ParseRig(rig);
                Vector3? pos = string.IsNullOrWhiteSpace(position) ? (Vector3?)null : ParseVector(position, "--position");
                if (fov != 0) CheckFov(fov);
                var followT = string.IsNullOrWhiteSpace(follow) ? null : FindTransform(follow);
                var lookAtT = string.IsNullOrWhiteSpace(lookAt) ? null : FindTransform(lookAt);
                var existing = UnityEngine.Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include)
                    .FirstOrDefault(c => string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    throw new ArgumentException($"A CinemachineCamera named '{existing.name}' already exists: change it with cliext.cinemachine set --camera {existing.name}, or pick another --name.");

                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "cliext.cinemachine create");
                if (pos.HasValue) go.transform.position = pos.Value;
                var cam = go.AddComponent<CinemachineCamera>();
                cam.Priority.Value = priority;
                if (fov > 0) cam.Lens.FieldOfView = fov;
                if (followT != null) cam.Target.TrackingTarget = followT;
                if (lookAtT != null) { cam.Target.CustomLookAtTarget = true; cam.Target.LookAtTarget = lookAtT; }
                switch (r)
                {
                    case "follow": go.AddComponent<CinemachineFollow>(); go.AddComponent<CinemachineRotationComposer>(); break;
                    case "orbital": go.AddComponent<CinemachineOrbitalFollow>(); go.AddComponent<CinemachineRotationComposer>(); break;
                    case "thirdperson": go.AddComponent<CinemachineThirdPersonFollow>(); break;
                    case "lookat": go.AddComponent<CinemachineHardLookAt>(); break;
                }
                string brain = EnsureBrain();
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(go.scene);
                bool noBrain = UnityEngine.Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include).Length == 0;
                cmd.Result.Write(new
                {
                    created = Describe(cam),
                    brainAdded = brain,
                    warnings = noBrain ? new[] { "No Camera in the open scenes to add a CinemachineBrain to: add a Camera, then a CinemachineBrain on it, for this camera to drive the view." } : null
                });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        internal static string ParseRig(string rig)
        {
            string r = (rig ?? "").Trim().ToLowerInvariant();
            if (r.Length == 0) r = "follow";
            if (Rigs.Contains(r)) return r;
            var close = AnimationErrors.Close(r, Rigs);
            throw new AnimUsageException($"Unknown rig '{rig}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Use one of: {string.Join(", ", Rigs)}.");
        }

        internal static void CheckFov(float fov)
        {
            if (float.IsNaN(fov) || fov <= 0 || fov >= 180)
                throw new AnimUsageException($"--fov {ValueText.Format(fov)} isn't a field of view: give degrees more than 0 and less than 180.");
        }

        private static Vector3 ParseVector(string text, string arg)
        {
            try { return (Vector3)ValueText.Parse(text, typeof(Vector3)); }
            catch (ArgumentException e) { throw new AnimUsageException($"{arg}: {e.Message}"); }
        }

        [CliGroupCommand(Group, "Change a CinemachineCamera: priority, follow and look-at targets (a GameObject name or path, or none to clear), field of view, enabled, " +
                                "and --rig (follow, orbital, thirdperson, lookat, fixed, as create takes) to replace its position and rotation components. " +
                                "Targets alone don't move a camera: one with no position (body) or rotation (aim) component ignores them, and the output's warnings say so and name --rig follow. " +
                                "In Play mode a priority change re-picks the live camera. Every target is found before anything changes. An unknown camera or target exits 13 " +
                                "(cliext.cinemachine list names the cameras); a bad --fov, an unknown --rig or no change asked for exits 2; values the camera already has (nothing would change) exit 6.",
            Name = "set", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Set(
            CommandContext cmd,
            [CliArg("camera", "The camera (name or path)", Required = true)] string camera,
            [CliArg("priority", "Priority, an integer; the highest live camera wins (leave out to keep it)")] int? priority = null,
            [CliArg("follow", "Tracking target (GameObject name or path), or none to clear it")] string follow = "",
            [CliArg("lookat", "Look-at target (GameObject name or path), or none to clear it")] string lookAt = "",
            [CliArg("fov", "Vertical field of view in degrees, more than 0 and less than 180 (leave out to keep it)")] float? fov = null,
            [CliArg("enabled", "true to enable the camera, false to disable it (leave out to keep it)")] bool? enabled = null,
            [CliArg("rig", "Replace the position and rotation components: follow, orbital, thirdperson, lookat or fixed (leave out to keep them)")] string rig = "")
        {
            try
            {
                var cam = FindCamera(camera);
                var plan = Plan(cam, priority, follow, lookAt, fov, enabled, rig);
                if (plan.Rig != null) ApplyRig(cam, plan.Rig);
                Undo.RecordObject(cam, "cliext.cinemachine set");
                if (plan.Priority.HasValue) cam.Priority.Value = plan.Priority.Value;
                if (plan.SetFollow) cam.Target.TrackingTarget = plan.Follow;
                if (plan.SetLookAt)
                {
                    cam.Target.CustomLookAtTarget = plan.LookAt != null;
                    cam.Target.LookAtTarget = plan.LookAt;
                }
                if (plan.Fov.HasValue) cam.Lens.FieldOfView = plan.Fov.Value;
                if (plan.Enabled.HasValue) cam.enabled = plan.Enabled.Value;
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
                var warnings = RigWarnings(cam);
                cmd.Result.Write(new { changed = plan.Changed.ToArray(), camera = Describe(cam), warnings = warnings.Count > 0 ? warnings.ToArray() : null });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        private static CinemachineComponentBase Stage(CinemachineCamera cam, CinemachineCore.Stage stage) =>
            cam.GetComponents<CinemachineComponentBase>().FirstOrDefault(c => c.Stage == stage);

        /// <summary>The rig the camera's position and rotation components make (as create names them), or null for another mix.</summary>
        internal static string CurrentRig(CinemachineCamera cam)
        {
            var body = Stage(cam, CinemachineCore.Stage.Body);
            var aim = Stage(cam, CinemachineCore.Stage.Aim);
            if (body is CinemachineFollow && aim is CinemachineRotationComposer) return "follow";
            if (body is CinemachineOrbitalFollow && aim is CinemachineRotationComposer) return "orbital";
            if (body is CinemachineThirdPersonFollow && aim == null) return "thirdperson";
            if (body == null && aim is CinemachineHardLookAt) return "lookat";
            if (body == null && aim == null) return "fixed";
            return null;
        }

        /// <summary>Replaces the camera's position (body) and rotation (aim) components with the rig's, with undo. Noise and extensions stay.</summary>
        internal static void ApplyRig(CinemachineCamera cam, string rig)
        {
            foreach (var c in cam.GetComponents<CinemachineComponentBase>().Where(c => c.Stage == CinemachineCore.Stage.Body || c.Stage == CinemachineCore.Stage.Aim).ToArray())
                Undo.DestroyObjectImmediate(c);
            var go = cam.gameObject;
            switch (rig)
            {
                case "follow": Undo.AddComponent<CinemachineFollow>(go); Undo.AddComponent<CinemachineRotationComposer>(go); break;
                case "orbital": Undo.AddComponent<CinemachineOrbitalFollow>(go); Undo.AddComponent<CinemachineRotationComposer>(go); break;
                case "thirdperson": Undo.AddComponent<CinemachineThirdPersonFollow>(go); break;
                case "lookat": Undo.AddComponent<CinemachineHardLookAt>(go); break;
            }
        }

        /// <summary>What the camera's targets won't do because it has no component to use them.</summary>
        internal static List<string> RigWarnings(CinemachineCamera cam)
        {
            var warnings = new List<string>();
            var follow = cam.Target.TrackingTarget;
            var lookAt = cam.Target.CustomLookAtTarget ? cam.Target.LookAtTarget : follow;
            var body = Stage(cam, CinemachineCore.Stage.Body);
            if (follow != null && body == null)
                warnings.Add($"{cam.name} has no position (body) component, so it doesn't move with its follow target {follow.name}: add a rig with cliext.cinemachine set --camera {cam.name} --rig follow (CinemachineFollow + RotationComposer), orbital or thirdperson.");
            if (lookAt != null && Stage(cam, CinemachineCore.Stage.Aim) == null && !(body is CinemachineThirdPersonFollow))
                warnings.Add($"{cam.name} has no rotation (aim) component, so it doesn't turn toward {lookAt.name}: add one with --rig follow (RotationComposer) or --rig lookat (HardLookAt).");
            return warnings;
        }

        internal sealed class SetPlan
        {
            public string Rig;
            public int? Priority;
            public bool SetFollow, SetLookAt;
            public Transform Follow, LookAt;
            public float? Fov;
            public bool? Enabled;
            public readonly List<string> Changed = new List<string>();
        }

        private static bool IsNone(string s) => string.Equals((s ?? "").Trim(), "none", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves and checks a set without changing the camera: throws when a target is unknown (13), a value is bad or
        /// nothing is asked for (2), or every value given is what the camera already has (6).
        /// </summary>
        internal static SetPlan Plan(CinemachineCamera cam, int? priority, string follow, string lookAt, float? fov, bool? enabled, string rig = "")
        {
            bool hasFollow = !string.IsNullOrWhiteSpace(follow), hasLookAt = !string.IsNullOrWhiteSpace(lookAt), hasRig = !string.IsNullOrWhiteSpace(rig);
            if (!priority.HasValue && !hasFollow && !hasLookAt && !fov.HasValue && !enabled.HasValue && !hasRig)
                throw new AnimUsageException($"Nothing to change on {cam.name}: pass --priority, --follow, --lookat, --fov, --enabled or --rig.");
            if (fov.HasValue) CheckFov(fov.Value);
            var plan = new SetPlan();
            var same = new List<string>();
            if (hasRig)
            {
                string r = ParseRig(rig);
                if (CurrentRig(cam) == r) same.Add($"rig {r}");
                else { plan.Rig = r; plan.Changed.Add("rig"); }
            }
            if (priority.HasValue)
            {
                if (cam.Priority.Value == priority.Value) same.Add($"priority {priority.Value}");
                else { plan.Priority = priority; plan.Changed.Add("priority"); }
            }
            if (hasFollow)
            {
                var t = IsNone(follow) ? null : FindTransform(follow);
                if (t == cam.Target.TrackingTarget) same.Add(t == null ? "no follow target" : $"follow {t.name}");
                else { plan.SetFollow = true; plan.Follow = t; plan.Changed.Add("follow"); }
            }
            if (hasLookAt)
            {
                var t = IsNone(lookAt) ? null : FindTransform(lookAt);
                var current = cam.Target.CustomLookAtTarget ? cam.Target.LookAtTarget : null;
                if (t == current) same.Add(t == null ? "no look-at target" : $"look-at {t.name}");
                else { plan.SetLookAt = true; plan.LookAt = t; plan.Changed.Add("lookAt"); }
            }
            if (fov.HasValue)
            {
                if (Mathf.Approximately(cam.Lens.FieldOfView, fov.Value)) same.Add($"fov {ValueText.Format(fov.Value)}");
                else { plan.Fov = fov; plan.Changed.Add("fov"); }
            }
            if (enabled.HasValue)
            {
                if (cam.enabled == enabled.Value) same.Add(enabled.Value ? "enabled" : "disabled");
                else { plan.Enabled = enabled; plan.Changed.Add("enabled"); }
            }
            if (plan.Changed.Count == 0)
                throw new ArgumentException($"{cam.name} already has {string.Join(", ", same)}: nothing would change. cliext.cinemachine list shows its values.");
            return plan;
        }

        [CliGroupCommand(Group, "Set the brain's default blend (--style Cut, EaseInOut, EaseIn, EaseOut, HardIn, HardOut, Linear --time seconds), or with --from and --to a custom blend " +
                                "between two cameras (by name; leave one out for any camera) in the brain's Custom Blends asset (created at --asset, an Assets/.../Name.asset path, when it has none; a missing folder is made). " +
                                "No brain or an unknown camera exits 13; an unknown --style, a negative --time, a bad --asset path, or a custom blend with no asset to keep it exits 2; " +
                                "a blend the brain already has exits 6. In Play mode a default blend change lasts until Play mode ends.",
            Name = "blend", Tags = new[] { "cliext", "cinemachine" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Blend(
            CommandContext cmd,
            [CliArg("style", "The blend curve: Cut, EaseInOut, EaseIn, EaseOut, HardIn, HardOut or Linear", Required = true)] string style,
            [CliArg("time", "Blend duration in seconds, 0 or more")] float time = 1,
            [CliArg("from", "Custom blend: from this camera")] string from = "",
            [CliArg("to", "Custom blend: to this camera")] string to = "",
            [CliArg("asset", "Where to create a Custom Blends asset (Assets/.../Name.asset) if the brain has none")] string asset = "")
        {
            try
            {
                var s = AnimationErrors.ParseEnum<CinemachineBlendDefinition.Styles>(style, "blend style");
                if (float.IsNaN(time) || time < 0) throw new AnimUsageException($"--time {ValueText.Format(time)} isn't a duration: give 0 or more seconds.");
                var brain = UnityEngine.Object.FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include).FirstOrDefault();
                if (brain == null)
                    throw new AnimNotFoundException("No CinemachineBrain in the open scenes: cliext.cinemachine create adds one to the main Camera, or add a CinemachineBrain to a Camera.");
                var def = new CinemachineBlendDefinition(s, time);
                if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
                {
                    if (!string.IsNullOrWhiteSpace(asset)) throw new AnimUsageException("--asset is only for a custom blend: give --from and/or --to, or leave --asset out to set the default blend.");
                    if (SameBlend(brain.DefaultBlend, def))
                        throw new ArgumentException($"{brain.name}'s default blend is already {BlendText(def)}: nothing would change.");
                    Undo.RecordObject(brain, "cliext.cinemachine blend");
                    brain.DefaultBlend = def;
                    if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(brain.gameObject.scene);
                    cmd.Result.Write(new { brain = brain.name, defaultBlend = BlendText(def) });
                    return ExitCode.Success;
                }
                string a = string.IsNullOrWhiteSpace(from) ? AnyCamera : FindCamera(from).name;
                string b = string.IsNullOrWhiteSpace(to) ? AnyCamera : FindCamera(to).name;
                if (a == b) throw new AnimUsageException($"--from and --to are both {a}: a custom blend is between two cameras.");

                var settings = brain.CustomBlends;
                string newPath = null;
                if (settings == null) newPath = CheckNewAssetPath(asset, out settings);
                else if (!string.IsNullOrWhiteSpace(asset) && AnimationErrors.AssetPath(asset) != AssetDatabase.GetAssetPath(settings))
                    throw new AnimUsageException($"{brain.name} already keeps its custom blends in {AssetDatabase.GetAssetPath(settings)}: leave --asset out.");

                var current = settings != null && settings.CustomBlends != null ? settings.CustomBlends : Array.Empty<CinemachineBlenderSettings.CustomBlend>();
                if (current.Any(x => x.From == a && x.To == b && SameBlend(x.Blend, def)))
                    throw new ArgumentException($"The custom blend {Label(a)} -> {Label(b)} is already {BlendText(def)}: nothing would change.");

                bool assign = brain.CustomBlends == null;
                if (settings == null)
                {
                    ProjectFolders.Ensure(Path.GetDirectoryName(newPath)?.Replace('\\', '/'));
                    settings = ScriptableObject.CreateInstance<CinemachineBlenderSettings>();
                    AssetDatabase.CreateAsset(settings, newPath);
                }
                if (assign)
                {
                    Undo.RecordObject(brain, "cliext.cinemachine blend");
                    brain.CustomBlends = settings;
                    if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(brain.gameObject.scene);
                }
                var list = current.Where(x => !(x.From == a && x.To == b)).ToList();
                list.Add(new CinemachineBlenderSettings.CustomBlend { From = a, To = b, Blend = def });
                Undo.RecordObject(settings, "cliext.cinemachine blend");
                settings.CustomBlends = list.ToArray();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                cmd.Result.Write(new { asset = AssetDatabase.GetAssetPath(settings), created = newPath != null, from = Label(a), to = Label(b), blend = BlendText(def), count = list.Count });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        private static bool SameBlend(CinemachineBlendDefinition x, CinemachineBlendDefinition y) =>
            x.Style == y.Style && (x.Style == CinemachineBlendDefinition.Styles.Cut || Mathf.Approximately(x.Time, y.Time));

        /// <summary>
        /// Checks where a new Custom Blends asset would go: an Assets/ or Packages/ path ending .asset in a folder that
        /// exists. Returns the path, or null with <paramref name="existing"/> set when a Custom Blends asset is already there.
        /// </summary>
        internal static string CheckNewAssetPath(string asset, out CinemachineBlenderSettings existing)
        {
            existing = null;
            string path = AnimationErrors.AssetPath(asset);
            if (string.IsNullOrEmpty(path))
                throw new AnimUsageException("The brain has no Custom Blends asset: pass --asset Assets/.../Blends.asset to create one.");
            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                throw new AnimUsageException($"--asset '{asset}' isn't under Assets/ or Packages/: give a project path such as Assets/Cameras/Blends.asset.");
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                throw new AnimUsageException($"--asset '{asset}' must end in .asset (such as Assets/Cameras/Blends.asset).");
            string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!ProjectFolders.IsProjectFolder(folder))
                throw new AnimUsageException($"--asset '{asset}': give a path in a folder under Assets/ (or inside a package), such as Assets/Cameras/Blends.asset.");
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null) return path;
            if (main is CinemachineBlenderSettings bs) { existing = bs; return null; }
            throw new AnimUsageException($"'{path}' is {AnimationErrors.A(main.GetType().Name)}, not a CinemachineBlenderSettings: give a new path or a Custom Blends asset.");
        }

        // ---------------------------------------------------------------------------------------------------------

        internal static CinemachineCamera FindCamera(string name)
        {
            string n = (name ?? "").Trim();
            var all = UnityEngine.Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include);
            var cam = all.FirstOrDefault(c => PathOf(c.transform) == n) ?? all.FirstOrDefault(c => string.Equals(c.name, n, StringComparison.OrdinalIgnoreCase));
            if (cam != null) return cam;
            if (all.Length == 0)
                throw new AnimNotFoundException($"No CinemachineCamera '{n}': the open scenes have none. cliext.cinemachine create makes one.");
            var go = FindGameObjectOrNull(n);
            string hint = AnimationErrors.Hint(n, all.Select(c => c.name), "Cameras", "cliext.cinemachine list");
            throw new AnimNotFoundException(go != null ? $"'{go.name}' has no CinemachineCamera.{hint}" : $"No CinemachineCamera '{n}' in the open scenes.{hint}");
        }

        private static IEnumerable<GameObject> SceneObjects() =>
            Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.scene.IsValid() && (g.hideFlags & HideFlags.HideInHierarchy) == 0);

        private static GameObject FindGameObjectOrNull(string name) =>
            string.IsNullOrWhiteSpace(name) ? null :
            GameObject.Find(name) ?? SceneObjects().FirstOrDefault(g => string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase));

        internal static Transform FindTransform(string name)
        {
            string n = (name ?? "").Trim();
            var go = FindGameObjectOrNull(n);
            if (go != null) return go.transform;
            var close = AnimationErrors.Close(n, SceneObjects().Select(g => g.name).Distinct());
            throw new AnimNotFoundException($"No GameObject '{n}' in the open scenes.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} " +
                                            "Give its name or hierarchy path (scene get_hierarchy lists them), or none to clear the target.");
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

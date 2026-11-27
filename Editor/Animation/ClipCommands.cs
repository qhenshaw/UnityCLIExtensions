using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCLIExtensions.Animation
{
    [Serializable]
    internal class ClipOp
    {
        /// <summary>Model clips: add_clip, set_clip, remove_clip, set_events, set_mask. AnimationClip: set_key, remove_key, add_event, remove_event, set_settings.</summary>
        public string op;
        /// <summary>The clip (its name in the model's clip list).</summary>
        public string name;
        /// <summary>For add_clip: the take it cuts from (default: the first).</summary>
        public string take;
        public float first = -1;
        public float last = -1;
        /// <summary>Settings as name=value;... (loopTime, loopPose, cycleOffset, lockRootRotation, keepOriginalOrientation, lockRootHeightY, keepOriginalPositionY, lockRootPositionXZ, keepOriginalPositionXZ, heightFromFeet, mirror, ...).</summary>
        public string set;
        /// <summary>Events "time:function[:param]" separated by ';' (time is normalized 0-1 for model clips, seconds for .anim).</summary>
        public string events;
        /// <summary>For set_mask: an AvatarMask path, or none.</summary>
        public string mask;
        /// <summary>For keys: the binding's object path ("" for the root), component type (Transform, SpriteRenderer, ...) and property (m_LocalPosition.x).</summary>
        public string path;
        public string type;
        public string property;
        public float time = -1;
        public float value;
        /// <summary>For set_key: auto, linear, constant, free (default auto).</summary>
        public string tangent;
        public string function;
        public string parameter;
    }

    [Serializable]
    internal class ClipOpList
    {
        public ClipOp[] ops;
    }

    /// <summary>
    /// <c>cliext.anim</c>: animation import from models (takes, clip ranges, loop, root motion, masks, events), keys and
    /// events on .anim clips (Pipeline's animation.clip sets whole curves), avatar masks, and a check that clips fit a model.
    /// </summary>
    internal static class ClipCommands
    {
        private const string Group = "cliext.anim";

        private static string FromGuid(string path)
        {
            path = (path ?? "").Trim();
            return path.Length == 32 && path.All(Uri.IsHexDigit) ? AssetDatabase.GUIDToAssetPath(path) : path;
        }

        private static ModelImporter ModelAt(string path) =>
            AssetImporter.GetAtPath(FromGuid(path)) as ModelImporter ?? throw new ArgumentException($"'{path}' isn't a model.");

        // --ops / --ops-file first (invalid usage when neither, both, or not a list of ops), before the target is looked up.
        private static bool TryReadOps(CommandContext cmd, string ops, string opsFile, out ClipOpList list, out ExitCode failure)
        {
            list = null;
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out failure)) return false;
            try
            {
                string s = json.Trim();
                list = JsonUtility.FromJson<ClipOpList>(s.StartsWith("[") ? "{\"ops\":" + s + "}" : s);
            }
            catch (Exception e) { cmd.Log.Error($"The ops aren't valid JSON: {e.Message}"); failure = ExitCode.InvalidUsage; return false; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Log.Error("No ops given."); failure = ExitCode.InvalidUsage; return false; }
            return true;
        }

        private static object DescribeModelClip(ModelImporterClipAnimation c) => new
        {
            name = c.name,
            take = c.takeName,
            first = c.firstFrame,
            last = c.lastFrame,
            loopTime = c.loopTime,
            loopPose = c.loopPose,
            cycleOffset = c.cycleOffset,
            mirror = c.mirror ? true : (bool?)null,
            root = new
            {
                rotationBaked = c.lockRootRotation, rotationKeepOriginal = c.keepOriginalOrientation,
                heightBaked = c.lockRootHeightY, heightKeepOriginal = c.keepOriginalPositionY, heightFromFeet = c.heightFromFeet,
                positionXZBaked = c.lockRootPositionXZ, positionXZKeepOriginal = c.keepOriginalPositionXZ
            },
            mask = c.maskSource != null ? AssetDatabase.GetAssetPath(c.maskSource) : c.maskType == ClipAnimationMaskType.CreateFromThisModel ? "(this model)" : null,
            events = SafeEvents(c).Select(e => $"{ValueText.Format(e.time)}:{e.functionName}{(string.IsNullOrEmpty(e.stringParameter) ? "" : ":" + e.stringParameter)}").ToArray()
        };

        private static AnimationEvent[] SafeEvents(ModelImporterClipAnimation c)
        {
            try { return c.events ?? Array.Empty<AnimationEvent>(); } catch (ArgumentNullException) { return Array.Empty<AnimationEvent>(); }
        }

        [CliGroupCommand(Group, "A model's animation import: rig type, avatar source, the takes in the file (with frame ranges), and the clips cut from them with loop, root motion, mirror, mask and events.",
            Name = "takes", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Takes(CommandContext cmd, [CliArg("path", "The model (.fbx, ...) path or GUID", Required = true)] string path)
        {
            try
            {
                var m = ModelAt(path);
                var clips = m.clipAnimations.Length > 0 ? m.clipAnimations : m.defaultClipAnimations;
                cmd.Result.Write(new
                {
                    path,
                    animationType = m.animationType.ToString(),
                    avatar = m.sourceAvatar != null ? AssetDatabase.GetAssetPath(m.sourceAvatar) : m.avatarSetup.ToString(),
                    importAnimation = m.importAnimation,
                    takes = (m.importedTakeInfos ?? Array.Empty<TakeInfo>()).Select(t => new { t.name, start = Math.Round(t.startTime * t.sampleRate), stop = Math.Round(t.stopTime * t.sampleRate), frameRate = t.sampleRate }).ToArray(),
                    clipsAreDefault = m.clipAnimations.Length == 0 ? true : (bool?)null,
                    clips = clips.Select(DescribeModelClip).ToArray()
                });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
        }

        [CliGroupCommand(Group, "Edit the clips a model imports, then reimport: add_clip (name, take, first, last frames), set_clip (name, set=\"loopTime=true;loopPose=true;lockRootRotation=true;keepOriginalOrientation=true;lockRootHeightY=true;lockRootPositionXZ=true;mirror=false\"; first, last), " +
                                "remove_clip, set_events (name, events=\"0.5:Footstep;0.9:Footstep:Left\", normalized times; replaces them), set_mask (name, mask=AvatarMask path or none). " +
                                "A model with no custom clips starts from its default ones. An op that fails saves nothing: the model's import settings are left as they were.",
            Name = "edit_clips", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode EditClips(
            CommandContext cmd,
            [CliArg("path", "The model (.fbx, ...) path or GUID", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!TryReadOps(cmd, ops, opsFile, out var list, out ExitCode usage)) return usage;
            try
            {
                var m = ModelAt(path);
                var clips = (m.clipAnimations.Length > 0 ? m.clipAnimations : m.defaultClipAnimations).ToList();
                ModelImporterClipAnimation Find(string name) => clips.FirstOrDefault(c => string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No clip '{name}'. Clips: {string.Join(", ", clips.Select(c => c.name))}");
                var results = new List<object>();
                foreach (var op in list.ops)
                {
                    switch ((op.op ?? "").Trim().ToLowerInvariant())
                    {
                        case "add_clip":
                        {
                            var takes = m.importedTakeInfos;
                            var take = string.IsNullOrEmpty(op.take) ? takes.FirstOrDefault() : takes.FirstOrDefault(t => string.Equals(t.name, op.take, StringComparison.OrdinalIgnoreCase));
                            if (take.name == null) throw new ArgumentException($"No take '{op.take}'. Takes: {string.Join(", ", takes.Select(t => t.name))}");
                            var c = new ModelImporterClipAnimation
                            {
                                name = op.name, takeName = take.name,
                                firstFrame = op.first >= 0 ? op.first : (float)Math.Round(take.startTime * take.sampleRate),
                                lastFrame = op.last >= 0 ? op.last : (float)Math.Round(take.stopTime * take.sampleRate)
                            };
                            // A new ModelImporterClipAnimation's events getter throws until they are set.
                            c.events = Array.Empty<AnimationEvent>();
                            AnimatorEditing.SetMembers(c, op.set);
                            clips.Add(c);
                            results.Add(new { op = "add_clip", clip = DescribeModelClip(c) });
                            break;
                        }
                        case "set_clip":
                        {
                            var c = Find(op.name);
                            if (op.first >= 0) c.firstFrame = op.first;
                            if (op.last >= 0) c.lastFrame = op.last;
                            AnimatorEditing.SetMembers(c, op.set);
                            results.Add(new { op = "set_clip", clip = DescribeModelClip(c) });
                            break;
                        }
                        case "remove_clip":
                            clips.Remove(Find(op.name));
                            results.Add(new { op = "remove_clip", name = op.name });
                            break;
                        case "set_events":
                        {
                            var c = Find(op.name);
                            c.events = ParseEvents(op.events).ToArray();
                            results.Add(new { op = "set_events", name = op.name, events = c.events.Length });
                            break;
                        }
                        case "set_mask":
                        {
                            var c = Find(op.name);
                            if (string.IsNullOrEmpty(op.mask) || op.mask == "none") { c.maskType = ClipAnimationMaskType.None; c.maskSource = null; }
                            else
                            {
                                c.maskType = ClipAnimationMaskType.CopyFromOther;
                                c.maskSource = AssetDatabase.LoadAssetAtPath<AvatarMask>(op.mask) ?? throw new ArgumentException($"No AvatarMask at '{op.mask}'.");
                            }
                            results.Add(new { op = "set_mask", name = op.name, mask = op.mask });
                            break;
                        }
                        default:
                            throw new ArgumentException($"Unknown op '{op.op}'. Ops: add_clip, set_clip, remove_clip, set_events, set_mask.");
                    }
                }
                m.clipAnimations = clips.ToArray();
                m.SaveAndReimport();
                cmd.Result.Write(new { path, applied = results.Count, results });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is IOException) { cmd.Log.Error((e is ArgumentNullException ? e.ToString() : e.Message) + " Nothing was saved."); return ExitCode.Failed; }
        }

        private static IEnumerable<AnimationEvent> ParseEvents(string events)
        {
            foreach (string e in (events ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = e.Split(':');
                if (parts.Length < 2) throw new ArgumentException($"Event '{e}' should be time:function[:parameter].");
                var ev = new AnimationEvent { time = float.Parse(parts[0], CultureInfo.InvariantCulture), functionName = parts[1].Trim() };
                if (parts.Length > 2)
                {
                    string p = parts[2];
                    if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) ev.intParameter = i;
                    else if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) ev.floatParameter = f;
                    else ev.stringParameter = p;
                }
                yield return ev;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // AnimationClip keys and events

        private static AnimationClip ClipAt(string path)
        {
            path = FromGuid(path);
            int at = path.LastIndexOf('@');
            if (at > 0 && !File.Exists(path))
                return AssetDatabase.LoadAllAssetsAtPath(path.Substring(0, at)).OfType<AnimationClip>().FirstOrDefault(c => c.name == path.Substring(at + 1))
                       ?? throw new ArgumentException($"No clip '{path}'.");
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path) ?? throw new ArgumentException($"No AnimationClip at '{path}'.");
        }

        [CliGroupCommand(Group, "Describe an AnimationClip (.anim, or a model's clip as model.fbx@clip): length, frame rate, loop and root settings, every curve binding with its key count and time range, object reference curves (sprites), and events.",
            Name = "clip", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Clip(CommandContext cmd, [CliArg("path", "The clip: an .anim path or GUID, or a model clip as model.fbx@clip", Required = true)] string path, [CliArg("keys", "Include every key")] bool keys = false)
        {
            try
            {
                var clip = ClipAt(path);
                var s = AnimationUtility.GetAnimationClipSettings(clip);
                cmd.Result.Write(new
                {
                    name = clip.name,
                    length = clip.length,
                    frameRate = clip.frameRate,
                    humanoid = clip.humanMotion,
                    legacy = clip.legacy ? true : (bool?)null,
                    settings = new { s.loopTime, s.loopBlend, s.cycleOffset, s.mirror, s.keepOriginalOrientation, s.keepOriginalPositionY, s.keepOriginalPositionXZ, s.loopBlendOrientation, s.loopBlendPositionY, s.loopBlendPositionXZ },
                    curves = AnimationUtility.GetCurveBindings(clip).Select(b =>
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, b);
                        return new
                        {
                            path = b.path, type = b.type.Name, property = b.propertyName,
                            keys = curve.length,
                            from = curve.length > 0 ? curve.keys[0].time : 0, to = curve.length > 0 ? curve.keys[curve.length - 1].time : 0,
                            values = keys ? curve.keys.Select(k => $"{ValueText.Format(k.time)}={ValueText.Format(k.value)}").ToArray() : null
                        };
                    }).ToArray(),
                    objectCurves = AnimationUtility.GetObjectReferenceCurveBindings(clip).Select(b => new
                    {
                        path = b.path, type = b.type.Name, property = b.propertyName,
                        keys = AnimationUtility.GetObjectReferenceCurve(clip, b).Select(k => $"{ValueText.Format(k.time)}={(k.value != null ? k.value.name : "null")}").ToArray()
                    }).ToArray(),
                    events = AnimationUtility.GetAnimationEvents(clip).Select(e => new { e.time, function = e.functionName, e.stringParameter, e.floatParameter, e.intParameter }).ToArray()
                });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
        }

        [CliGroupCommand(Group, "Edit an .anim clip key by key: set_key (path, type, property, time, value, tangent auto|linear|constant|free; adds or replaces the key at that time), remove_key (path, type, property, time), " +
                                "add_event (time in seconds, function, parameter), remove_event (time or function), set_settings (set=\"loopTime=true;loopBlend=true;cycleOffset=0.5\"; frameRate). Pipeline's animation.clip set_curve replaces whole curves. " +
                                "An op that fails saves nothing: the clip is left as it was.",
            Name = "edit_clip", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode EditClip(
            CommandContext cmd,
            [CliArg("path", "The .anim clip path or GUID", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!TryReadOps(cmd, ops, opsFile, out var list, out ExitCode usage)) return usage;
            AssetRollback rollback = null;
            try
            {
                var clip = ClipAt(path);
                if (AssetDatabase.IsSubAsset(clip)) throw new ArgumentException("That clip is imported from a model; change it with cliext.anim edit_clips on the model.");
                rollback = AssetRollback.Begin(new[] { AssetDatabase.GetAssetPath(clip) });
                var results = new List<object>();
                foreach (var op in list.ops)
                {
                    switch ((op.op ?? "").Trim().ToLowerInvariant())
                    {
                        case "set_key":
                        case "remove_key":
                        {
                            var binding = Binding(op);
                            var curve = AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve();
                            if (op.time < 0) throw new ArgumentException($"{op.op} needs a time.");
                            int existing = Array.FindIndex(curve.keys, k => Mathf.Abs(k.time - op.time) < 1e-4f);
                            if (op.op.Trim().ToLowerInvariant() == "remove_key")
                            {
                                if (existing < 0) throw new ArgumentException($"No key at {op.time}s on {op.property}.");
                                curve.RemoveKey(existing);
                            }
                            else
                            {
                                if (existing >= 0) curve.RemoveKey(existing);
                                int i = curve.AddKey(new Keyframe(op.time, op.value));
                                var mode = (op.tangent ?? "auto").ToLowerInvariant() switch
                                {
                                    "linear" => AnimationUtility.TangentMode.Linear,
                                    "constant" => AnimationUtility.TangentMode.Constant,
                                    "free" => AnimationUtility.TangentMode.Free,
                                    _ => AnimationUtility.TangentMode.ClampedAuto
                                };
                                AnimationUtility.SetKeyLeftTangentMode(curve, i, mode);
                                AnimationUtility.SetKeyRightTangentMode(curve, i, mode);
                            }
                            AnimationUtility.SetEditorCurve(clip, binding, curve.length > 0 ? curve : null);
                            results.Add(new { op = op.op, property = op.property, keys = curve.length });
                            break;
                        }
                        case "add_event":
                        {
                            var evs = AnimationUtility.GetAnimationEvents(clip).ToList();
                            var ev = ParseEvents($"{ValueText.Format(op.time)}:{op.function}{(string.IsNullOrEmpty(op.parameter) ? "" : ":" + op.parameter)}").First();
                            evs.Add(ev);
                            AnimationUtility.SetAnimationEvents(clip, evs.OrderBy(e => e.time).ToArray());
                            results.Add(new { op = "add_event", time = ev.time, function = ev.functionName });
                            break;
                        }
                        case "remove_event":
                        {
                            var evs = AnimationUtility.GetAnimationEvents(clip).ToList();
                            int before = evs.Count;
                            evs.RemoveAll(e => (op.time >= 0 && Mathf.Abs(e.time - op.time) < 1e-4f) || (!string.IsNullOrEmpty(op.function) && e.functionName == op.function));
                            AnimationUtility.SetAnimationEvents(clip, evs.ToArray());
                            results.Add(new { op = "remove_event", removed = before - evs.Count });
                            break;
                        }
                        case "set_settings":
                        {
                            var s = AnimationUtility.GetAnimationClipSettings(clip);
                            object boxed = s;
                            var done = AnimatorEditing.SetMembers(boxed, op.set);
                            AnimationUtility.SetAnimationClipSettings(clip, (AnimationClipSettings)boxed);
                            if (op.value > 0) clip.frameRate = op.value;
                            results.Add(new { op = "set_settings", set = done });
                            break;
                        }
                        default:
                            throw new ArgumentException($"Unknown op '{op.op}'. Ops: set_key, remove_key, add_event, remove_event, set_settings.");
                    }
                }
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssetIfDirty(clip);
                cmd.Result.Write(new { path, applied = results.Count, results, length = clip.length });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is IOException)
            {
                rollback?.Rollback();
                cmd.Log.Error(e.Message + (rollback != null ? " Nothing was saved." : ""));
                return ExitCode.Failed;
            }
            finally { rollback?.Dispose(); }
        }

        private static EditorCurveBinding Binding(ClipOp op)
        {
            if (string.IsNullOrWhiteSpace(op.property)) throw new ArgumentException("Keys need a property, such as m_LocalPosition.x.");
            string typeName = string.IsNullOrWhiteSpace(op.type) ? "Transform" : op.type.Trim();
            var type = TypeCache.GetTypesDerivedFrom<Component>().Concat(new[] { typeof(GameObject) })
                .FirstOrDefault(t => t.Name == typeName || t.FullName == typeName) ?? throw new ArgumentException($"No component type '{typeName}'.");
            return EditorCurveBinding.FloatCurve(op.path ?? "", type, op.property);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Avatar masks and fit checks

        [CliGroupCommand(Group, "Create or change an AvatarMask: --humanoid \"Head=false;LeftArm=false\" (body parts: Root, Body, Head, LeftLeg, RightLeg, LeftArm, RightArm, LeftFingers, RightFingers, LeftFootIK, RightFootIK, LeftHandIK, RightHandIK), " +
                                "--from a model to list its transforms, --transforms \"Armature/Hips/Spine=false\". Creates --path if it doesn't exist. Returns the mask.",
            Name = "mask", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Mask(
            CommandContext cmd,
            [CliArg("path", "Where the .mask asset is, or goes when it does not exist yet", Required = true)] string path,
            [CliArg("humanoid", "Body parts: Part=true|false;...")] string humanoid = "",
            [CliArg("from", "A model whose transform hierarchy the mask lists")] string from = "",
            [CliArg("transforms", "Transform paths: path=true|false;... (a trailing /* covers the children)")] string transforms = "")
        {
            try
            {
                var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
                bool created = false;
                if (mask == null)
                {
                    mask = new AvatarMask();
                    AssetDatabase.CreateAsset(mask, path.EndsWith(".mask") ? path : path + ".mask");
                    created = true;
                }
                Undo.RecordObject(mask, "cliext.anim mask");
                foreach (string pair in humanoid.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=');
                    var part = (AvatarMaskBodyPart)Enum.Parse(typeof(AvatarMaskBodyPart), kv[0].Trim(), true);
                    mask.SetHumanoidBodyPartActive(part, kv.Length < 2 || (bool)ValueText.Parse(kv[1], typeof(bool)));
                }
                if (!string.IsNullOrWhiteSpace(from))
                {
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(from) ?? throw new ArgumentException($"No model at '{from}'.");
                    var paths = model.GetComponentsInChildren<Transform>(true).Select(t => AnimationUtility.CalculateTransformPath(t, model.transform)).ToList();
                    mask.transformCount = paths.Count;
                    for (int i = 0; i < paths.Count; i++) { mask.SetTransformPath(i, paths[i]); mask.SetTransformActive(i, true); }
                }
                foreach (string pair in transforms.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.LastIndexOf('=');
                    string p = (eq > 0 ? pair.Substring(0, eq) : pair).Trim();
                    bool on = eq < 0 || (bool)ValueText.Parse(pair.Substring(eq + 1), typeof(bool));
                    bool children = p.EndsWith("/*");
                    if (children) p = p.Substring(0, p.Length - 2);
                    int hits = 0;
                    for (int i = 0; i < mask.transformCount; i++)
                    {
                        string tp = mask.GetTransformPath(i);
                        if (tp == p || (children && tp.StartsWith(p + "/"))) { mask.SetTransformActive(i, on); hits++; }
                    }
                    if (hits == 0) throw new ArgumentException($"The mask has no transform '{p}'" + (mask.transformCount == 0 ? "; pass --from a model first." : "."));
                }
                EditorUtility.SetDirty(mask);
                AssetDatabase.SaveAssetIfDirty(mask);
                cmd.Result.Write(new
                {
                    path = AssetDatabase.GetAssetPath(mask),
                    created,
                    humanoid = Enum.GetValues(typeof(AvatarMaskBodyPart)).Cast<AvatarMaskBodyPart>().Where(b => b != AvatarMaskBodyPart.LastBodyPart)
                        .ToDictionary(b => b.ToString(), b => mask.GetHumanoidBodyPartActive(b)).Where(kv => !kv.Value).Select(kv => kv.Key + " off").ToArray(),
                    transforms = mask.transformCount,
                    transformsOff = Enumerable.Range(0, mask.transformCount).Where(i => !mask.GetTransformActive(i)).Select(i => mask.GetTransformPath(i)).Take(50).ToArray()
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Check that clips fit a model: a humanoid clip on a generic or avatar-less model (or the reverse), an invalid or non-human avatar, and for generic clips the animated paths the model has no transform for. " +
                                "--clips is an AnimatorController (all its clips), clips, or models (their clips), separated by ';'. Exit 6 on a mismatch.",
            Name = "check", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Check(
            CommandContext cmd,
            [CliArg("model", "The model or prefab the clips play on", Required = true)] string model,
            [CliArg("clips", "Controller, clips or models, separated by ';'", Required = true)] string clips)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(model);
            if (go == null) { cmd.Log.Error($"No model or prefab at '{model}'."); return ExitCode.NotFound; }
            var animator = go.GetComponentInChildren<Animator>(true);
            var avatar = animator != null && animator.avatar != null ? animator.avatar : AssetDatabase.LoadAllAssetsAtPath(model).OfType<Avatar>().FirstOrDefault();
            var root = animator != null ? animator.transform : go.transform;
            var paths = new HashSet<string>(root.GetComponentsInChildren<Transform>(true).Select(t => AnimationUtility.CalculateTransformPath(t, root)));
            int problems = 0;
            var modelInfo = new { model, avatar = avatar != null ? avatar.name : null, valid = avatar != null && avatar.isValid, human = avatar != null && avatar.isHuman, transforms = paths.Count };
            if (avatar != null && !avatar.isValid) { problems++; cmd.Result.WriteRecord(new { problem = "The model's avatar isn't valid (check the rig's bone mapping).", modelInfo }); }

            var list = new List<AnimationClip>();
            foreach (string p in clips.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
            {
                var main = AssetDatabase.LoadMainAssetAtPath(p);
                if (main is AnimatorController ac) list.AddRange(ac.animationClips);
                else if (main is AnimatorOverrideController oc) list.AddRange(oc.animationClips);
                else if (main is AnimationClip c) list.Add(c);
                else list.AddRange(AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")));
            }
            foreach (var clip in list.Distinct())
            {
                var issues = new List<string>();
                if (clip.humanMotion && (avatar == null || !avatar.isHuman)) issues.Add("Humanoid clip, but the model has no humanoid avatar: it won't play (set the model's rig to Humanoid).");
                if (!clip.humanMotion && avatar != null && avatar.isHuman && AnimationUtility.GetCurveBindings(clip).Any(b => b.type == typeof(Transform)))
                    issues.Add("Generic clip on a humanoid model: it animates transforms by path, so it only works if the bone names and hierarchy match.");
                if (!clip.humanMotion)
                {
                    var bindings = AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)).ToList();
                    var missing = bindings.Select(b => b.path).Distinct().Where(p => !paths.Contains(p)).ToList();
                    if (missing.Count > 0) issues.Add($"{missing.Count} animated path(s) not on the model: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : "")}");
                    // The path exists, but the component the curve drives isn't there (a SpriteRenderer curve on a 3D model).
                    var noComponent = bindings.Where(b => paths.Contains(b.path) && typeof(Component).IsAssignableFrom(b.type) && b.type != typeof(Transform))
                        .Where(b => { var t = b.path.Length == 0 ? root : root.Find(b.path); return t != null && t.GetComponent(b.type) == null; })
                        .Select(b => $"{b.type.Name} on '{(b.path.Length == 0 ? "(root)" : b.path)}'").Distinct().ToList();
                    if (noComponent.Count > 0) issues.Add($"Curves for components the model doesn't have: {string.Join(", ", noComponent.Take(6))}");
                }
                if (clip.legacy) issues.Add("Legacy clip: an Animator can't play it.");
                if (issues.Count > 0) problems++;
                cmd.Result.WriteRecord(new { clip = clip.name, asset = AssetDatabase.GetAssetPath(clip), humanoid = clip.humanMotion, ok = issues.Count == 0, issues = issues.Count > 0 ? issues.ToArray() : null });
            }
            cmd.Log.Info($"Model: {(modelInfo.human ? "humanoid" : "generic")} avatar {(modelInfo.avatar ?? "none")}; {list.Count} clip(s), {problems} with problems.");
            return problems > 0 ? ExitCode.Failed : ExitCode.Success;
        }
    }
}

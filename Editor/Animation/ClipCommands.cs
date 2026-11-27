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
        /// <summary>Seconds (a number; read as text so a bad or missing one fails instead of reading as 0).</summary>
        public string time;
        /// <summary>For set_key: the key's value; for set_settings: the frame rate.</summary>
        public string value;
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

        private static string FromGuid(string path) => AnimationErrors.AssetPath(path);

        private const string HowToGetAModel = "Import the model file (.fbx, ...) into Assets/ first, or pass an existing model's path or GUID.";

        private static ModelImporter ModelAt(string path)
        {
            string p = FromGuid(path);
            if (AssetImporter.GetAtPath(p) is ModelImporter m) return m;
            var main = p.Length > 0 ? AssetDatabase.LoadMainAssetAtPath(p) : null;
            if (main == null) throw new AnimNotFoundException($"No model at '{path}'. {HowToGetAModel}");
            throw new AnimUsageException($"'{path}' is {AnimationErrors.A(main.GetType().Name)}, not a model (.fbx, ...).");
        }

        private static float? Number(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return (float)ValueText.Parse(text, typeof(float)); }
            catch (ArgumentException) { throw new ArgumentException($"{what} '{text}' isn't a number: give one such as 0.5."); }
        }

        private static string ClipHint(string name, IEnumerable<ModelImporterClipAnimation> clips) =>
            AnimationErrors.Hint(name, clips.Select(c => c.name), "Clips", "cliext.anim takes");

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
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); failure = ExitCode.InvalidUsage; return false; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given."); failure = ExitCode.InvalidUsage; return false; }
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
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
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
            ModelImporter m;
            try { m = ModelAt(path); }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            int index = 0;
            try
            {
                var clips = (m.clipAnimations.Length > 0 ? m.clipAnimations : m.defaultClipAnimations).ToList();
                ModelImporterClipAnimation Find(string name)
                {
                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The op needs name: the clip's name in the model's clip list.");
                    return clips.FirstOrDefault(c => string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase))
                           ?? throw new ArgumentException($"No clip '{name}'." + ClipHint(name, clips));
                }
                void CheckRange(ModelImporterClipAnimation c)
                {
                    if (c.firstFrame < 0 || c.lastFrame <= c.firstFrame)
                        throw new ArgumentException($"Clip '{c.name}' would run from frame {ValueText.Format(c.firstFrame)} to {ValueText.Format(c.lastFrame)}: first must be 0 or more and last after it.");
                }
                var results = new List<object>();
                for (index = 0; index < list.ops.Length; index++)
                {
                    var op = list.ops[index];
                    switch ((op.op ?? "").Trim().ToLowerInvariant())
                    {
                        case "add_clip":
                        {
                            if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("add_clip needs a name.");
                            if (clips.Any(c => string.Equals(c.name, op.name.Trim(), StringComparison.OrdinalIgnoreCase)))
                                throw new ArgumentException($"The model already has a clip '{op.name.Trim()}'. Change it with set_clip, or pick another name.");
                            var takes = m.importedTakeInfos ?? Array.Empty<TakeInfo>();
                            if (takes.Length == 0) throw new ArgumentException("The model has no animation takes to cut a clip from (cliext.anim takes shows them).");
                            var take = string.IsNullOrEmpty(op.take) ? takes.FirstOrDefault() : takes.FirstOrDefault(t => string.Equals(t.name, op.take, StringComparison.OrdinalIgnoreCase));
                            if (take.name == null) throw new ArgumentException($"No take '{op.take}'." + AnimationErrors.Hint(op.take, takes.Select(t => t.name), "Takes", "cliext.anim takes"));
                            var c = new ModelImporterClipAnimation
                            {
                                name = op.name.Trim(), takeName = take.name,
                                firstFrame = op.first >= 0 ? op.first : (float)Math.Round(take.startTime * take.sampleRate),
                                lastFrame = op.last >= 0 ? op.last : (float)Math.Round(take.stopTime * take.sampleRate)
                            };
                            // A new ModelImporterClipAnimation's events getter throws until they are set.
                            c.events = Array.Empty<AnimationEvent>();
                            AnimatorEditing.SetMembers(c, op.set);
                            CheckRange(c);
                            clips.Add(c);
                            results.Add(new { op = "add_clip", clip = DescribeModelClip(c) });
                            break;
                        }
                        case "set_clip":
                        {
                            var c = Find(op.name);
                            if (op.first < 0 && op.last < 0 && string.IsNullOrWhiteSpace(op.set))
                                throw new ArgumentException("set_clip needs first, last or set=\"loopTime=true;...\".");
                            if (op.first >= 0) c.firstFrame = op.first;
                            if (op.last >= 0) c.lastFrame = op.last;
                            AnimatorEditing.SetMembers(c, op.set);
                            CheckRange(c);
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
                            if (op.events == null) throw new ArgumentException("set_events needs events=\"0.5:Footstep;...\" (normalized times; \"\" removes them all).");
                            var evs = ParseEvents(op.events).ToArray();
                            foreach (var ev in evs)
                                if (ev.time < 0 || ev.time > 1) throw new ArgumentException($"Event time {ValueText.Format(ev.time)} ({ev.functionName}) is outside the clip: model clip events use normalized times from 0 to 1.");
                            c.events = evs;
                            results.Add(new { op = "set_events", name = op.name, events = c.events.Length });
                            break;
                        }
                        case "set_mask":
                        {
                            var c = Find(op.name);
                            if (string.IsNullOrEmpty(op.mask)) throw new ArgumentException("set_mask needs mask: an AvatarMask path, or none to clear it.");
                            if (op.mask == "none") { c.maskType = ClipAnimationMaskType.None; c.maskSource = null; }
                            else
                            {
                                c.maskType = ClipAnimationMaskType.CopyFromOther;
                                c.maskSource = AssetDatabase.LoadAssetAtPath<AvatarMask>(op.mask) ?? throw new ArgumentException($"No AvatarMask at '{op.mask}'. Create one with cliext.anim mask --path {op.mask}.");
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
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is IOException)
            {
                string message = AnimationErrors.Message(e);
                cmd.Result.Write(new { path, applied = 0, failedAt = index, error = message, saved = false });
                cmd.Fail($"Op {index} ({list.ops[Math.Min(index, list.ops.Length - 1)].op}) failed: {message} Nothing was saved.");
                return ExitCode.Failed;
            }
        }

        private static IEnumerable<AnimationEvent> ParseEvents(string events)
        {
            foreach (string e in (events ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = e.Split(':');
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) throw new ArgumentException($"Event '{e}' should be time:function[:parameter].");
                float time = Number(parts[0], $"Event '{e}': the time") ?? throw new ArgumentException($"Event '{e}' needs a time: time:function[:parameter].");
                var ev = new AnimationEvent { time = time, functionName = parts[1].Trim() };
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
            string given = path;
            path = FromGuid(path);
            int at = path.LastIndexOf('@');
            if (at > 0 && !File.Exists(path))
            {
                string model = path.Substring(0, at), name = path.Substring(at + 1);
                if (AssetDatabase.LoadMainAssetAtPath(model) == null) throw new AnimNotFoundException($"No model at '{model}'. {HowToGetAModel}");
                var clips = AnimationErrors.ModelClips(model);
                return AnimationErrors.ModelClip(model, name)
                       ?? throw new AnimNotFoundException($"No clip '{name}' in '{model}'." + AnimationErrors.Hint(name, clips.Select(c => c.name), "Clips", "cliext.anim takes"));
            }
            AnimationErrors.RequireType(path, "AnimationClip", typeof(AnimationClip),
                $"Create one with animation.clip create --path {(path.Length > 0 ? path : "Assets/Name.anim")}, or give a model's clip as Assets/Model.fbx@Clip.", given);
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
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
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        [CliGroupCommand(Group, "Edit an .anim clip key by key: set_key (path, type, property, time, value, tangent auto|linear|constant|free; adds or replaces the key at that time), remove_key (path, type, property, time), " +
                                "add_event (time in seconds, function, parameter), remove_event (time, function or both), set_settings (set=\"loopTime=true;loopBlend=true;cycleOffset=0.5\"; value: a new frame rate). Pipeline's animation.clip set_curve replaces whole curves. " +
                                "An op that fails saves nothing: the clip is left as it was.",
            Name = "edit_clip", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode EditClip(
            CommandContext cmd,
            [CliArg("path", "The .anim clip path or GUID", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!TryReadOps(cmd, ops, opsFile, out var list, out ExitCode usage)) return usage;
            AnimationClip clip;
            try
            {
                clip = ClipAt(path);
                if (AssetDatabase.IsSubAsset(clip)) throw new AnimUsageException("That clip is imported from a model; change it with cliext.anim edit_clips on the model.");
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            var results = new List<object>();
            if (!TryApplyClipOps(clip, list.ops, results, out int failedAt, out string error))
            {
                cmd.Result.Write(new { path, applied = 0, failedAt, error, saved = false });
                cmd.Fail($"Op {failedAt} ({list.ops[failedAt].op}) failed: {error} Nothing was saved.");
                return ExitCode.Failed;
            }
            cmd.Result.Write(new { path, applied = results.Count, results, length = clip.length });
            return ExitCode.Success;
        }

        /// <summary>
        /// Applies every op to an .anim clip and saves it once, or, when one fails, puts the clip back as it was on disk
        /// and in memory and saves nothing.
        /// </summary>
        internal static bool TryApplyClipOps(AnimationClip clip, IList<ClipOp> ops, List<object> results, out int failedAt, out string error)
        {
            failedAt = -1;
            error = null;
            using var rollback = AssetRollback.Begin(new[] { AssetDatabase.GetAssetPath(clip) }, revertUndo: true);
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(ApplyClipOp(clip, ops[i])); }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException || e is IOException)
                {
                    failedAt = i;
                    error = AnimationErrors.Message(e);
                    results.Clear();
                    rollback.Rollback();
                    return false;
                }
            }
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);
            return true;
        }

        private static object ApplyClipOp(AnimationClip clip, ClipOp op)
        {
            string name = (op.op ?? "").Trim().ToLowerInvariant();
            switch (name)
            {
                case "set_key":
                case "remove_key":
                {
                    var binding = Binding(op);
                    float time = Number(op.time, $"{name} time") ?? throw new ArgumentException($"{name} needs a time (seconds).");
                    if (time < 0) throw new ArgumentException($"{name} time {ValueText.Format(time)} is before the clip starts: give 0 or more.");
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    int existing = curve == null ? -1 : Array.FindIndex(curve.keys, k => Mathf.Abs(k.time - time) < 1e-4f);
                    if (name == "remove_key")
                    {
                        if (curve == null) throw new ArgumentException($"The clip has no curve '{op.property}' on {binding.type.Name} at '{binding.path}'." + CurveHint(clip));
                        if (existing < 0) throw new ArgumentException($"No key at {ValueText.Format(time)}s on {op.property}. Its keys are at: {string.Join(", ", curve.keys.Select(k => ValueText.Format(k.time)))} (cliext.anim clip --keys true lists them).");
                        curve.RemoveKey(existing);
                    }
                    else
                    {
                        float value = Number(op.value, "set_key value") ?? throw new ArgumentException("set_key needs a value (a number).");
                        var mode = (op.tangent ?? "auto").Trim().ToLowerInvariant() switch
                        {
                            "auto" => AnimationUtility.TangentMode.ClampedAuto,
                            "linear" => AnimationUtility.TangentMode.Linear,
                            "constant" => AnimationUtility.TangentMode.Constant,
                            "free" => AnimationUtility.TangentMode.Free,
                            _ => throw new ArgumentException($"'{op.tangent}' isn't a tangent. Use one of: auto, linear, constant, free.")
                        };
                        curve ??= new AnimationCurve();
                        if (existing >= 0) curve.RemoveKey(existing);
                        int i = curve.AddKey(new Keyframe(time, value));
                        AnimationUtility.SetKeyLeftTangentMode(curve, i, mode);
                        AnimationUtility.SetKeyRightTangentMode(curve, i, mode);
                    }
                    AnimationUtility.SetEditorCurve(clip, binding, curve.length > 0 ? curve : null);
                    return new { op = name, property = op.property, keys = curve.length };
                }
                case "add_event":
                {
                    if (string.IsNullOrWhiteSpace(op.function)) throw new ArgumentException("add_event needs a function (the method the event calls).");
                    float time = Number(op.time, "add_event time") ?? throw new ArgumentException("add_event needs a time (seconds).");
                    if (time < 0) throw new ArgumentException($"add_event time {ValueText.Format(time)} is before the clip starts: give 0 or more.");
                    var evs = AnimationUtility.GetAnimationEvents(clip).ToList();
                    var ev = ParseEvents($"{ValueText.Format(time)}:{op.function.Trim()}{(string.IsNullOrEmpty(op.parameter) ? "" : ":" + op.parameter)}").First();
                    evs.Add(ev);
                    AnimationUtility.SetAnimationEvents(clip, evs.OrderBy(e => e.time).ToArray());
                    return new { op = "add_event", time = ev.time, function = ev.functionName };
                }
                case "remove_event":
                {
                    float? time = Number(op.time, "remove_event time");
                    if (time == null && string.IsNullOrEmpty(op.function)) throw new ArgumentException("remove_event needs a time or a function.");
                    var evs = AnimationUtility.GetAnimationEvents(clip).ToList();
                    int before = evs.Count;
                    evs.RemoveAll(e => (time == null || Mathf.Abs(e.time - time.Value) < 1e-4f) && (string.IsNullOrEmpty(op.function) || e.functionName == op.function));
                    if (evs.Count == before)
                        throw new ArgumentException($"No event{(time != null ? $" at {ValueText.Format(time.Value)}s" : "")}{(string.IsNullOrEmpty(op.function) ? "" : $" calling {op.function}")}. " +
                                                    (before == 0 ? "The clip has no events." : $"Events: {string.Join(", ", AnimationUtility.GetAnimationEvents(clip).Select(e => $"{ValueText.Format(e.time)}:{e.functionName}"))}."));
                    AnimationUtility.SetAnimationEvents(clip, evs.ToArray());
                    return new { op = "remove_event", removed = before - evs.Count };
                }
                case "set_settings":
                {
                    float? frameRate = Number(op.value, "frameRate (value)");
                    if (string.IsNullOrWhiteSpace(op.set) && frameRate == null) throw new ArgumentException("set_settings needs set=\"loopTime=true;...\" or value (the frame rate).");
                    if (frameRate != null && frameRate <= 0) throw new ArgumentException($"Frame rate {ValueText.Format(frameRate.Value)} must be more than 0.");
                    var s = AnimationUtility.GetAnimationClipSettings(clip);
                    object boxed = s;
                    var done = AnimatorEditing.SetMembers(boxed, op.set);
                    AnimationUtility.SetAnimationClipSettings(clip, (AnimationClipSettings)boxed);
                    if (frameRate != null) clip.frameRate = frameRate.Value;
                    return new { op = "set_settings", set = done, frameRate = clip.frameRate };
                }
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: set_key, remove_key, add_event, remove_event, set_settings.");
            }
        }

        private static string CurveHint(AnimationClip clip)
        {
            var b = AnimationUtility.GetCurveBindings(clip);
            return b.Length == 0 ? " The clip has no curves." : $" Curves: {string.Join(", ", b.Take(12).Select(x => $"{(x.path.Length > 0 ? x.path + ":" : "")}{x.type.Name}.{x.propertyName}"))}{(b.Length > 12 ? ", ..." : "")} (cliext.anim clip lists them).";
        }

        private static EditorCurveBinding Binding(ClipOp op)
        {
            if (string.IsNullOrWhiteSpace(op.property)) throw new ArgumentException("Keys need a property, such as m_LocalPosition.x.");
            string typeName = string.IsNullOrWhiteSpace(op.type) ? "Transform" : op.type.Trim();
            var types = TypeCache.GetTypesDerivedFrom<Component>().Concat(new[] { typeof(GameObject), typeof(Component) }).ToList();
            var type = types.FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
            if (type == null)
            {
                var close = AnimationErrors.Close(typeName, types.Where(t => t.IsPublic).Select(t => t.Name).Distinct());
                throw new ArgumentException($"No component type '{typeName}'." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : " Give a component's class name, such as Transform or SpriteRenderer (cliext.anim clip lists a clip's curve types)."));
            }
            return EditorCurveBinding.FloatCurve(op.path ?? "", type, op.property);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Avatar masks and fit checks

        [CliGroupCommand(Group, "Create or change an AvatarMask: --humanoid \"Head=false;LeftArm=false\" (body parts: Root, Body, Head, LeftLeg, RightLeg, LeftArm, RightArm, LeftFingers, RightFingers, LeftFootIK, RightFootIK, LeftHandIK, RightHandIK), " +
                                "--from a model to list its transforms, --transforms \"Armature/Hips/Spine=false\". Creates --path (under Assets/ or Packages/; .mask is added) if it doesn't exist; with none of the three on an existing mask it only reads it. " +
                                "Returns the mask. Anything that fails changes nothing (a new mask isn't made).",
            Name = "mask", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Mask(
            CommandContext cmd,
            [CliArg("path", "Where the .mask asset is, or goes when it does not exist yet", Required = true)] string path,
            [CliArg("humanoid", "Body parts: Part=true|false;...")] string humanoid = "",
            [CliArg("from", "A model whose transform hierarchy the mask lists")] string from = "",
            [CliArg("transforms", "Transform paths: path=true|false;... (path/* sets the path and everything under it)")] string transforms = "")
        {
            AvatarMask work = null;
            try
            {
                string p = AnimationErrors.AssetPath(path);
                if (!p.StartsWith("Assets/") && !p.StartsWith("Packages/"))
                    throw new AnimUsageException($"'{path}' isn't under Assets/ (or Packages/): pass a project path such as Assets/Animation/UpperBody.mask.");
                string ext = System.IO.Path.GetExtension(p);
                if (ext.Length > 0 && !ext.Equals(".mask", StringComparison.OrdinalIgnoreCase))
                {
                    var there = AssetDatabase.LoadMainAssetAtPath(p);
                    throw new AnimUsageException(there != null ? $"'{path}' is {AnimationErrors.A(there.GetType().Name)}, not an AvatarMask." : $"'{path}' isn't a .mask path: give one such as Assets/Animation/UpperBody.mask.");
                }
                if (ext.Length == 0) p += ".mask";
                var existing = AssetDatabase.LoadAssetAtPath<AvatarMask>(p);
                if (existing == null && AssetDatabase.LoadMainAssetAtPath(p) != null)
                    throw new AnimUsageException($"'{p}' is {AnimationErrors.A(AssetDatabase.LoadMainAssetAtPath(p).GetType().Name)}, not an AvatarMask.");
                bool change = !string.IsNullOrWhiteSpace(humanoid) || !string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(transforms);

                // Changes go to a copy first, so a bad body part or transform leaves the mask (or the folder) as it was.
                work = existing != null ? UnityEngine.Object.Instantiate(existing) : new AvatarMask();
                foreach (string pair in humanoid.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=');
                    var part = AnimationErrors.ParseEnum<AvatarMaskBodyPart>(kv[0].Trim(), "body part", nameof(AvatarMaskBodyPart.LastBodyPart));
                    bool on;
                    try { on = kv.Length < 2 || (bool)ValueText.Parse(kv[1], typeof(bool)); }
                    catch (ArgumentException e) { throw new AnimUsageException($"--humanoid {kv[0].Trim()}: {e.Message}"); }
                    work.SetHumanoidBodyPartActive(part, on);
                }
                if (!string.IsNullOrWhiteSpace(from))
                {
                    string fromPath = AnimationErrors.AssetPath(from);
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(fromPath);
                    if (model == null)
                    {
                        if (AssetDatabase.LoadMainAssetAtPath(fromPath) == null) throw new AnimNotFoundException($"No model at '{from}' for --from. {HowToGetAModel}");
                        throw new AnimUsageException($"--from '{from}' isn't a model or prefab.");
                    }
                    var paths = model.GetComponentsInChildren<Transform>(true).Select(t => AnimationUtility.CalculateTransformPath(t, model.transform)).ToList();
                    work.transformCount = paths.Count;
                    for (int i = 0; i < paths.Count; i++) { work.SetTransformPath(i, paths[i]); work.SetTransformActive(i, true); }
                }
                foreach (string pair in transforms.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.LastIndexOf('=');
                    string tpath = (eq > 0 ? pair.Substring(0, eq) : pair).Trim();
                    bool on;
                    try { on = eq < 0 || (bool)ValueText.Parse(pair.Substring(eq + 1), typeof(bool)); }
                    catch (ArgumentException e) { throw new AnimUsageException($"--transforms {tpath}: {e.Message}"); }
                    bool children = tpath.EndsWith("/*");
                    if (children) tpath = tpath.Substring(0, tpath.Length - 2);
                    int hits = 0;
                    for (int i = 0; i < work.transformCount; i++)
                    {
                        string tp = work.GetTransformPath(i);
                        if (tp == tpath || (children && tp.StartsWith(tpath + "/"))) { work.SetTransformActive(i, on); hits++; }
                    }
                    if (hits == 0)
                    {
                        var known = Enumerable.Range(0, work.transformCount).Select(i => work.GetTransformPath(i)).ToList();
                        throw new ArgumentException($"The mask has no transform '{tpath}'" + (work.transformCount == 0 ? ": pass --from a model to list its transforms first." :
                            "." + AnimationErrors.Hint(tpath, known, "Transforms", "cliext.anim mask --path " + p + " (transformsOff) or the model's hierarchy")));
                    }
                }

                AvatarMask mask = existing;
                bool created = false;
                if (existing == null)
                {
                    // Missing folders are made (one already on disk is imported, not made again as "<Folder> 1").
                    string dir = System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir)) ProjectFolders.Ensure(dir);
                    AssetDatabase.CreateAsset(work, p);
                    mask = work;
                    work = null;
                    created = true;
                }
                else if (change)
                {
                    Undo.RecordObject(existing, "cliext.anim mask");
                    EditorUtility.CopySerialized(work, existing);
                    existing.name = System.IO.Path.GetFileNameWithoutExtension(p);
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssetIfDirty(existing);
                }
                cmd.Result.Write(new
                {
                    path = AssetDatabase.GetAssetPath(mask),
                    created,
                    changed = created || change,
                    humanoid = Enum.GetValues(typeof(AvatarMaskBodyPart)).Cast<AvatarMaskBodyPart>().Where(b => b != AvatarMaskBodyPart.LastBodyPart)
                        .ToDictionary(b => b.ToString(), b => mask.GetHumanoidBodyPartActive(b)).Where(kv => !kv.Value).Select(kv => kv.Key + " off").ToArray(),
                    transforms = mask.transformCount,
                    transformsOff = Enumerable.Range(0, mask.transformCount).Where(i => !mask.GetTransformActive(i)).Select(i => mask.GetTransformPath(i)).Take(50).ToArray()
                });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            finally { if (work != null) UnityEngine.Object.DestroyImmediate(work); }
        }

        [CliGroupCommand(Group, "Check that clips fit a model: a humanoid clip on a generic or avatar-less model (or the reverse), an invalid or non-human avatar, and for generic clips the animated paths the model has no transform for. " +
                                "--clips is an AnimatorController (all its clips), clips, or models (their clips), separated by ';'. Exit 6 on a mismatch, 13 when the model or a --clips entry doesn't exist.",
            Name = "check", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Check(
            CommandContext cmd,
            [CliArg("model", "The model or prefab the clips play on", Required = true)] string model,
            [CliArg("clips", "Controller, clips or models, separated by ';'", Required = true)] string clips)
        {
            string modelPath = AnimationErrors.AssetPath(model);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (go == null)
            {
                var other = modelPath.Length > 0 ? AssetDatabase.LoadMainAssetAtPath(modelPath) : null;
                if (other == null) { cmd.Fail($"No model or prefab at '{model}'. {HowToGetAModel}"); return ExitCode.NotFound; }
                cmd.Fail($"--model '{model}' is {AnimationErrors.A(other.GetType().Name)}, not a model or prefab."); return ExitCode.InvalidUsage;
            }

            // Every --clips entry resolves before anything is reported.
            var list = new List<AnimationClip>();
            foreach (string entry in clips.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
            {
                string p = AnimationErrors.AssetPath(entry);
                var main = AssetDatabase.LoadMainAssetAtPath(p);
                if (main == null)
                {
                    int at = p.LastIndexOf('@');
                    AnimationClip sub;
                    try { sub = at > 0 && AssetDatabase.LoadMainAssetAtPath(p.Substring(0, at)) != null ? AnimationErrors.ModelClip(p.Substring(0, at), p.Substring(at + 1)) : null; }
                    catch (ClipReferenceException e) { cmd.Fail($"--clips: {e.Message}"); return ExitCode.InvalidUsage; }
                    if (sub == null) { cmd.Fail($"--clips: nothing at '{entry}'. Give an AnimatorController, an .anim, a model or Model.fbx@Clip that exists."); return ExitCode.NotFound; }
                    list.Add(sub);
                    continue;
                }
                var found = main is AnimatorController ac ? ac.animationClips
                          : main is AnimatorOverrideController oc ? oc.animationClips
                          : main is AnimationClip c ? new[] { c }
                          : AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().Where(x => !x.name.StartsWith("__preview__")).ToArray();
                if (found.Length == 0) { cmd.Fail($"--clips: '{entry}' ({main.GetType().Name}) has no animation clips to check."); return ExitCode.Failed; }
                list.AddRange(found);
            }
            if (list.Count == 0) { cmd.Fail("--clips names nothing to check."); return ExitCode.InvalidUsage; }

            var animator = go.GetComponentInChildren<Animator>(true);
            var avatar = animator != null && animator.avatar != null ? animator.avatar : AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            var root = animator != null ? animator.transform : go.transform;
            var paths = new HashSet<string>(root.GetComponentsInChildren<Transform>(true).Select(t => AnimationUtility.CalculateTransformPath(t, root)));
            int problems = 0;
            var modelInfo = new { model, avatar = avatar != null ? avatar.name : null, valid = avatar != null && avatar.isValid, human = avatar != null && avatar.isHuman, transforms = paths.Count };
            if (avatar != null && !avatar.isValid) { problems++; cmd.Result.WriteRecord(new { problem = "The model's avatar isn't valid (check the rig's bone mapping).", modelInfo }); }

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
            cmd.Log.Info($"Model: {(modelInfo.human ? "humanoid" : "generic")} avatar {(modelInfo.avatar ?? "none")}; {list.Distinct().Count()} clip(s), {problems} with problems.");
            if (problems > 0) { cmd.Fail($"{problems} problem(s): the records with ok false (or a problem) say which clips don't fit the model and why."); return ExitCode.Failed; }
            return ExitCode.Success;
        }
    }
}

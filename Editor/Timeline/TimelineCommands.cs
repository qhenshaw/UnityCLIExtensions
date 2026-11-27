using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace UnityCLIExtensions.Timeline
{
    [Serializable]
    internal class TimelineOp
    {
        /// <summary>set_clip, move_clip, remove_clip, remove_track, rename_track, mute_track, lock_track, add_marker, remove_marker.</summary>
        public string op;
        /// <summary>The track: its name, or a path through groups (Group/Track), or its index.</summary>
        public string track;
        /// <summary>The clip: its display name or index on the track.</summary>
        public string clip;
        public string name;
        /// <summary>Clip settings "start=1;duration=2;clipIn=0;easeIn=0.25;easeOut=0.25;timeScale=1;blendIn=0;blendOut=0".</summary>
        public string set;
        /// <summary>For move_clip: the destination track. For mute/lock: true/false.</summary>
        public string to;
        public string value;
        /// <summary>For add_marker/remove_marker: the time in seconds.</summary>
        public double time = -1;
        /// <summary>For add_marker: a SignalAsset path (a SignalEmitter), or empty for a plain marker on the timeline's marker track.</summary>
        public string signal;
    }

    [Serializable]
    internal class TimelineOpList
    {
        public TimelineOp[] ops;
    }

    /// <summary>
    /// <c>cliext.timeline</c>: what Pipeline's animation.timeline commands leave out: clip timing and easing, moving and
    /// removing clips and tracks, mute and lock, markers and signals, track bindings on a PlayableDirector, and
    /// scrubbing, playing and reading a director.
    /// </summary>
    internal static class TimelineCommands
    {
        private const string Group = "cliext.timeline";

        private static TimelineAsset Load(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            return AssetDatabase.LoadAssetAtPath<TimelineAsset>(path) ?? throw new ArgumentException($"No TimelineAsset at '{path}'.");
        }

        private static IEnumerable<(string path, TrackAsset track)> Tracks(TimelineAsset t)
        {
            IEnumerable<(string, TrackAsset)> Walk(IEnumerable<TrackAsset> tracks, string prefix)
            {
                foreach (var tr in tracks)
                {
                    yield return (prefix + tr.name, tr);
                    foreach (var x in Walk(tr.GetChildTracks(), prefix + tr.name + "/")) yield return x;
                }
            }
            return Walk(t.GetRootTracks(), "");
        }

        private static TrackAsset FindTrack(TimelineAsset t, string track)
        {
            var all = Tracks(t).ToList();
            if (int.TryParse(track, out int i) && i >= 0 && i < all.Count) return all[i].track;
            return all.FirstOrDefault(x => string.Equals(x.path, track, StringComparison.OrdinalIgnoreCase)).track
                   ?? all.FirstOrDefault(x => string.Equals(x.track.name, track, StringComparison.OrdinalIgnoreCase)).track
                   ?? throw new ArgumentException($"No track '{track}'. Tracks: {string.Join(", ", all.Select(x => x.path))}");
        }

        private static TimelineClip FindClip(TrackAsset track, string clip)
        {
            var clips = track.GetClips().ToList();
            if (int.TryParse(clip, out int i) && i >= 0 && i < clips.Count) return clips[i];
            return clips.FirstOrDefault(c => string.Equals(c.displayName, clip, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No clip '{clip}' on {track.name}. Clips: {string.Join(", ", clips.Select((c, n) => $"{n} {c.displayName}"))}");
        }

        private static object DescribeClip(TimelineClip c, int index) => new
        {
            index,
            name = c.displayName,
            asset = c.asset != null ? c.asset.GetType().Name : null,
            start = Math.Round(c.start, 4),
            duration = Math.Round(c.duration, 4),
            end = Math.Round(c.end, 4),
            clipIn = c.clipIn,
            timeScale = c.timeScale,
            easeIn = c.easeInDuration,
            easeOut = c.easeOutDuration,
            blendIn = c.blendInDuration > 0 ? c.blendInDuration : (double?)null,
            blendOut = c.blendOutDuration > 0 ? c.blendOutDuration : (double?)null,
            animationClip = c.animationClip != null ? c.animationClip.name : null
        };

        [CliGroupCommand(Group, "Describe a TimelineAsset in full: every track by path with its type, mute and lock, its clips with start, duration, clip-in, speed, ease and blend, and its markers; " +
                                "with --director, also what each track is bound to.",
            Name = "inspect", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(
            CommandContext cmd,
            [CliArg("path", "The .playable timeline (or leave it out and pass --director)")] string path = "",
            [CliArg("director", "A GameObject with a PlayableDirector")] string director = "")
        {
            try
            {
                PlayableDirector d = string.IsNullOrWhiteSpace(director) ? null : FindDirector(director);
                var t = d != null && string.IsNullOrWhiteSpace(path) ? d.playableAsset as TimelineAsset ?? throw new ArgumentException($"{d.name}'s director doesn't play a timeline.") : Load(path);
                cmd.Result.Write(new
                {
                    path = AssetDatabase.GetAssetPath(t),
                    duration = t.duration,
                    frameRate = t.editorSettings.frameRate,
                    markers = t.markerTrack != null ? t.markerTrack.GetMarkers().Select(DescribeMarker).ToArray() : null,
                    tracks = Tracks(t).Select(x => new
                    {
                        x.path,
                        type = x.track.GetType().Name,
                        muted = x.track.muted ? true : (bool?)null,
                        locked = x.track.locked ? true : (bool?)null,
                        boundTo = d != null ? Describe(d.GetGenericBinding(x.track)) : null,
                        clips = x.track.GetClips().Select((c, i) => DescribeClip(c, i)).ToArray(),
                        markers = x.track.GetMarkers().Select(DescribeMarker).ToArray()
                    }).ToArray()
                });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.NotFound; }
        }

        private static object DescribeMarker(IMarker m) => new
        {
            time = m.time,
            type = m.GetType().Name,
            signal = m is SignalEmitter se && se.asset != null ? AssetDatabase.GetAssetPath(se.asset) : null
        };

        private static string Describe(UnityEngine.Object o) => o == null ? null : o is Component c ? $"{c.gameObject.name} ({c.GetType().Name})" : o.name;

        [CliGroupCommand(Group, "Edit a TimelineAsset with a JSON list of ops, saved once: add_clip (track, clip=asset path or model.fbx@take, time, set=\"duration=2\"; no clip for a track's default clip), set_clip (track, clip, set=\"start=1;duration=2;clipIn=0;easeIn=0.25;easeOut=0.25;timeScale=1\", name), " +
                                "move_clip (to another track), remove_clip, remove_track, rename_track, mute_track and lock_track (value true/false), add_marker (time, on a track or the timeline's markers; signal=SignalAsset path), remove_marker (time). " +
                                "Pipeline's animation.timeline commands create timelines and add tracks and clips. An op that fails saves nothing: the timeline is left as it was.",
            Name = "edit", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "The .playable timeline", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            if (!OpsInput.TryRead(cmd, ops, opsFile, out string json, out ExitCode usage)) return usage;
            TimelineOpList list;
            try { string s = json.Trim(); list = JsonUtility.FromJson<TimelineOpList>(s.StartsWith("[") ? "{\"ops\":" + s + "}" : s); }
            catch (Exception e) { cmd.Fail($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Fail("No ops given."); return ExitCode.InvalidUsage; }

            TimelineAsset t;
            try { t = Load(path); } catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.NotFound; }
            using var rollback = AssetRollback.Begin(new[] { AssetDatabase.GetAssetPath(t) });
            var results = new List<object>();
            for (int i = 0; i < list.ops.Length; i++)
            {
                try { results.Add(Apply(t, list.ops[i])); }
                catch (Exception e)
                {
                    string message = e is ArgumentException || e is FormatException ? e.Message : e.ToString();
                    rollback.Rollback();
                    cmd.Result.Write(new { path, applied = 0, failedAt = i, error = message, saved = false });
                    cmd.Fail($"Op {i} ({list.ops[i].op}) failed: {message} Nothing was saved.");
                    return ExitCode.Failed;
                }
            }
            Save(t);
            cmd.Result.Write(new { path, applied = results.Count, results, duration = t.duration });
            return ExitCode.Success;
        }

        private static void Save(TimelineAsset t)
        {
            EditorUtility.SetDirty(t);
            foreach (var (_, tr) in Tracks(t)) EditorUtility.SetDirty(tr);
            AssetDatabase.SaveAssets();
        }

        private static object Apply(TimelineAsset t, TimelineOp op)
        {
            switch ((op.op ?? "").Trim().ToLowerInvariant())
            {
                case "add_clip":
                {
                    // Pipeline's add_clip takes a clip asset; this also takes a model's clip (Assets/Char.fbx@Run).
                    var track = FindTrack(t, op.track);
                    TimelineClip clip;
                    if (string.IsNullOrWhiteSpace(op.clip)) clip = track.CreateDefaultClip();
                    else if (track is AnimationTrack at) clip = at.CreateClip(LoadClip<AnimationClip>(op.clip));
                    else if (track is AudioTrack au) clip = au.CreateClip(LoadClip<AudioClip>(op.clip));
                    else throw new ArgumentException($"Give a {track.GetType().Name} no clip (its default clip is made).");
                    if (op.time >= 0) clip.start = op.time;
                    foreach (string pair in (op.set ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        if (pair.Trim().StartsWith("duration=", StringComparison.OrdinalIgnoreCase)) clip.duration = (double)ValueText.Parse(pair.Split('=')[1], typeof(double));
                    if (!string.IsNullOrEmpty(op.name)) clip.displayName = op.name;
                    return new { op = "add_clip", track = track.name, clip = DescribeClip(clip, track.GetClips().ToList().IndexOf(clip)) };
                }
                case "set_clip":
                {
                    var clip = FindClip(FindTrack(t, op.track), op.clip);
                    foreach (string pair in (op.set ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"'{pair}' isn't name=value.");
                        double v = (double)ValueText.Parse(pair.Substring(eq + 1), typeof(double));
                        switch (pair.Substring(0, eq).Trim().ToLowerInvariant())
                        {
                            case "start": clip.start = v; break;
                            case "duration": clip.duration = v; break;
                            case "clipin": clip.clipIn = v; break;
                            case "timescale": case "speed": clip.timeScale = v; break;
                            case "easein": clip.easeInDuration = v; break;
                            case "easeout": clip.easeOutDuration = v; break;
                            case "blendin": clip.blendInDuration = v; break;
                            case "blendout": clip.blendOutDuration = v; break;
                            default: throw new ArgumentException($"Unknown clip setting '{pair}': start, duration, clipIn, timeScale, easeIn, easeOut, blendIn, blendOut.");
                        }
                    }
                    if (!string.IsNullOrEmpty(op.name)) clip.displayName = op.name;
                    return new { op = "set_clip", track = op.track, clip = DescribeClip(clip, clip.GetParentTrack().GetClips().ToList().IndexOf(clip)) };
                }
                case "move_clip":
                {
                    var from = FindTrack(t, op.track);
                    var clip = FindClip(from, op.clip);
                    var to = FindTrack(t, op.to);
                    if (!clip.TryMoveToTrack(to)) throw new ArgumentException($"{to.name} ({to.GetType().Name}) can't hold that clip.");
                    return new { op = "move_clip", clip = clip.displayName, to = to.name };
                }
                case "remove_clip":
                {
                    var track = FindTrack(t, op.track);
                    var clip = FindClip(track, op.clip);
                    track.DeleteClip(clip);
                    return new { op = "remove_clip", track = track.name, clip = op.clip };
                }
                case "remove_track":
                {
                    var track = FindTrack(t, op.track);
                    t.DeleteTrack(track);
                    return new { op = "remove_track", track = op.track };
                }
                case "rename_track":
                {
                    var track = FindTrack(t, op.track);
                    track.name = op.name;
                    return new { op = "rename_track", track = op.name };
                }
                case "mute_track":
                case "lock_track":
                {
                    var track = FindTrack(t, op.track);
                    bool on = string.IsNullOrEmpty(op.value) || (bool)ValueText.Parse(op.value, typeof(bool));
                    if (op.op.Trim().ToLowerInvariant() == "mute_track") track.muted = on; else track.locked = on;
                    return new { op = op.op, track = track.name, value = on };
                }
                case "add_marker":
                {
                    if (op.time < 0) throw new ArgumentException("add_marker needs a time.");
                    TrackAsset track;
                    if (string.IsNullOrWhiteSpace(op.track)) { t.CreateMarkerTrack(); track = t.markerTrack; }
                    else track = FindTrack(t, op.track);
                    if (string.IsNullOrWhiteSpace(op.signal))
                    {
                        var m = track.CreateMarker<SignalEmitter>(op.time);
                        return new { op = "add_marker", track = track.name, time = m.time, type = m.GetType().Name };
                    }
                    var signal = AssetDatabase.LoadAssetAtPath<SignalAsset>(op.signal) ?? throw new ArgumentException($"No SignalAsset at '{op.signal}'.");
                    var emitter = track.CreateMarker<SignalEmitter>(op.time);
                    emitter.asset = signal;
                    return new { op = "add_marker", track = track.name, time = emitter.time, signal = op.signal };
                }
                case "remove_marker":
                {
                    TrackAsset track = string.IsNullOrWhiteSpace(op.track) ? t.markerTrack : FindTrack(t, op.track);
                    var marker = track?.GetMarkers().FirstOrDefault(m => Math.Abs(m.time - op.time) < 1e-4) ?? throw new ArgumentException($"No marker at {op.time}s.");
                    track.DeleteMarker(marker);
                    return new { op = "remove_marker", time = op.time };
                }
                default:
                    throw new ArgumentException($"Unknown op '{op.op}'. Ops: add_clip, set_clip, move_clip, remove_clip, remove_track, rename_track, mute_track, lock_track, add_marker, remove_marker.");
            }
        }

        /// <summary>A clip asset by path, or a sub-asset as path@name (a model's take).</summary>
        private static T LoadClip<T>(string spec) where T : UnityEngine.Object
        {
            string path = spec, sub = null;
            int at = spec.LastIndexOf('@');
            if (at > 0 && !File.Exists(spec)) { path = spec.Substring(0, at); sub = spec.Substring(at + 1); }
            var all = AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().Where(c => !c.name.StartsWith("__preview__")).ToList();
            var found = sub == null ? AssetDatabase.LoadAssetAtPath<T>(path) ?? all.FirstOrDefault() : all.FirstOrDefault(c => string.Equals(c.name, sub, StringComparison.OrdinalIgnoreCase));
            return found ?? throw new ArgumentException($"No {typeof(T).Name} '{spec}'." + (all.Count > 0 ? $" There: {string.Join(", ", all.Select(c => c.name))}" : ""));
        }

        // ---------------------------------------------------------------------------------------------------------
        // Directors

        [CliGroupCommand(Group, "Bind a track of a director's timeline to a scene object (a GameObject, or its Animator / AudioSource / other component the track needs); --object none clears it.",
            Name = "bind", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Bind(
            CommandContext cmd,
            [CliArg("director", "The GameObject with the PlayableDirector", Required = true)] string director,
            [CliArg("track", "The track (name, path or index)", Required = true)] string track,
            [CliArg("object", "The GameObject to bind, or none", Required = true)] string obj)
        {
            try
            {
                var d = FindDirector(director);
                var t = d.playableAsset as TimelineAsset ?? throw new ArgumentException($"{d.name}'s director doesn't play a timeline.");
                var tr = FindTrack(t, track);
                UnityEngine.Object binding = null;
                if (obj != "none")
                {
                    var go = FindGameObject(obj);
                    var bindingType = tr.outputs.FirstOrDefault().outputTargetType;
                    binding = bindingType == null || bindingType == typeof(GameObject) ? (UnityEngine.Object)go
                            : go.GetComponent(bindingType) ?? throw new ArgumentException($"{go.name} has no {bindingType.Name}, which a {tr.GetType().Name} binds to.");
                }
                Undo.RecordObject(d, "cliext.timeline bind");
                d.SetGenericBinding(tr, binding);
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
                cmd.Result.Write(new { director = d.name, track = tr.name, boundTo = Describe(binding) });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.Failed; }
        }

        [CliGroupCommand(Group, "Control a PlayableDirector: --time seconds scrubs to that time and evaluates (in Edit mode the scene shows that frame), --action play|pause|resume|stop. " +
                                "Returns the director's time, duration and state.",
            Name = "director", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Director(
            CommandContext cmd,
            [CliArg("director", "The GameObject with the PlayableDirector", Required = true)] string director,
            [CliArg("time", "Scrub to this time in seconds (default -1: don't scrub)")] double time = -1,
            [CliArg("action", "play, pause, resume, stop, or empty to just read")] string action = "")
        {
            try
            {
                var d = FindDirector(director);
                switch ((action ?? "").Trim().ToLowerInvariant())
                {
                    case "": break;
                    case "play": d.Play(); break;
                    case "pause": d.Pause(); break;
                    case "resume": d.Resume(); break;
                    case "stop": d.Stop(); break;
                    default: throw new ArgumentException($"Unknown action '{action}': play, pause, resume, stop.");
                }
                if (time >= 0)
                {
                    if (d.playableGraph.IsValid() == false) d.RebuildGraph();
                    d.time = time;
                    d.Evaluate();
                    if (!Application.isPlaying) SceneView.RepaintAll();
                }
                cmd.Result.Write(new
                {
                    director = d.name,
                    timeline = d.playableAsset != null ? AssetDatabase.GetAssetPath(d.playableAsset) : null,
                    time = Math.Round(d.time, 4),
                    duration = d.duration,
                    state = d.state.ToString(),
                    wrapMode = d.extrapolationMode.ToString()
                });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return ExitCode.Failed; }
        }

        private static PlayableDirector FindDirector(string name)
        {
            var all = UnityEngine.Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include);
            return all.FirstOrDefault(d => string.Equals(d.name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No PlayableDirector on '{name}'. Directors: {string.Join(", ", all.Select(d => d.name))}");
        }

        private static GameObject FindGameObject(string name) =>
            GameObject.Find(name) ?? Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.scene.IsValid() && string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No GameObject '{name}' in the open scenes.");
    }
}

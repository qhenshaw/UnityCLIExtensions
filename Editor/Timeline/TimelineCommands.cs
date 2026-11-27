using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityCLIExtensions.Animation;
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
        /// <summary>add_clip, set_clip, move_clip, remove_clip, remove_track, rename_track, mute_track, lock_track, add_marker, remove_marker.</summary>
        public string op;
        /// <summary>The track: its name, or a path through groups (Group/Track), or its index.</summary>
        public string track;
        /// <summary>The clip: its display name or index on the track. For add_clip: the clip asset.</summary>
        public string clip;
        public string name;
        /// <summary>Clip settings "start=1;duration=2;clipIn=0;easeIn=0.25;easeOut=0.25;timeScale=1;blendIn=0;blendOut=0".</summary>
        public string set;
        /// <summary>For move_clip: the destination track.</summary>
        public string to;
        /// <summary>For mute/lock: true/false.</summary>
        public string value;
        /// <summary>For add_clip, add_marker and remove_marker: the time in seconds.</summary>
        public double time = -1;
        /// <summary>For add_marker: a SignalAsset path (a SignalEmitter), or empty for a plain emitter.</summary>
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
        private const string Ops = "add_clip, set_clip, move_clip, remove_clip, remove_track, rename_track, mute_track, lock_track, add_marker, remove_marker";
        private const string Inspect_ = "cliext.timeline inspect";

        private static string Num(double v) => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);

        /// <summary>The timeline at a path or GUID: invalid usage for no path, one outside Assets/ or Packages/, or another asset type; not found (naming the command that makes one) when nothing is there.</summary>
        internal static TimelineAsset Load(string path)
        {
            string p = AnimationErrors.AssetPath(path);
            if (p.Length == 0) throw new AnimUsageException("Pass --path, a .playable timeline (or --director, a GameObject with a PlayableDirector).");
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                throw new AnimUsageException($"'{path}' isn't under Assets/ or Packages/. Give the timeline's asset path, such as Assets/Cutscenes/Intro.playable.");
            AnimationErrors.RequireType(p, "TimelineAsset", typeof(TimelineAsset),
                $"Create one with animation.timeline create --path {p}, or pass an existing .playable path or GUID.", path);
            return AssetDatabase.LoadAssetAtPath<TimelineAsset>(p);
        }

        internal static IEnumerable<(string path, TrackAsset track)> Tracks(TimelineAsset t)
        {
            IEnumerable<(string, TrackAsset)> Walk(IEnumerable<TrackAsset> tracks, string prefix)
            {
                foreach (var tr in tracks)
                {
                    // The marker track is listed among the root tracks once made; its markers are the timeline's own.
                    if (tr == null || tr == t.markerTrack) continue;
                    yield return (prefix + tr.name, tr);
                    foreach (var x in Walk(tr.GetChildTracks(), prefix + tr.name + "/")) yield return x;
                }
            }
            return Walk(t.GetRootTracks(), "");
        }

        internal static TrackAsset FindTrack(TimelineAsset t, string track, string what = "a track")
        {
            if (string.IsNullOrWhiteSpace(track)) throw new ArgumentException($"Name {what} (its name, Group/Track path or index; {Inspect_} lists them).");
            string n = track.Trim();
            var all = Tracks(t).ToList();
            if (int.TryParse(n, out int i))
            {
                if (i >= 0 && i < all.Count) return all[i].track;
                throw new ArgumentException($"No track {i}: the timeline has {all.Count} (0-{all.Count - 1}).{AnimationErrors.Hint("", all.Select(x => x.path), "Tracks", Inspect_)}");
            }
            var byPath = all.FirstOrDefault(x => string.Equals(x.path, n, StringComparison.OrdinalIgnoreCase)).track;
            if (byPath != null) return byPath;
            var byName = all.Where(x => string.Equals(x.track.name, n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 1) throw new ArgumentException($"'{n}' names {byName.Count} tracks: {string.Join(", ", byName.Select(x => x.path))}. Use the path.");
            return byName.FirstOrDefault().track
                   ?? throw new ArgumentException($"No track '{n}'." + AnimationErrors.Hint(n, all.Select(x => x.path), "Tracks", Inspect_));
        }

        internal static TimelineClip FindClip(TrackAsset track, string clip)
        {
            var clips = track.GetClips().ToList();
            if (string.IsNullOrWhiteSpace(clip)) throw new ArgumentException($"Name the clip on {track.name} (its name or index; {Inspect_} lists them).");
            string n = clip.Trim();
            if (int.TryParse(n, out int i))
            {
                if (i >= 0 && i < clips.Count) return clips[i];
                throw new ArgumentException($"No clip {i} on {track.name}: it has {clips.Count}.{AnimationErrors.Hint("", clips.Select(c => c.displayName), "Clips", Inspect_)}");
            }
            return clips.FirstOrDefault(c => string.Equals(c.displayName, n, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No clip '{n}' on {track.name}." + AnimationErrors.Hint(n, clips.Select(c => c.displayName), "Clips", Inspect_));
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
                                "with --director, also what each track is bound to. A missing timeline or director exits 13; a path outside Assets/ or another asset type exits 2.",
            Name = "inspect", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(
            CommandContext cmd,
            [CliArg("path", "The .playable timeline (or leave it out and pass --director)")] string path = "",
            [CliArg("director", "A GameObject with a PlayableDirector: also show what each track is bound to")] string director = "")
        {
            try
            {
                PlayableDirector d = string.IsNullOrWhiteSpace(director) ? null : FindDirector(director);
                var t = d != null && string.IsNullOrWhiteSpace(path) ? DirectorTimeline(d) : Load(path);
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
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        private static object DescribeMarker(IMarker m) => new
        {
            time = m.time,
            type = m.GetType().Name,
            signal = m is SignalEmitter se && se.asset != null ? AssetDatabase.GetAssetPath(se.asset) : null
        };

        private static string Describe(UnityEngine.Object o) => o == null ? null : o is Component c ? $"{c.gameObject.name} ({c.GetType().Name})" : o.name;

        [CliGroupCommand(Group, "Edit a TimelineAsset with a JSON list of ops, saved once: add_clip (track, clip=asset path or model.fbx@take, time, set=\"duration=2\"; no clip for a track's default clip), " +
                                "set_clip (track, clip, set=\"start=1;duration=2;clipIn=0;easeIn=0.25;easeOut=0.25;timeScale=1;blendIn=0;blendOut=0\", name), " +
                                "move_clip (track, clip, to=another track), remove_clip, remove_track, rename_track (name), mute_track and lock_track (value true/false, default true), " +
                                "add_marker (time; on a track, or the timeline's marker track with no track; signal=SignalAsset path), remove_marker (time). " +
                                "Tracks are named by name, Group/Track path or index, clips by name or index (cliext.timeline inspect lists both). " +
                                "Pipeline's animation.timeline commands create timelines and add tracks. An op that fails saves nothing (so does one that would change nothing): the timeline is left as it was, on disk and in memory.",
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
            try { t = Load(path); } catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
            var results = new List<object>();
            if (!TryApplyAll(t, list.ops, results, out int failedAt, out string error))
            {
                cmd.Result.Write(new { path, applied = 0, failedAt, error, saved = false });
                cmd.Fail($"Op {failedAt} ({list.ops[failedAt].op}) failed: {error} Nothing was saved.");
                return ExitCode.Failed;
            }
            cmd.Result.Write(new { path, applied = results.Count, results, duration = t.duration });
            return ExitCode.Success;
        }

        /// <summary>
        /// Applies every op and saves once; on the first failure puts the timeline back as it was, on disk and in memory
        /// (removed tracks, clips and their assets included, through the batch's Undo group), and saves nothing.
        /// </summary>
        internal static bool TryApplyAll(TimelineAsset t, IList<TimelineOp> ops, List<object> results, out int failedAt, out string error)
        {
            string assetPath = AssetDatabase.GetAssetPath(t);
            using var rollback = AssetRollback.Begin(new[] { assetPath }, revertUndo: true);
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(Apply(t, ops[i])); }
                catch (Exception e)
                {
                    results.Clear();
                    rollback.Rollback();
                    failedAt = i;
                    error = AnimationErrors.Message(e);
                    return false;
                }
            }
            Save(t);
            failedAt = -1;
            error = null;
            return true;
        }

        private static void Save(TimelineAsset t)
        {
            EditorUtility.SetDirty(t);
            foreach (var (_, tr) in Tracks(t)) EditorUtility.SetDirty(tr);
            if (t.markerTrack != null) EditorUtility.SetDirty(t.markerTrack);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Applies "start=1;duration=2;..." to a clip; every value is checked before any is set.</summary>
        private static void SetClip(TimelineClip clip, string set)
        {
            var values = new List<(string key, double v)>();
            foreach (string pair in (set ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.Trim().Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{pair.Trim()}' isn't name=value.");
                string key = pair.Substring(0, eq).Trim().ToLowerInvariant();
                if (key == "speed") key = "timescale";
                double v = (double)ValueText.Parse(pair.Substring(eq + 1), typeof(double));
                switch (key)
                {
                    case "start": case "clipin": case "easein": case "easeout": case "blendin": case "blendout":
                        if (v < 0 || double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException($"{pair.Substring(0, eq).Trim()} must be 0 or more seconds, not {pair.Substring(eq + 1).Trim()}.");
                        break;
                    case "duration": case "timescale":
                        if (!(v > 0) || double.IsInfinity(v)) throw new ArgumentException($"{pair.Substring(0, eq).Trim()} must be more than 0, not {pair.Substring(eq + 1).Trim()}.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown clip setting '{pair.Substring(0, eq).Trim()}'." +
                            AnimationErrors.Hint(pair.Substring(0, eq).Trim(), new[] { "start", "duration", "clipIn", "timeScale", "easeIn", "easeOut", "blendIn", "blendOut" }, "Settings", "cliext.timeline edit --help"));
                }
                values.Add((key, v));
            }
            foreach (var (key, v) in values)
            {
                switch (key)
                {
                    case "start": clip.start = v; break;
                    case "duration": clip.duration = v; break;
                    case "clipin": clip.clipIn = v; break;
                    case "timescale": clip.timeScale = v; break;
                    case "easein": clip.easeInDuration = v; break;
                    case "easeout": clip.easeOutDuration = v; break;
                    case "blendin": clip.blendInDuration = v; break;
                    case "blendout": clip.blendOutDuration = v; break;
                }
            }
        }

        internal static object Apply(TimelineAsset t, TimelineOp op)
        {
            string kind = (op.op ?? "").Trim().ToLowerInvariant();
            switch (kind)
            {
                case "add_clip":
                {
                    // Pipeline's add_clip takes a clip asset; this also takes a model's clip (Assets/Char.fbx@Run).
                    var track = FindTrack(t, op.track);
                    if (track is GroupTrack) throw new ArgumentException($"{track.name} is a GroupTrack: it holds tracks, not clips. Add the clip to one of its tracks, or add a track with animation.timeline add_track.");
                    if (double.IsNaN(op.time) || (op.time < 0 && op.time != -1)) throw new ArgumentException($"add_clip's time must be 0 or more seconds, not {Num(op.time)}.");
                    TimelineClip clip;
                    if (string.IsNullOrWhiteSpace(op.clip))
                    {
                        try { clip = track.CreateDefaultClip(); }
                        catch (InvalidOperationException) { throw new ArgumentException($"{track.name} ({track.GetType().Name}) has no default clip: give a clip asset."); }
                    }
                    else if (track is AnimationTrack at) clip = at.CreateClip(LoadClip<AnimationClip>(op.clip));
                    else if (track is AudioTrack au) clip = au.CreateClip(LoadClip<AudioClip>(op.clip));
                    else throw new ArgumentException($"{track.name} is a {track.GetType().Name}: give it no clip (its default clip is made). Clip assets go on Animation and Audio tracks.");
                    if (op.time >= 0) clip.start = op.time;
                    SetClip(clip, op.set);
                    if (!string.IsNullOrEmpty(op.name)) clip.displayName = op.name;
                    return new { op = "add_clip", track = track.name, clip = DescribeClip(clip, track.GetClips().ToList().IndexOf(clip)) };
                }
                case "set_clip":
                {
                    var track = FindTrack(t, op.track);
                    var clip = FindClip(track, op.clip);
                    if (string.IsNullOrWhiteSpace(op.set) && string.IsNullOrEmpty(op.name))
                        throw new ArgumentException("set_clip needs set (start=1;duration=2;...) or a name: as given it would change nothing.");
                    SetClip(clip, op.set);
                    if (!string.IsNullOrEmpty(op.name)) clip.displayName = op.name;
                    return new { op = "set_clip", track = track.name, clip = DescribeClip(clip, clip.GetParentTrack().GetClips().ToList().IndexOf(clip)) };
                }
                case "move_clip":
                {
                    var from = FindTrack(t, op.track);
                    var clip = FindClip(from, op.clip);
                    if (string.IsNullOrWhiteSpace(op.to)) throw new ArgumentException("move_clip needs to: the track to move the clip to.");
                    var to = FindTrack(t, op.to, "the track to move to");
                    if (to == from) throw new ArgumentException($"{clip.displayName} is already on {from.name}: give another track in to.");
                    if (!clip.TryMoveToTrack(to))
                        throw new ArgumentException($"{to.name} ({to.GetType().Name}) can't hold {clip.displayName}, {AnimationErrors.A(clip.asset != null ? clip.asset.GetType().Name : "clip")}: move it to {AnimationErrors.A(from.GetType().Name)}.");
                    return new { op = "move_clip", clip = clip.displayName, to = to.name };
                }
                case "remove_clip":
                {
                    var track = FindTrack(t, op.track);
                    var clip = FindClip(track, op.clip);
                    string name = clip.displayName;
                    if (!track.DeleteClip(clip)) throw new ArgumentException($"{name} could not be removed from {track.name}{(track.locked ? ": the track is locked (lock_track value false)" : "")}.");
                    return new { op = "remove_clip", track = track.name, clip = name };
                }
                case "remove_track":
                {
                    var track = FindTrack(t, op.track);
                    string name = track.name;
                    if (!t.DeleteTrack(track)) throw new ArgumentException($"{name} could not be removed.");
                    return new { op = "remove_track", track = name };
                }
                case "rename_track":
                {
                    var track = FindTrack(t, op.track);
                    if (string.IsNullOrWhiteSpace(op.name)) throw new ArgumentException("rename_track needs a name.");
                    if (track.name == op.name.Trim()) throw new ArgumentException($"{track.name} already has that name: as given it would change nothing.");
                    string old = track.name;
                    track.name = op.name.Trim();
                    return new { op = "rename_track", track = old, name = track.name };
                }
                case "mute_track":
                case "lock_track":
                {
                    var track = FindTrack(t, op.track);
                    bool on = string.IsNullOrWhiteSpace(op.value) || (bool)ValueText.Parse(op.value, typeof(bool));
                    bool mute = kind == "mute_track";
                    if ((mute ? track.muted : track.locked) == on)
                        throw new ArgumentException($"{track.name} is already {(on ? "" : "un")}{(mute ? "muted" : "locked")}: as given it would change nothing.");
                    if (mute) track.muted = on; else track.locked = on;
                    return new { op = kind, track = track.name, value = on };
                }
                case "add_marker":
                {
                    if (op.time < 0 || double.IsNaN(op.time)) throw new ArgumentException(op.time == -1 ? "add_marker needs a time (seconds, 0 or more)." : $"add_marker's time must be 0 or more seconds, not {Num(op.time)}.");
                    SignalAsset signal = null;
                    if (!string.IsNullOrWhiteSpace(op.signal))
                    {
                        string sp = AnimationErrors.AssetPath(op.signal);
                        AnimationErrors.RequireType(sp, "SignalAsset", typeof(SignalAsset), "Create one in the Project window (Create > Timeline > Signal), or leave signal out for a plain emitter.", op.signal);
                        signal = AssetDatabase.LoadAssetAtPath<SignalAsset>(sp);
                    }
                    TrackAsset track;
                    if (string.IsNullOrWhiteSpace(op.track)) { t.CreateMarkerTrack(); track = t.markerTrack; }
                    else track = FindTrack(t, op.track);
                    if (track.GetMarkers().Any(m => Math.Abs(m.time - op.time) < 1e-4)) throw new ArgumentException($"{track.name} already has a marker at {Num(op.time)}s.");
                    var emitter = track.CreateMarker<SignalEmitter>(op.time) ?? throw new ArgumentException($"{track.name} ({track.GetType().Name}) can't hold markers.");
                    if (signal != null) emitter.asset = signal;
                    return new { op = "add_marker", track = track.name, time = emitter.time, signal = signal != null ? AssetDatabase.GetAssetPath(signal) : null };
                }
                case "remove_marker":
                {
                    TrackAsset track = string.IsNullOrWhiteSpace(op.track) ? t.markerTrack : FindTrack(t, op.track);
                    var markers = track != null ? track.GetMarkers().ToList() : new List<IMarker>();
                    string where = track != null ? track.name : "the timeline's marker track";
                    string times = markers.Count == 0 ? $" {where} has no markers." : $" Markers on {where} at: {string.Join(", ", markers.Select(m => Num(m.time) + "s"))}.";
                    if (op.time < 0) throw new ArgumentException("remove_marker needs a time (seconds)." + times);
                    var marker = markers.FirstOrDefault(m => Math.Abs(m.time - op.time) < 1e-4) ?? throw new ArgumentException($"No marker at {Num(op.time)}s.{times}");
                    track.DeleteMarker(marker);
                    return new { op = "remove_marker", track = track.name, time = op.time };
                }
                default:
                {
                    var close = AnimationErrors.Close(op.op ?? "", Ops.Split(new[] { ", " }, StringSplitOptions.None));
                    throw new ArgumentException(string.IsNullOrWhiteSpace(op.op) ? $"An op has no \"op\". Ops: {Ops}." :
                        $"Unknown op '{op.op}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Ops: {Ops}.");
                }
            }
        }

        /// <summary>A clip asset by path, or a sub-asset as path@name (a model's take).</summary>
        private static T LoadClip<T>(string spec) where T : UnityEngine.Object
        {
            string path = spec.Trim(), sub = null;
            int at = path.LastIndexOf('@');
            if (at > 0 && !File.Exists(path)) { sub = path.Substring(at + 1); path = path.Substring(0, at); }
            var all = AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().Where(c => !c.name.StartsWith("__preview__")).ToList();
            var found = sub == null ? AssetDatabase.LoadAssetAtPath<T>(path) ?? all.FirstOrDefault() : all.FirstOrDefault(c => string.Equals(c.name, sub, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null)
                throw new ArgumentException($"No {typeof(T).Name} at '{path}': nothing is there. Give " +
                    (typeof(T) == typeof(AnimationClip) ? "an .anim path or a model's take as model.fbx@Take (cliext.anim takes lists them)." : "an audio file's path."));
            if (sub != null && all.Count > 0)
                throw new ArgumentException($"No {typeof(T).Name} '{sub}' in '{path}'." + AnimationErrors.Hint(sub, all.Select(c => c.name), "Clips", "cliext.anim takes"));
            throw new ArgumentException($"'{path}' is {AnimationErrors.A(main.GetType().Name)} with no {typeof(T).Name}" +
                (typeof(T) == typeof(AudioClip) ? ": an AudioTrack takes an audio file." : ": an AnimationTrack takes an .anim or a model's take."));
        }

        // ---------------------------------------------------------------------------------------------------------
        // Directors

        private static TimelineAsset DirectorTimeline(PlayableDirector d) =>
            d.playableAsset as TimelineAsset ?? throw new ArgumentException(
                $"{d.name}'s director doesn't play a timeline{(d.playableAsset != null ? $" (it plays {AnimationErrors.A(d.playableAsset.GetType().Name)})" : "")}. Set its Playable Asset to a .playable, or pass --path.");

        [CliGroupCommand(Group, "Bind a track of a director's timeline to a scene object (a GameObject, or its Animator / AudioSource / other component the track needs); --object none clears it. " +
                                "An unknown director, track or object exits 13; an object without the component the track binds to, or a group track, exits 2.",
            Name = "bind", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Bind(
            CommandContext cmd,
            [CliArg("director", "The GameObject with the PlayableDirector", Required = true)] string director,
            [CliArg("track", "The track (name, Group/Track path or index)", Required = true)] string track,
            [CliArg("object", "The GameObject to bind, or none", Required = true)] string obj)
        {
            try
            {
                var d = FindDirector(director);
                var t = DirectorTimeline(d);
                TrackAsset tr;
                try { tr = FindTrack(t, track); } catch (ArgumentException e) { throw new AnimNotFoundException(e.Message); }
                var output = tr.outputs.FirstOrDefault();
                if (tr is GroupTrack || output.sourceObject == null)
                    throw new AnimUsageException($"{tr.name} is a {tr.GetType().Name}, which has no binding. Bind one of its tracks instead.");
                UnityEngine.Object binding = null;
                if (!string.Equals((obj ?? "").Trim(), "none", StringComparison.OrdinalIgnoreCase))
                    binding = BindingFor(tr, output.outputTargetType, FindGameObject(obj));
                Undo.RecordObject(d, "cliext.timeline bind");
                d.SetGenericBinding(tr, binding);
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
                cmd.Result.Write(new { director = d.name, track = tr.name, boundTo = Describe(binding) });
                return ExitCode.Success;
            }
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        /// <summary>What a track binds to on <paramref name="go"/>: the GameObject, or its component of the track's binding type.</summary>
        internal static UnityEngine.Object BindingFor(TrackAsset tr, Type bindingType, GameObject go)
        {
            if (bindingType == null || bindingType == typeof(GameObject)) return go;
            if (!typeof(Component).IsAssignableFrom(bindingType))
                throw new AnimUsageException($"A {tr.GetType().Name} binds to {AnimationErrors.A(bindingType.Name)}, not a GameObject.");
            // GetComponent's missing result is a fake null in the Editor, which `??` lets through: compare with ==.
            var component = go.GetComponent(bindingType);
            if (component != null) return component;
            var inChild = go.GetComponentsInChildren(bindingType, true).FirstOrDefault(c => c != null);
            throw new AnimUsageException($"{go.name} has no {bindingType.Name}, which a {tr.GetType().Name} binds to." +
                (inChild != null ? $" Its child '{inChild.gameObject.name}' has one: pass --object {inChild.gameObject.name}." : $" Add {AnimationErrors.A(bindingType.Name)} to it, or bind an object that has one."));
        }

        [CliGroupCommand(Group, "Control a PlayableDirector: --time seconds scrubs to that time and evaluates (in Edit mode the scene shows that frame), --action play|pause|resume|stop (play, pause and resume need Play mode). " +
                                "Returns the director's time, duration and state. An unknown director exits 13; a bad --time or --action exits 2.",
            Name = "director", Tags = new[] { "cliext", "timeline" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Director(
            CommandContext cmd,
            [CliArg("director", "The GameObject with the PlayableDirector", Required = true)] string director,
            [CliArg("time", "Scrub to this time in seconds, 0 to the timeline's duration (default -1: don't scrub)")] double time = -1,
            [CliArg("action", "play, pause, resume, stop, or empty to just read (play, pause and resume need Play mode)")] string action = "")
        {
            try
            {
                string a = (action ?? "").Trim().ToLowerInvariant();
                if (a != "" && a != "play" && a != "pause" && a != "resume" && a != "stop")
                    throw new AnimUsageException($"Unknown action '{action}'. Use one of: play, pause, resume, stop (or leave it out to just read).");
                if (double.IsNaN(time) || (time < 0 && time != -1)) throw new AnimUsageException($"--time must be 0 or more seconds (or -1 not to scrub), not {Num(time)}.");
                var d = FindDirector(director);
                if ((a != "" || time >= 0) && d.playableAsset == null)
                    throw new ArgumentException($"{d.name}'s director has no Playable Asset to {(a != "" ? a : "scrub")}. Set it to a .playable timeline first.");
                if (time >= 0 && d.playableAsset != null && time > d.playableAsset.duration + 1e-6)
                    throw new AnimUsageException($"--time {Num(time)} is past the end of {d.playableAsset.name} ({Num(d.playableAsset.duration)}s).");
                if ((a == "play" || a == "pause" || a == "resume") && !Application.isPlaying)
                {
                    cmd.Fail($"A director only advances in Play mode: enter Play mode first (editor.playmode start) to {a} it. In Edit mode, scrub with --time.");
                    return ExitCode.Failed;
                }
                switch (a)
                {
                    case "play": d.Play(); break;
                    case "pause": d.Pause(); break;
                    case "resume": d.Resume(); break;
                    case "stop": d.Stop(); break;
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
            catch (ArgumentException e) { cmd.Fail(e.Message); return AnimationErrors.Exit(e); }
        }

        private static PlayableDirector FindDirector(string name)
        {
            string n = (name ?? "").Trim();
            var all = UnityEngine.Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include);
            var d = all.FirstOrDefault(x => string.Equals(x.name, n, StringComparison.OrdinalIgnoreCase));
            if (d != null) return d;
            var go = FindGameObjectOrNull(n);
            if (go != null && go.GetComponent<PlayableDirector>() is PlayableDirector onGo && onGo != null) return onGo;
            string hint = all.Length == 0
                ? " The open scenes have no PlayableDirector: add one to a GameObject and set its Playable Asset to a .playable."
                : AnimationErrors.Hint(n, all.Select(x => x.name), "Directors", "scene get_hierarchy");
            throw new AnimNotFoundException(go != null ? $"'{go.name}' has no PlayableDirector.{hint}" : $"No PlayableDirector on '{n}' in the open scenes.{hint}");
        }

        private static GameObject FindGameObjectOrNull(string name) =>
            string.IsNullOrWhiteSpace(name) ? null :
            GameObject.Find(name) ?? Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.scene.IsValid() && string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase));

        private static GameObject FindGameObject(string name) =>
            FindGameObjectOrNull((name ?? "").Trim())
            ?? throw new AnimNotFoundException($"No GameObject '{name}' in the open scenes. Give its name or hierarchy path (scene get_hierarchy lists them), or none to clear the binding.");
    }
}

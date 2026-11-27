// Prove which clip a scrubbed character is posed in, without changing the scene:
//   1. cliext.eval run --file <copy of this> --vars "mode=bones;object=<GameObject>;clip=-;time=0;read=-"
//      returns the hierarchy paths of every transform under <object>, joined with ';' (the --read list).
//   2. cliext.timeline director --director <d> --time <t> --read "<that list>" --format json > scrub.json
//      (Edit mode puts the bound objects back afterwards: no --keep-pose, nothing to restore, the scene stays as saved)
//   3. cliext.eval run --file <copy of this> --vars "mode=compare;object=<GameObject>;clip=<Model.fbx@Clip or .anim>;time=<local time>;read=<full path of scrub.json>"
//      samples the clip at time (seconds into the clip) on a hidden copy of <object> and returns how far the --read
//      pose is from it: a mean under ~1 degree means that clip is the one posed (the max can still be several
//      degrees); a wrong clip is clearly away (mean 5+). A looping clip's time is wrapped by its length. Two clips
//      with the same name: the first one is sampled (check the reported length), so cut a uniquely named clip first.
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

var inv = CultureInfo.InvariantCulture;
string mode = "{{mode}}", name = "{{object}}";
var go = GameObject.Find(name) ?? Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
    .Select(t => t.gameObject).FirstOrDefault(g => g.name == name);
if (go == null) return $"No GameObject '{name}' in the open scenes.";
var root = go.transform;
var bones = root.GetComponentsInChildren<Transform>(true);
string ScenePath(Transform t) { string p = t.name; for (var q = t.parent; q != null; q = q.parent) p = q.name + "/" + p; return p; }
string F(float f) => f.ToString("0.###", inv);

if (mode == "bones")
    return string.Join(";", bones.Where(t => t != root).Select(ScenePath));
if (mode == "compare")
{
    string spec = "{{clip}}", readFile = @"{{read}}"; // verbatim: a Windows path's backslashes stay as they are
    if (!File.Exists(readFile)) return $"No file '{readFile}': save the reply of cliext.timeline director --read ... --format json to it.";
    // Each --read entry: "object":"<scene path>", ..., "localRotation":[x,y,z] (Euler degrees).
    var read = Regex.Matches(File.ReadAllText(readFile), "\"object\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"localRotation\"\\s*:\\s*\\[([^\\]]+)\\]")
        .Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Split(',').Select(v => float.Parse(v.Trim(), inv)).ToArray());
    if (read.Count == 0) return $"'{readFile}' holds no --read poses: pass --read to cliext.timeline director (mode=bones lists the paths).";
    int at = spec.LastIndexOf('@');
    var clip = at > 0 && !spec.EndsWith(".anim")
        ? AssetDatabase.LoadAllAssetsAtPath(spec.Substring(0, at)).OfType<AnimationClip>().FirstOrDefault(c => c.name == spec.Substring(at + 1))
        : AssetDatabase.LoadAssetAtPath<AnimationClip>(spec);
    if (clip == null) return $"No clip '{spec}' (cliext.anim takes lists a model's clips).";
    float time = float.Parse("{{time}}", inv);
    // A looping clip (loopTime) repeats on a timeline track, but SampleAnimation clamps past its end: wrap it here.
    if (clip.isLooping && clip.length > 0f) time %= clip.length;
    string prefix = ScenePath(root) + "/";
    var copy = Object.Instantiate(go);
    copy.hideFlags = HideFlags.HideAndDontSave;
    try
    {
        clip.SampleAnimation(copy, time);
        var sampled = copy.GetComponentsInChildren<Transform>(true)
            .GroupBy(t => AnimationUtility.CalculateTransformPath(t, copy.transform)).ToDictionary(g => g.Key, g => g.First());
        var angles = new System.Collections.Generic.List<float>();
        foreach (var kv in read)
        {
            if (!kv.Key.StartsWith(prefix) || !sampled.TryGetValue(kv.Key.Substring(prefix.Length), out var s)) continue;
            angles.Add(Quaternion.Angle(Quaternion.Euler(kv.Value[0], kv.Value[1], kv.Value[2]), s.localRotation));
        }
        if (angles.Count == 0) return $"None of the {read.Count} --read objects is under '{ScenePath(root)}': read the bones mode=bones lists.";
        return $"{clip.name} ({F(clip.length)} s) at {F(time)} s: max {angles.Max():F1} deg, mean {angles.Average():F2} deg over {angles.Count} bones.";
    }
    finally { Object.DestroyImmediate(copy); }
}
return $"Unknown mode '{mode}': bones or compare.";

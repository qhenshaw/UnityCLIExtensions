// cliext.eval run --file <copy of this> --vars "mode=snapshot;object=<GameObject>;clip=-;time=0"
// mode=snapshot  saves every transform under <object> to Temp/pose-<object>.txt (run it before the first scrub)
// mode=restore   puts them back from that file (run it after the last scrub, before any save)
// mode=compare   samples clip (an .anim or Model.fbx@Clip) at time (seconds into the clip) on a hidden copy of
//                <object> and returns how far the scene pose is from it: a mean under ~1 degree means that clip is
//                the one posed (the max can still be several degrees); a wrong clip is clearly away (mean 5+).
//                A looping clip's time is wrapped by its length. Two clips with the same name: the first one is
//                sampled (check the reported length), so cut a uniquely named clip first.
using System.Globalization;
using System.IO;

var inv = CultureInfo.InvariantCulture;
string mode = "{{mode}}", name = "{{object}}";
var go = GameObject.Find(name) ?? Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
    .Select(t => t.gameObject).FirstOrDefault(g => g.name == name);
if (go == null) return $"No GameObject '{name}' in the open scenes.";
var root = go.transform;
var bones = root.GetComponentsInChildren<Transform>(true);
string PathOf(Transform t) => AnimationUtility.CalculateTransformPath(t, root);
string file = $"Temp/pose-{go.name}.txt";
string F(float f) => f.ToString("R", inv);

if (mode == "snapshot")
{
    File.WriteAllLines(file, bones.Select(t =>
    {
        Vector3 p = t.localPosition, s = t.localScale; Quaternion r = t.localRotation;
        return PathOf(t) + "\t" + string.Join(",", new[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, s.x, s.y, s.z }.Select(F));
    }));
    return $"Saved {bones.Length} transforms to {file}.";
}
if (mode == "restore")
{
    if (!File.Exists(file)) return $"No {file}: run mode=snapshot before scrubbing.";
    var saved = File.ReadAllLines(file).Select(l => l.Split('\t')).GroupBy(x => x[0])
        .ToDictionary(g => g.Key, g => g.First()[1].Split(',').Select(v => float.Parse(v, inv)).ToArray());
    int restored = 0;
    foreach (var t in bones)
    {
        if (!saved.TryGetValue(PathOf(t), out var v)) continue;
        Undo.RecordObject(t, "restore pose");
        t.localPosition = new Vector3(v[0], v[1], v[2]);
        t.localRotation = new Quaternion(v[3], v[4], v[5], v[6]);
        t.localScale = new Vector3(v[7], v[8], v[9]);
        restored++;
    }
    return $"Restored {restored} of {bones.Length} transforms from {file}.";
}
if (mode == "compare")
{
    string spec = "{{clip}}";
    int at = spec.LastIndexOf('@');
    var clip = at > 0 && !spec.EndsWith(".anim")
        ? AssetDatabase.LoadAllAssetsAtPath(spec.Substring(0, at)).OfType<AnimationClip>().FirstOrDefault(c => c.name == spec.Substring(at + 1))
        : AssetDatabase.LoadAssetAtPath<AnimationClip>(spec);
    if (clip == null) return $"No clip '{spec}' (cliext.anim takes lists a model's clips).";
    float time = float.Parse("{{time}}", inv);
    // A looping clip (loopTime) repeats on a timeline track, but SampleAnimation clamps past its end: wrap it here.
    if (clip.isLooping && clip.length > 0f) time %= clip.length;
    var copy = Object.Instantiate(go);
    copy.hideFlags = HideFlags.HideAndDontSave;
    try
    {
        clip.SampleAnimation(copy, time);
        var sampled = copy.GetComponentsInChildren<Transform>(true)
            .GroupBy(t => AnimationUtility.CalculateTransformPath(t, copy.transform)).ToDictionary(g => g.Key, g => g.First());
        var angles = bones.Where(t => t != root && sampled.ContainsKey(PathOf(t)))
            .Select(t => Quaternion.Angle(t.localRotation, sampled[PathOf(t)].localRotation)).ToList();
        if (angles.Count == 0) return $"'{go.name}' has no child transforms to compare.";
        return $"{clip.name} ({F(clip.length)} s) at {F(time)} s: max {angles.Max():F1} deg, mean {angles.Average():F2} deg over {angles.Count} bones.";
    }
    finally { Object.DestroyImmediate(copy); }
}
return $"Unknown mode '{mode}': snapshot, restore or compare.";

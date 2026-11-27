// Checks every asset under a folder for missing object references, missing scripts and sub-objects that don't load,
// and lists each material's shader. Unity 6.6: objectReferenceEntityIdValue (objectReferenceInstanceIDValue is CS0619).
// Run: unity command cliext.eval run --file .claude/skills/unity-cliext-import/ops/missing-refs.cs --vars "folder=Assets/<imported folder>" --timeout 120
var folder = "{{folder}}";
if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) return "no folder " + folder;
var problems = new System.Collections.Generic.List<string>();
var shaders = new System.Collections.Generic.List<string>();
int assets = 0, objects = 0;
foreach (var guid in UnityEditor.AssetDatabase.FindAssets("", new[] { folder }))
{
    var p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
    if (UnityEditor.AssetDatabase.IsValidFolder(p) || p.EndsWith(".unity")) continue; // scenes don't load as assets
    assets++;
    foreach (var o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(p))
    {
        if (o == null) { problems.Add(p + ": a sub-object that doesn't load (missing script?)"); continue; }
        objects++;
        var go = o as UnityEngine.GameObject;
        if (go != null)
        {
            int n = UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            if (n > 0) problems.Add(p + " > " + go.name + ": " + n + " missing script(s)");
        }
        var mat = o as UnityEngine.Material;
        if (mat != null)
        {
            string s = mat.shader != null ? mat.shader.name : "(none)";
            shaders.Add(p + ": " + s + (mat.shader == null || s == "Hidden/InternalErrorShader" || !mat.shader.isSupported ? " (missing or unsupported)" : ""));
        }
        var it = new UnityEditor.SerializedObject(o).GetIterator();
        while (it.Next(true))
        {
            if (it.propertyType != UnityEditor.SerializedPropertyType.ObjectReference) continue;
            if (it.objectReferenceValue == null && !it.objectReferenceEntityIdValue.Equals(default(UnityEngine.EntityId)))
                problems.Add(p + " > " + o.name + " (" + o.GetType().Name + ")." + it.propertyPath + ": missing reference");
        }
    }
}
int max = 40;
return assets + " asset(s), " + objects + " object(s) checked; " + problems.Count + " problem(s)\n" +
       string.Join("\n", problems.GetRange(0, System.Math.Min(max, problems.Count))) + (problems.Count > max ? "\n... " + (problems.Count - max) + " more" : "") +
       "\nmaterials:\n" + string.Join("\n", shaders.GetRange(0, System.Math.Min(max, shaders.Count)));

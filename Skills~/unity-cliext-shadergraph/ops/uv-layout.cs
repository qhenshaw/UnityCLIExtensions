// How each mesh under a scene object (or in a model/mesh asset) lays out UV0: its bounds, and uvArea, the summed
// area of its UV triangles (1 = the whole 0-1 square once; a small number means the mesh samples only a small part
// of any texture mapped by UV0). Also the mesh's object-space size (boundsSize, in the mesh's own units: what
// Position (Object space) spans, for picking a triplanar or object-space scale) and, for a scene object, the
// renderer's world size (worldSize). They differ a lot on an imported model with a scaled child (0.016 vs 1.8 seen).
// unity command cliext.eval run --file <this file> --vars "target=<scene object path or asset path>" --timeout 60 --format json
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

string target = "{{target}}";
var meshes = new List<(string owner, Mesh mesh, Renderer renderer)>();
foreach (var m in AssetDatabase.LoadAllAssetsAtPath(target).OfType<Mesh>()) meshes.Add((target, m, null));
if (meshes.Count == 0)
{
    var go = GameObject.Find(target);
    if (go == null) return $"No mesh asset or scene object '{target}'.";
    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
    {
        Mesh mesh = null;
        if (r is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
        else if (r.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
        if (mesh != null) meshes.Add((r.name, mesh, r));
    }
}

return meshes.Select(t =>
{
    var uv = new List<Vector2>();
    t.mesh.GetUVs(0, uv);
    var size = t.mesh.bounds.size;
    if (uv.Count == 0) return (object)new { t.owner, mesh = t.mesh.name, uv0 = "none", boundsSize = new[] { size.x, size.y, size.z } };
    var tris = t.mesh.triangles;
    float area = 0;
    for (int i = 0; i + 2 < tris.Length; i += 3)
    {
        Vector2 a = uv[tris[i]], b = uv[tris[i + 1]], c = uv[tris[i + 2]];
        area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
    }
    return new
    {
        t.owner,
        mesh = t.mesh.name,
        vertices = t.mesh.vertexCount,
        uvMin = new[] { uv.Min(p => p.x), uv.Min(p => p.y) },
        uvMax = new[] { uv.Max(p => p.x), uv.Max(p => p.y) },
        uvArea = area,
        boundsSize = new[] { size.x, size.y, size.z },
        worldSize = t.renderer != null ? new[] { t.renderer.bounds.size.x, t.renderer.bounds.size.y, t.renderer.bounds.size.z } : null
    };
}).ToArray();

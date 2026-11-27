// What a shader samples from a texture: its import colour space and the per-channel percentiles of the sampled values
// (linear, as a Linear-colour-space project's shader sees them), plus the share of pixels below a threshold.
// unity command cliext.eval run --file <this file> --vars "texture=Assets/<...>.png;threshold=0.3" --timeout 60 --format json
using System.Linq;
using UnityEditor;
using UnityEngine;

string path = "{{texture}}";
float threshold = float.Parse("{{threshold}}", System.Globalization.CultureInfo.InvariantCulture);
var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
if (tex == null) return $"No texture at '{path}'.";
var importer = AssetImporter.GetAtPath(path) as TextureImporter;

// Blit the imported texture (any format, readable or not) into a linear target: the values a shader gets.
int w = Mathf.Min(tex.width, 512), h = Mathf.Min(tex.height, 512);
var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
var previous = RenderTexture.active;
Graphics.Blit(tex, rt);
RenderTexture.active = rt;
var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
read.Apply();
RenderTexture.active = previous;
RenderTexture.ReleaseTemporary(rt);
var pixels = read.GetPixels();
UnityEngine.Object.DestroyImmediate(read);

object Stats(System.Func<Color, float> channel)
{
    var v = pixels.Select(channel).OrderBy(x => x).ToArray();
    float P(float q) => v[Mathf.Clamp((int)(q * (v.Length - 1)), 0, v.Length - 1)];
    return new { p05 = P(0.05f), p25 = P(0.25f), p50 = P(0.5f), p75 = P(0.75f), p95 = P(0.95f), belowThreshold = v.Count(x => x < threshold) / (float)v.Length };
}

return new
{
    path,
    size = new[] { tex.width, tex.height },
    sRGBTexture = importer != null ? importer.sRGBTexture : (bool?)null,
    textureType = importer != null ? importer.textureType.ToString() : null,
    projectColorSpace = PlayerSettings.colorSpace.ToString(),
    threshold,
    r = Stats(c => c.r), g = Stats(c => c.g), b = Stats(c => c.b), a = Stats(c => c.a)
};

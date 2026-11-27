using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace AssetPackTools.Editor
{
    /// <summary>
    /// Converts every .mat under a folder to the project's own shader (default: URP Lit) when the pack's shaders were
    /// not imported. A material keeps its saved properties after a shader swap (and when its shader is missing), so
    /// the source slots can still be read - which also makes this safe to re-run after extracting more assets.
    ///
    /// Adapt per vendor: fill the *Slots arrays with the pack shader's property names (open a .mat in a text editor,
    /// look under m_SavedProperties.m_TexEnvs / m_Floats / m_Colors), and the Target* names with your shader's.
    /// Run: ConvertAll("Assets/<Vendor>/<Pack>") from a snippet, or the menu Tools/Asset Packs/Convert Pack Materials
    /// (which uses DefaultPackRoot).
    /// </summary>
    public static class PackMaterialConverter
    {
        // ------------------------------------------------------------------ configure

        /// <summary>
        /// CHANGE THIS to the folder the extracted pack writes to (the extractor keeps the package's own paths, usually
        /// Assets/&lt;Vendor&gt;/&lt;Pack&gt;). Only the menu item uses it: ConvertAll(root) takes the folder for one run.
        /// Every .mat under it is converted, so never point it at a folder that holds the project's own materials.
        /// </summary>
        public const string DefaultPackRoot = "Assets/ThirdParty";

        private const string TargetShader = "Universal Render Pipeline/Lit";
        private const string TargetBaseMap = "_BaseMap", TargetBaseColor = "_BaseColor";
        private const string TargetNormalMap = "_BumpMap", TargetEmissionMap = "_EmissionMap", TargetEmissionColor = "_EmissionColor";
        private const string TargetCutoff = "_Cutoff", TargetCull = "_Cull", TargetSmoothness = "_Smoothness";

        // First non-null slot wins. These are names common art-pack vendors use plus Unity's standard ones; add this pack's.
        private static readonly string[] BaseSlots =
        {
            "_BaseMap", "_MainTex", "_ColorMap", "_Albedo_Map", "_Base_Texture", "_Texture_Map", "_Base_Albedo",
            "_Leaf_Texture", "_Triplanar_Texture_Top", "_Trunk_Texture",
        };
        private static readonly string[] NormalSlots = { "_BumpMap", "_NormalMap", "_Normal_Map", "_Base_Normal", "_Leaf_Normal" };
        private static readonly string[] EmissionSlots = { "_EmissionMap", "_Emission_Map", "_Emission_Texture" };
        // Vendor names first: a vendor material often also carries Unity's _EmissionColor at its white default.
        private static readonly string[] EmissionColors = { "_Emission_Color", "_Emission_Colour", "_EmissiveColor", "_EmissionColor" };
        private static readonly string[] EmissionIntensityFloats = { "_Emission_Intensity", "_EmissionIntensity", "_Emission_Strength", "_EmissiveIntensity" };
        // An emission toggle the vendor saved as 0 turns emission off even when a map is set.
        private static readonly string[] EmissionFlags = { "_Enable_Emission", "_EmissionEnabled", "_UseEmission" };
        private static readonly string[] AlphaClipFlags = { "_AlphaClip", "_BUILTIN_AlphaClip", "_AlphaCutoffEnable" };
        private static readonly string[] CutoffFloats = { "_Alpha_Clip_Threshold", "_AlphaCutoff", "_Cutoff" };

        /// <summary>
        /// Low-poly packs often colour models from a tiny palette texture (often 32x32).
        /// Bilinear filtering and mipmaps blend palette cells and turn the model grey at distance, so these import
        /// point-sampled, uncompressed, without mipmaps. Search filter for AssetDatabase.FindAssets; empty = skip.
        /// </summary>
        private const string PaletteTextureFilter = "t:Texture2D ColorMap";
        private const int PaletteMaxSize = 64;

        // ------------------------------------------------------------------ run

        [MenuItem("Tools/Asset Packs/Convert Pack Materials")]
        public static void ConvertAllMenu() => Debug.Log(ConvertAll(DefaultPackRoot));

        /// <summary>Converts every .mat under root (an AssetDatabase folder) and returns a one-line summary.</summary>
        public static string ConvertAll(string root = DefaultPackRoot)
        {
            root = (root ?? "").Replace('\\', '/').TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(root))
                return $"No folder '{root}' in the AssetDatabase: pass the pack's folder, ConvertAll(\"Assets/<Vendor>/<Pack>\").";
            var shader = Shader.Find(TargetShader);
            if (shader == null) return $"Shader '{TargetShader}' not found.";
            int converted = 0, embedded = 0, clipped = 0, emissive = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".mat")) { embedded++; continue; } // materials embedded in model files are read-only
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) continue;
                Convert(m, shader);
                converted++;
                if (m.IsKeywordEnabled("_ALPHATEST_ON")) clipped++;
                if (m.IsKeywordEnabled("_EMISSION")) emissive++;
            }
            AssetDatabase.SaveAssets();
            int palettes = FixPaletteTextures(root);
            return $"Pack materials converted under {root}: {converted} ({clipped} alpha-clipped, {emissive} emissive; embedded skipped: {embedded}). Palette textures fixed: {palettes}.";
        }

        private static void Convert(Material m, Shader target)
        {
            bool reconvert = m.shader == target;
            var saved = ReadSaved(m);
            var baseTex = First(saved.textures, BaseSlots);
            var normal = First(saved.textures, NormalSlots);
            var emission = First(saved.textures, EmissionSlots);

            // Many vendor shaders save their alpha-clip toggle on for opaque atlases too. Clip only when the pack asks
            // for it (the toggle, or a foliage name) AND the base texture imports with alpha to clip on; otherwise the
            // material renders opaque.
            bool foliage = saved.textures.ContainsKey("_Leaf_Texture") || m.name.Contains("Leaf") || m.name.Contains("Fern");
            bool alphaClip = (foliage || AnyFlag(saved, AlphaClipFlags)) && HasAlpha(baseTex);
            float cutoff = 0.5f;
            foreach (var k in CutoffFloats)
                if (saved.floats.TryGetValue(k, out var v) && v > 0.01f) { cutoff = v; break; }

            // Emission keeps the vendor's HDR colour and intensity (a glow saved as 4.6 stays 4.6, not white at 1).
            bool emissionOn = emission != null && !FlagOff(saved, EmissionFlags);
            Color emissionColor = Color.white;
            string colorSlot = null;
            foreach (var k in EmissionColors)
                if (saved.colors.TryGetValue(k, out var c)) { emissionColor = c; colorSlot = k; break; }
            // On a re-run, _EmissionColor already holds colour x intensity: don't multiply it again.
            if (!(reconvert && colorSlot == TargetEmissionColor))
                foreach (var k in EmissionIntensityFloats)
                    if (saved.floats.TryGetValue(k, out var i)) { emissionColor *= i; break; }
            emissionColor.a = 1;

            m.shader = target;
            // Start clean: keywords and texture slots left over from the vendor shader (_METALLICGLOSSMAP, a metallic
            // or occlusion map) would still apply. Only the slots set below keep a texture; the vendor's own slot names
            // stay in the saved properties, so a re-run can read them again.
            m.shaderKeywords = new string[0];
            var set = new HashSet<string> { TargetBaseMap };
            m.SetTexture(TargetBaseMap, baseTex);
            // URP keeps the legacy _MainTex in step with _BaseMap (lightmapping and old scripts read it).
            if (target.FindPropertyIndex("_MainTex") >= 0) { m.SetTexture("_MainTex", baseTex); set.Add("_MainTex"); }
            m.SetColor(TargetBaseColor, Color.white);
            m.SetFloat(TargetSmoothness, 0.15f);
            m.SetFloat("_Metallic", 0);

            // URP Lit keyword/toggle names - change when targeting a custom shader.
            m.SetFloat("_Surface", 0);
            m.SetFloat("_AlphaClip", alphaClip ? 1 : 0);
            m.SetFloat("_AlphaToMask", alphaClip ? 1 : 0);
            SetKeyword(m, "_ALPHATEST_ON", alphaClip);
            m.SetOverrideTag("RenderType", alphaClip ? "TransparentCutout" : "Opaque");
            m.renderQueue = alphaClip ? (int)RenderQueue.AlphaTest : -1;
            m.SetFloat(TargetCutoff, cutoff);
            m.SetFloat(TargetCull, foliage ? 0f : 2f); // foliage cards are double sided

            if (normal != null && !foliage) { m.SetTexture(TargetNormalMap, normal); set.Add(TargetNormalMap); }
            SetKeyword(m, "_NORMALMAP", normal != null && !foliage);

            SetKeyword(m, "_EMISSION", emissionOn);
            if (emissionOn)
            {
                m.SetTexture(TargetEmissionMap, emission);
                set.Add(TargetEmissionMap);
                m.SetColor(TargetEmissionColor, emissionColor);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                m.SetColor(TargetEmissionColor, Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }

            for (int p = 0; p < target.GetPropertyCount(); p++)
            {
                if (target.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                string name = target.GetPropertyName(p);
                if (!set.Contains(name) && m.GetTexture(name) != null) m.SetTexture(name, null);
            }

            EditorUtility.SetDirty(m);
        }

        /// <summary>True when the texture imports with an alpha channel that holds the source's alpha.</summary>
        private static bool HasAlpha(Texture tex)
        {
            if (tex == null) return false;
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter ti)
            {
                if (ti.alphaSource == TextureImporterAlphaSource.None) return false;
                if (ti.alphaSource == TextureImporterAlphaSource.FromGrayScale) return true;
                return ti.DoesSourceTextureHaveAlpha();
            }
            return GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat);
        }

        private static int FixPaletteTextures(string root)
        {
            if (string.IsNullOrEmpty(PaletteTextureFilter)) return 0;
            int fixedCount = 0;
            foreach (var guid in AssetDatabase.FindAssets(PaletteTextureFilter, new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null || tex.width > PaletteMaxSize || tex.height > PaletteMaxSize) continue;
                if (!importer.mipmapEnabled && importer.filterMode == FilterMode.Point
                    && importer.textureCompression == TextureImporterCompression.Uncompressed) continue;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Point;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
                fixedCount++;
            }
            return fixedCount;
        }

        // ------------------------------------------------------------------ saved properties

        private class Saved
        {
            public readonly Dictionary<string, Texture> textures = new();
            public readonly Dictionary<string, float> floats = new();
            public readonly Dictionary<string, Color> colors = new();
        }

        /// <summary>Reads m_SavedProperties directly: these survive shader swaps and missing shaders.</summary>
        private static Saved ReadSaved(Material m)
        {
            var saved = new Saved();
            var so = new SerializedObject(m);
            var te = so.FindProperty("m_SavedProperties.m_TexEnvs");
            for (int i = 0; i < te.arraySize; i++)
            {
                var e = te.GetArrayElementAtIndex(i);
                if (e.FindPropertyRelative("second.m_Texture").objectReferenceValue is Texture tex)
                    saved.textures[e.FindPropertyRelative("first").stringValue] = tex;
            }
            var fl = so.FindProperty("m_SavedProperties.m_Floats");
            for (int i = 0; i < fl.arraySize; i++)
            {
                var e = fl.GetArrayElementAtIndex(i);
                saved.floats[e.FindPropertyRelative("first").stringValue] = e.FindPropertyRelative("second").floatValue;
            }
            var co = so.FindProperty("m_SavedProperties.m_Colors");
            for (int i = 0; i < co.arraySize; i++)
            {
                var e = co.GetArrayElementAtIndex(i);
                saved.colors[e.FindPropertyRelative("first").stringValue] = e.FindPropertyRelative("second").colorValue;
            }
            return saved;
        }

        private static Texture First(Dictionary<string, Texture> textures, string[] slots)
        {
            foreach (var s in slots)
                if (textures.TryGetValue(s, out var t) && t != null) return t;
            return null;
        }

        private static bool AnyFlag(Saved s, string[] keys)
        {
            foreach (var k in keys)
                if (s.floats.TryGetValue(k, out var v) && v > 0.5f) return true;
            return false;
        }

        /// <summary>True when one of the toggles is saved and every saved one is off.</summary>
        private static bool FlagOff(Saved s, string[] keys)
        {
            bool any = false;
            foreach (var k in keys)
                if (s.floats.TryGetValue(k, out var v)) { any = true; if (v > 0.5f) return false; }
            return any;
        }

        private static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }
    }
}

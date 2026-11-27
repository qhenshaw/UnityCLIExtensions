using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityCLIExtensions.Assets;
using UnityEditor;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// cliext.import, cliext.presets and cliext.package argument checks: everything a batch or an import resolves before
    /// it changes anything. Nothing here changes the project.
    /// </summary>
    public class ImportCommandsTests
    {
        private const string Self = "Packages/com.qhenshaw.unitycliextensions/Editor/Assets/ImportCommands.cs";

        private static ArgumentException Catch(TestDelegate d) => Assert.Catch<ArgumentException>(d);
        private static void IsUsage(ArgumentException e) => Assert.AreEqual("AnimUsageException", e.GetType().Name, e.Message);
        private static void IsNotFound(ArgumentException e) => Assert.AreEqual("AnimNotFoundException", e.GetType().Name, e.Message);

        [Test]
        public void Folders_OutsideTheProjectAreUsage_MissingOnesAreNotFound_AndFilesAreNotFolders()
        {
            Assert.AreEqual("Assets", ImportCommands.Folder("Assets/"));
            var outside = Catch(() => ImportCommands.Folder("C:/Temp"));
            IsUsage(outside);
            StringAssert.Contains("isn't under Assets/ or Packages/", outside.Message);
            var missing = Catch(() => ImportCommands.Folder("Assets/NoSuchFolder_Cliext"));
            IsNotFound(missing);
            StringAssert.Contains("asset create_folder", missing.Message);
            var file = Catch(() => ImportCommands.Folder(Self));
            IsUsage(file);
            StringAssert.Contains("is a file, not a folder", file.Message);
            StringAssert.Contains("--filter ImportCommands.cs", file.Message);
        }

        [Test]
        public void AssetTypes_AreCheckedWithDidYouMean()
        {
            Assert.AreEqual("Texture2D", ImportCommands.AssetType("texture2d"));
            Assert.AreEqual("Model", ImportCommands.AssetType("model"));
            Assert.AreEqual("", ImportCommands.AssetType(" "));
            var e = Catch(() => ImportCommands.AssetType("Texure2D"));
            IsUsage(e);
            StringAssert.StartsWith("--type: no asset type 'Texure2D'. Did you mean: Texture2D", e.Message);
        }

        [Test]
        public void SetPairs_NeedNameEqualsValue_EachNameOnce()
        {
            var pairs = ImportCommands.Pairs("maxTextureSize=1024; mipmapEnabled = false;");
            Assert.AreEqual(2, pairs.Count);
            Assert.AreEqual("mipmapEnabled", pairs[1].Key);
            Assert.AreEqual("false", pairs[1].Value);
            StringAssert.Contains("'maxTextureSize' isn't name=value", Catch(() => ImportCommands.Pairs("maxTextureSize")).Message);
            StringAssert.Contains("give name=value pairs", Catch(() => ImportCommands.Pairs(" ; ")).Message);
            StringAssert.Contains("names maxTextureSize twice", Catch(() => ImportCommands.Pairs("maxTextureSize=1;MaxTextureSize=2")).Message);
        }

        [Test]
        public void Settings_UnknownNamesListTheValidOnes_AndObsoleteOrComplexOnesAreLeftOut()
        {
            var settable = ImportCommands.Settable(typeof(TextureImporter));
            Assert.IsTrue(settable.ContainsKey("maxTextureSize"));
            Assert.IsTrue(settable.ContainsKey("MIPMAPENABLED"), "Names match without case.");
            Assert.IsFalse(settable.ContainsKey("name"), "Object.name is not an import setting.");
            Assert.IsFalse(settable.ContainsKey("spritesheet"), "Arrays can't be set from text.");
            Assert.IsFalse(settable.ContainsKey("grayscaleToAlpha"), "Obsolete properties are left out.");
            var e = Catch(() => ImportCommands.Setting(typeof(TextureImporter), "maxTexureSize"));
            IsUsage(e);
            StringAssert.StartsWith("--set: TextureImporter has no setting 'maxTexureSize'. Did you mean: maxTextureSize? Settings: ", e.Message);
            StringAssert.Contains("mipmapEnabled", e.Message);
            Assert.IsTrue(ImportCommands.Settable(typeof(ModelImporter)).ContainsKey("globalScale"));
        }

        [Test]
        public void Values_BadNumbersBoolsAndEnums_AreOneLineUsageErrors()
        {
            var size = ImportCommands.Setting(typeof(TextureImporter), "maxTextureSize");
            Assert.AreEqual(1024, ImportCommands.Value(size, "1024"));
            Assert.AreEqual("maxTextureSize: 'big' isn't a Int32 value: give a number such as 1 or 0.5.", Catch(() => ImportCommands.Value(size, "big")).Message);
            StringAssert.StartsWith("maxTextureSize: 1000 isn't a texture size: give one of 32,", Catch(() => ImportCommands.Value(size, "1000")).Message);
            var mip = ImportCommands.Setting(typeof(TextureImporter), "mipmapEnabled");
            Assert.AreEqual("mipmapEnabled: 'maybe' isn't a Boolean value: give true or false.", Catch(() => ImportCommands.Value(mip, "maybe")).Message);
            var kind = ImportCommands.Setting(typeof(TextureImporter), "textureType");
            Assert.AreEqual(TextureImporterType.NormalMap, ImportCommands.Value(kind, "normalmap"));
            Assert.AreEqual(TextureImporterType.Sprite, ImportCommands.Value(kind, ((int)TextureImporterType.Sprite).ToString()));
            var e = Catch(() => ImportCommands.Value(kind, "Normal"));
            IsUsage(e);
            StringAssert.StartsWith("textureType: 'Normal' isn't a TextureImporterType. Did you mean: NormalMap? Use one of: Default, NormalMap,", e.Message);
            StringAssert.DoesNotContain("Bump", e.Message, "Obsolete enum names are not offered.");
            StringAssert.Contains("isn't a TextureImporterType", Catch(() => ImportCommands.Value(kind, "99")).Message);
        }

        [Test]
        public void Platforms_AreCheckedWithDidYouMean_AndIosIsIPhone()
        {
            Assert.IsNull(ImportCommands.Platform(""));
            Assert.IsNull(ImportCommands.Platform("default"));
            Assert.AreEqual("Android", ImportCommands.Platform("android"));
            Assert.AreEqual("iPhone", ImportCommands.Platform("iOS"));
            var e = Catch(() => ImportCommands.Platform("Andriod"));
            IsUsage(e);
            StringAssert.StartsWith("--platform: no texture platform 'Andriod'. Did you mean: Android? Platforms: ", e.Message);
        }

        [Test]
        public void PresetArguments_MissingIsNotFound_OtherTypesAreUsage()
        {
            var missing = Catch(() => ImportCommands.LoadPreset("Assets/NoSuch_Cliext.preset"));
            IsNotFound(missing);
            StringAssert.Contains("cliext.presets create --from <asset> --out Assets/NoSuch_Cliext.preset", missing.Message);
            var script = Catch(() => ImportCommands.LoadPreset(Self));
            IsUsage(script);
            StringAssert.Contains("is a MonoScript, not a Preset", script.Message);
            IsUsage(Catch(() => ImportCommands.LoadPreset("D:/x.preset")));
        }

        [Test]
        public void DefaultFilters_FoldersBecomeGlobs_MissingFoldersAreNotFound()
        {
            Assert.AreEqual("", ImportCommands.DefaultFilter(""));
            Assert.AreEqual("glob:\"Assets/**\"", ImportCommands.DefaultFilter("Assets/"));
            Assert.AreEqual("glob:\"Assets/Art/**/*.png\"", ImportCommands.DefaultFilter("glob:\"Assets/Art/**/*.png\""));
            var e = Catch(() => ImportCommands.DefaultFilter("Assets/NoSuch_Cliext"));
            IsNotFound(e);
            StringAssert.Contains("asset create_folder", e.Message);
            IsUsage(Catch(() => ImportCommands.DefaultFilter("C:/Art")));
        }

        private static readonly string[] TexturePaths =
        {
            "m_EnableMipMap", "m_sRGBTexture", "m_MaxTextureSize", "m_TextureSettings.m_FilterMode", "m_TextureSettings.m_WrapU", "m_AlphaUsage",
            "m_PlatformSettings.Array.size", "m_PlatformSettings.Array.data[0].m_MaxTextureSize", "m_PlatformSettings.Array.data[0].m_TextureCompression", "m_UserData"
        };

        [Test]
        public void PresetCreate_Only_KeepsTheNamedAndThePlatformBlock_ExcludeLeavesOut()
        {
            CollectionAssert.IsEmpty(ImportCommands.Exclusions(TexturePaths, "", ""));
            CollectionAssert.AreEqual(new[] { "m_sRGBTexture", "m_TextureSettings", "m_AlphaUsage", "m_UserData" },
                ImportCommands.Exclusions(TexturePaths, "enableMipMap;M_MAXTEXTURESIZE", ""), "--only leaves the platform settings whole.");
            CollectionAssert.AreEqual(new[] { "m_sRGBTexture", "m_TextureSettings.m_WrapU", "m_AlphaUsage", "m_UserData" },
                ImportCommands.Exclusions(TexturePaths, "m_EnableMipMap;m_MaxTextureSize;m_TextureSettings.m_FilterMode", ""), "A kept sub-property keeps its parent's other parts listed one by one.");
            CollectionAssert.AreEqual(new[] { "m_sRGBTexture", "m_AlphaUsage" }, ImportCommands.Exclusions(TexturePaths, "", "sRGBTexture;alphaUsage"));
            CollectionAssert.AreEqual(new[] { "m_sRGBTexture", "m_TextureSettings", "m_AlphaUsage", "m_UserData", "m_PlatformSettings" },
                ImportCommands.Exclusions(TexturePaths, "enableMipMap;maxTextureSize", "m_PlatformSettings"), "Only a named m_PlatformSettings drops the block.");

            var unknown = Catch(() => ImportCommands.Exclusions(TexturePaths, "maxTexureSize", ""));
            IsUsage(unknown);
            StringAssert.StartsWith("--only: the preset has no property 'maxTexureSize'. Did you mean: m_MaxTextureSize? Properties: m_EnableMipMap,", unknown.Message);
            var inside = Catch(() => ImportCommands.Exclusions(TexturePaths, "", "m_PlatformSettings.Array.data[0].m_TextureCompression"));
            IsUsage(inside);
            StringAssert.Contains("keeps or excludes whole: name m_PlatformSettings", inside.Message);
            StringAssert.Contains("both name m_MaxTextureSize", Catch(() => ImportCommands.Exclusions(TexturePaths, "maxTextureSize", "maxTextureSize")).Message);
            StringAssert.Contains("no properties", Catch(() => ImportCommands.Exclusions(new[] { "m_A", "m_B" }, "", "m_A;m_B")).Message);
        }

        [Test]
        public void PresetAudit_WithNoDefaults_SaysNothingWasAudited()
        {
            var r = ImportCommands.RunAudit("Packages/com.qhenshaw.unitycliextensions/Editor/Assets", 12);
            Assert.AreEqual(0, r.Audited);
            CollectionAssert.IsEmpty(r.Breaking);
            StringAssert.Contains("has a default preset, so nothing was audited", r.Note);
            StringAssert.Contains("set_default --preset", ImportCommands.NoDefaultsNote);
        }

        [Test]
        public void PresetSetDefault_WritesThePresetManagerFile()
        {
            const string presetPath = "Assets/CliextTest_SetDefault.preset";
            string file = Path.GetFullPath(ImportCommands.PresetManagerAsset);
            byte[] before = File.ReadAllBytes(file);
            var mat = new UnityEngine.GameObject("CliextTest", typeof(UnityEngine.BoxCollider));
            var preset = new UnityEditor.Presets.Preset(mat.GetComponent<UnityEngine.BoxCollider>());
            AssetDatabase.CreateAsset(preset, presetPath);
            var type = preset.GetPresetType();
            var original = UnityEditor.Presets.Preset.GetDefaultPresetsForType(type);
            try
            {
                Assert.IsTrue(type.IsValidDefault(), "A BoxCollider preset can be a default.");
                Assert.IsTrue(ImportCommands.SetDefaults(type, original.Prepend(new UnityEditor.Presets.DefaultPreset("", preset, true)).ToArray()));
                string guid = AssetDatabase.AssetPathToGUID(presetPath);
                StringAssert.Contains(guid, File.ReadAllText(file), "set_default writes ProjectSettings/PresetManager.asset, not only the Editor's copy.");
            }
            finally
            {
                UnityEditor.Presets.Preset.SetDefaultPresetsForType(type, original);
                AssetDatabase.DeleteAsset(presetPath);
                UnityEngine.Object.DestroyImmediate(mat);
                File.WriteAllBytes(file, before);
            }
        }

        // .unitypackage files

        private string m_Dir;

        [SetUp]
        public void MakeDir() { m_Dir = Path.Combine(Path.GetTempPath(), "cliext-pkgtest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(m_Dir); }

        [TearDown]
        public void RemoveDir() { if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true); }

        private static void Entry(Stream s, string name, byte[] data)
        {
            var h = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
            Encoding.ASCII.GetBytes("0000644\0").CopyTo(h, 100);
            Encoding.ASCII.GetBytes(Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
            h[156] = (byte)'0';
            Encoding.ASCII.GetBytes("ustar\0").CopyTo(h, 257);
            s.Write(h, 0, 512);
            s.Write(data, 0, data.Length);
            int pad = (512 - data.Length % 512) % 512;
            s.Write(new byte[pad], 0, pad);
        }

        private string Package(string name, params (string guid, string path, string asset)[] assets)
        {
            string file = Path.Combine(m_Dir, name);
            using (var fs = File.Create(file))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            {
                foreach (var a in assets)
                {
                    Entry(gz, $"{a.guid}/pathname", Encoding.UTF8.GetBytes(a.path + "\n00"));
                    Entry(gz, $"{a.guid}/asset.meta", Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {a.guid}\n"));
                    if (a.asset != null) Entry(gz, $"{a.guid}/asset", Encoding.UTF8.GetBytes(a.asset));
                }
                gz.Write(new byte[1024], 0, 1024);
            }
            return file;
        }

        [Test]
        public void Packages_AreReadWithPathsAndMeta_FoldersHaveNoFile()
        {
            string file = Package("ok.unitypackage",
                ("0123456789abcdef0123456789abcdef", "Assets/Cliext/Notes.txt", "hello"),
                ("fedcba9876543210fedcba9876543210", "Assets/Cliext", null));
            var entries = UnityPackageFile.Read(file, Path.Combine(m_Dir, "x"));
            CollectionAssert.AreEqual(new[] { "Assets/Cliext", "Assets/Cliext/Notes.txt" }, entries.Select(e => e.path).ToArray(), "The old 00 line after the path is dropped; folders sort first.");
            Assert.IsTrue(entries[0].IsFolder);
            Assert.AreEqual("hello", File.ReadAllText(entries[1].assetFile));
            StringAssert.Contains("guid: 0123456789abcdef0123456789abcdef", Encoding.UTF8.GetString(entries[1].meta));
        }

        [Test]
        public void Packages_ThatArentOne_OrWritOutsideAssets_AreUsageErrors()
        {
            string text = Path.Combine(m_Dir, "text.unitypackage");
            File.WriteAllText(text, "not a package");
            var e = Catch(() => UnityPackageFile.Read(text, m_Dir));
            IsUsage(e);
            StringAssert.Contains("it isn't a gzip archive", e.Message);

            string gzOnly = Path.Combine(m_Dir, "gz.unitypackage");
            using (var fs = File.Create(gzOnly))
            using (var gz = new GZipStream(fs, CompressionMode.Compress)) { var b = Encoding.ASCII.GetBytes(new string('x', 600)); gz.Write(b, 0, b.Length); }
            StringAssert.Contains("not a tar archive", Catch(() => UnityPackageFile.Read(gzOnly, m_Dir)).Message);

            string empty = Package("empty.unitypackage");
            StringAssert.Contains("the archive is empty", Catch(() => UnityPackageFile.Read(empty, m_Dir)).Message);

            foreach (string bad in new[] { "../Outside.txt", "Assets/../../Outside.txt", "ProjectSettings/TagManager.asset", "Packages/com.nobody.nothing/x.txt" })
            {
                string file = Package("bad.unitypackage", ("0123456789abcdef0123456789abcdef", bad, "x"));
                var ex = Catch(() => UnityPackageFile.Read(file, Path.Combine(m_Dir, "x")));
                IsUsage(ex);
                StringAssert.Contains("Nothing was imported", ex.Message, bad);
                File.Delete(file);
            }
        }
    }
}

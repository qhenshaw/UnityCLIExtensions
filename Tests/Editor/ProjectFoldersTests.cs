using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// ProjectFolders.Ensure, which every cliext command that creates an asset uses for its folder: missing folders are
    /// made, and a folder already on disk but not imported is imported instead of made again as "&lt;Folder&gt; 1".
    /// </summary>
    public class ProjectFoldersTests
    {
        private const string Root = "Assets/CliExtFolderTests";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            if (File.Exists(Root + ".meta")) File.Delete(Root + ".meta");
            AssetDatabase.Refresh();
        }

        [Test]
        public void Ensure_MakesMissingFolders_OutermostFirst()
        {
            var made = ProjectFolders.Ensure($"{Root}/A/B");
            CollectionAssert.AreEqual(new[] { Root, $"{Root}/A", $"{Root}/A/B" }, made);
            Assert.IsTrue(AssetDatabase.IsValidFolder($"{Root}/A/B"));
            Assert.IsEmpty(ProjectFolders.Ensure($"{Root}/A/B"), "A folder that is there is left alone.");
        }

        [Test]
        public void Ensure_ImportsAFolderOnDisk_InsteadOfMakingAFolderNamedOne()
        {
            ProjectFolders.Ensure(Root);
            Directory.CreateDirectory($"{Root}/OnDisk/Inner"); // as New-Item or mkdir would, with no import
            Assert.IsFalse(AssetDatabase.IsValidFolder($"{Root}/OnDisk"), "The test needs a folder Unity hasn't imported yet.");
            ProjectFolders.Ensure($"{Root}/OnDisk/Inner/New");
            Assert.IsTrue(AssetDatabase.IsValidFolder($"{Root}/OnDisk/Inner/New"));
            Assert.IsFalse(Directory.Exists($"{Root}/OnDisk 1") || Directory.Exists($"{Root}/OnDisk/Inner 1"), "A second folder was made beside the one on disk.");
            CollectionAssert.AreEquivalent(new[] { $"{Root}/OnDisk" }, AssetDatabase.GetSubFolders(Root));
        }

        [Test]
        public void Ensure_OutsideAssets_Throws()
        {
            Assert.Throws<ArgumentException>(() => ProjectFolders.Ensure("NotAssets/X"));
            Assert.Throws<ArgumentException>(() => ProjectFolders.Ensure("Packages"));
            Assert.IsFalse(ProjectFolders.IsProjectFolder("Packages/"));
            Assert.IsTrue(ProjectFolders.IsProjectFolder("Assets"));
        }
    }
}

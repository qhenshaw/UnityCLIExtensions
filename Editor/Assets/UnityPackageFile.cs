using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityCLIExtensions.Animation;
using UnityEditor;

namespace UnityCLIExtensions.Assets
{
    /// <summary>One asset in a .unitypackage: its GUID, its project path, and where its extracted file and .meta are.</summary>
    internal sealed class UnityPackageEntry
    {
        public string guid;
        public string path;
        public string assetFile;   // extracted file, null for a folder
        public byte[] meta;        // the .meta text, null when the package has none
        public bool IsFolder => assetFile == null;
    }

    /// <summary>What <see cref="UnityPackageFile.Import"/> wrote: per asset, created, replaced or unchanged.</summary>
    internal sealed class UnityPackageResult
    {
        public readonly List<string> created = new List<string>();
        public readonly List<string> replaced = new List<string>();
        public readonly List<string> unchanged = new List<string>();
        public readonly List<string> movedByGuid = new List<string>();
    }

    /// <summary>
    /// Reads and imports a .unitypackage (a gzip tar of &lt;guid&gt;/pathname, asset and asset.meta) without Unity's
    /// import pipeline: no Import window and no "Missing signature" dialog (which Unity 6.3 and later show for every
    /// unsigned package and which stops the Editor's main thread), and the import is finished when the call returns.
    /// </summary>
    /// <remarks>
    /// Everything is read and checked before the project changes: a file that isn't a gzip tar, holds no assets, or has
    /// a path outside Assets/ (or an existing Packages/ folder) is invalid usage. An asset whose GUID is already in the
    /// project at another path is written there, as Unity's import does. If writing fails part-way, the files written
    /// are deleted and the replaced ones put back.
    /// </remarks>
    internal static class UnityPackageFile
    {
        /// <summary>Reads the package, extracting the asset files into <paramref name="extractTo"/>.</summary>
        public static List<UnityPackageEntry> Read(string file, string extractTo)
        {
            using (var fs = File.OpenRead(file))
            {
                int b1 = fs.ReadByte(), b2 = fs.ReadByte();
                if (b1 != 0x1f || b2 != 0x8b)
                    throw new AnimUsageException($"'{file}' isn't a .unitypackage: it isn't a gzip archive. Export one with AssetDatabase.ExportPackage (Assets > Export Package).");
                fs.Position = 0;
                try
                {
                    using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                        return ReadTar(gz, file, extractTo);
                }
                catch (InvalidDataException e) { throw new AnimUsageException($"'{file}' is damaged: {e.Message}"); }
                catch (EndOfStreamException) { throw new AnimUsageException($"'{file}' is damaged: it ends part-way through an entry."); }
            }
        }

        private static List<UnityPackageEntry> ReadTar(Stream tar, string file, string extractTo)
        {
            var byGuid = new Dictionary<string, UnityPackageEntry>();
            var header = new byte[512];
            string longName = null;
            int entries = 0;
            while (ReadFull(tar, header, 512, allowEof: true))
            {
                if (header.All(b => b == 0)) break;
                bool ustar = Encoding.ASCII.GetString(header, 257, 5) == "ustar";
                string name = longName ?? Text(header, 0, 100);
                if (longName == null && ustar && Text(header, 345, 155) is string prefix && prefix.Length > 0) name = prefix + "/" + name;
                longName = null;
                long size = Octal(header, 124, 12, file);
                char kind = (char)header[156];
                entries++;
                if (kind == 'L')
                {
                    var buf = new byte[size];
                    ReadFull(tar, buf, (int)size, allowEof: false);
                    Skip(tar, Pad(size));
                    longName = Encoding.UTF8.GetString(buf).TrimEnd('\0');
                    continue;
                }
                name = name.Replace('\\', '/');
                if (name.StartsWith("./")) name = name.Substring(2);
                int slash = name.IndexOf('/');
                string part = slash > 0 ? name.Substring(slash + 1) : "";
                if ((kind != '0' && kind != '\0') || slash <= 0 || part.Contains("/"))
                {
                    Skip(tar, size + Pad(size));
                    continue;
                }
                string guid = name.Substring(0, slash);
                if (!byGuid.TryGetValue(guid, out var entry)) byGuid[guid] = entry = new UnityPackageEntry { guid = guid };
                switch (part)
                {
                    case "pathname":
                    {
                        var buf = new byte[size];
                        ReadFull(tar, buf, (int)size, allowEof: false);
                        entry.path = Encoding.UTF8.GetString(buf).Split('\n')[0].Trim().Replace('\\', '/');
                        break;
                    }
                    case "asset.meta":
                    {
                        entry.meta = new byte[size];
                        ReadFull(tar, entry.meta, (int)size, allowEof: false);
                        break;
                    }
                    case "asset":
                    {
                        entry.assetFile = Path.Combine(extractTo, guid);
                        Directory.CreateDirectory(extractTo);
                        using (var outFile = File.Create(entry.assetFile)) Copy(tar, outFile, size);
                        break;
                    }
                    default:
                        Skip(tar, size);
                        break;
                }
                Skip(tar, Pad(size));
            }
            if (entries == 0) throw new AnimUsageException($"'{file}' isn't a .unitypackage: the archive is empty.");
            var result = byGuid.Values.Where(e => e.path != null && e.path != "Assets").OrderBy(e => e.path, StringComparer.Ordinal).ToList();
            if (result.Count == 0) throw new AnimUsageException($"'{file}' isn't a .unitypackage: it holds no assets (no <guid>/pathname entries).");
            foreach (var e in result) Check(e, file);
            return result;
        }

        /// <summary>A package path is safe to write: under Assets/ (or an existing Packages/ folder), relative, no "..".</summary>
        internal static void Check(UnityPackageEntry e, string file)
        {
            string p = e.path;
            bool assets = p == "Assets" || p.StartsWith("Assets/", StringComparison.Ordinal);
            bool packages = p.StartsWith("Packages/", StringComparison.Ordinal);
            if ((!assets && !packages) || Path.IsPathRooted(p) || p.Split('/').Any(s => s == ".." || s == "." || s.Length == 0))
                throw new AnimUsageException($"'{file}' has an asset at '{p}', outside Assets/: it can't be imported here. Nothing was imported.");
            if (packages)
            {
                string[] parts = p.Split('/');
                if (parts.Length < 3 || !Directory.Exists(Path.Combine("Packages", parts[1])))
                    throw new AnimUsageException($"'{file}' has an asset at '{p}', in a package this project doesn't have: only Assets/ and existing Packages/ folders are imported. Nothing was imported.");
            }
            if (e.guid.Length != 32 || !e.guid.All(Uri.IsHexDigit))
                throw new AnimUsageException($"'{file}' has an entry '{e.guid}' that isn't a GUID: it isn't a .unitypackage Unity wrote.");
        }

        /// <summary>
        /// Writes the entries into the project, then refreshes the AssetDatabase. Returns what changed; throws (leaving
        /// the project as it was) when a file can't be written.
        /// </summary>
        public static UnityPackageResult Import(List<UnityPackageEntry> entries, string backupDir, bool dryRun)
        {
            var result = new UnityPackageResult();
            var targets = new List<(UnityPackageEntry e, string path)>();
            foreach (var e in entries)
            {
                string at = AssetDatabase.GUIDToAssetPath(e.guid);
                string path = e.path;
                if (!string.IsNullOrEmpty(at) && at != e.path && (File.Exists(at) || Directory.Exists(at)))
                {
                    path = at;
                    result.movedByGuid.Add($"{e.path} -> {at}");
                }
                targets.Add((e, path));
            }
            foreach (var (e, path) in targets)
            {
                bool exists = e.IsFolder ? Directory.Exists(path) : File.Exists(path);
                bool sameMeta = e.meta == null || (File.Exists(path + ".meta") && File.ReadAllBytes(path + ".meta").SequenceEqual(e.meta));
                bool sameAsset = e.IsFolder ? exists : exists && SameFile(e.assetFile, path);
                (exists ? (sameAsset && sameMeta ? result.unchanged : result.replaced) : result.created).Add(path);
            }
            if (dryRun || result.created.Count + result.replaced.Count == 0) return result;

            var written = new List<string>();
            var dirs = new List<string>();
            var backups = new List<(string original, string copy)>();
            try
            {
                int n = 0;
                foreach (var (e, path) in targets)
                {
                    if (result.unchanged.Contains(path)) continue;
                    foreach (string f in e.IsFolder ? new[] { path + ".meta" } : new[] { path, path + ".meta" })
                        if (File.Exists(f))
                        {
                            Directory.CreateDirectory(backupDir);
                            string copy = Path.Combine(backupDir, (n++).ToString());
                            File.Copy(f, copy);
                            backups.Add((f, copy));
                        }
                    string dir = e.IsFolder ? path : Path.GetDirectoryName(path);
                    MakeDirs(dir, dirs);
                    if (!e.IsFolder) { written.Add(path); File.Copy(e.assetFile, path, true); }
                    if (e.meta != null) { written.Add(path + ".meta"); File.WriteAllBytes(path + ".meta", e.meta); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                foreach (string f in written) try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
                foreach (var (original, copy) in backups) try { File.Copy(copy, original, true); } catch (IOException) { }
                foreach (string d in dirs.AsEnumerable().Reverse()) try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (IOException) { }
                throw new IOException($"Couldn't write the package's files ({ex.Message}); the files it wrote were removed and the replaced ones put back. Nothing was imported.");
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return result;
        }

        private static void MakeDirs(string dir, List<string> made)
        {
            if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;
            MakeDirs(Path.GetDirectoryName(dir), made);
            Directory.CreateDirectory(dir);
            made.Add(dir);
        }

        private static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        private static long Pad(long size) => (512 - size % 512) % 512;

        private static string Text(byte[] b, int at, int len)
        {
            int end = at;
            while (end < at + len && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, at, end - at);
        }

        private static long Octal(byte[] b, int at, int len, string file)
        {
            string s = Text(b, at, len).Trim(' ', '\0');
            if (s.Length == 0) return 0;
            try { return Convert.ToInt64(s, 8); }
            catch (Exception e) when (e is FormatException || e is ArgumentException || e is OverflowException)
            { throw new AnimUsageException($"'{file}' isn't a .unitypackage: it is a gzip file but not a tar archive."); }
        }

        private static bool ReadFull(Stream s, byte[] buf, int count, bool allowEof)
        {
            int read = 0;
            while (read < count)
            {
                int r = s.Read(buf, read, count - read);
                if (r <= 0)
                {
                    if (read == 0 && allowEof) return false;
                    throw new EndOfStreamException();
                }
                read += r;
            }
            return true;
        }

        private static void Skip(Stream s, long count)
        {
            var buf = new byte[8192];
            while (count > 0)
            {
                int r = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (r <= 0) throw new EndOfStreamException();
                count -= r;
            }
        }

        private static void Copy(Stream from, Stream to, long count)
        {
            var buf = new byte[81920];
            while (count > 0)
            {
                int r = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (r <= 0) throw new EndOfStreamException();
                to.Write(buf, 0, r);
                count -= r;
            }
        }
    }
}

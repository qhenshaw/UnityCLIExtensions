using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>A test assembly and how it runs.</summary>
    [Serializable]
    public class TestAssemblyInfo
    {
        public string name;
        /// <summary><c>EditMode</c> (an Editor-only assembly) or <c>PlayMode</c>.</summary>
        public string mode;
        /// <summary>Why it is affected: the changed assembly it is, or the one it depends on.</summary>
        public string because;
    }

    /// <summary>The changed files mapped to the test assemblies that cover them.</summary>
    [Serializable]
    public class AffectedTestsPlan
    {
        public string[] changedFiles = Array.Empty<string>();
        /// <summary>Assemblies (without .dll) that the changed scripts and .asmdef files belong to.</summary>
        public string[] changedAssemblies = Array.Empty<string>();
        /// <summary>Changed files that are not scripts or assembly definitions (prefabs, scenes, data): no assembly owns them.</summary>
        public string[] otherChanges = Array.Empty<string>();
        public List<TestAssemblyInfo> testAssemblies = new List<TestAssemblyInfo>();
    }

    /// <summary>
    /// Works out which test assemblies a change can affect: each changed script or .asmdef belongs to an assembly, and a
    /// test assembly is affected when it is one of those or depends on one, directly or through others.
    /// </summary>
    public static class AffectedTests
    {
        /// <summary>The project's changed files according to git: uncommitted changes against <paramref name="since"/>, plus untracked files.</summary>
        public static List<string> ChangedFilesFromGit(string since = "HEAD")
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Git($"diff --name-only --relative {since} --")) files.Add(line);
            foreach (string line in Git("ls-files --others --exclude-standard")) files.Add(line);
            return files.Select(f => f.Replace('\\', '/')).OrderBy(f => f).ToList();
        }

        /// <summary>Maps changed files (project-relative) to the test assemblies they affect.</summary>
        public static AffectedTestsPlan Plan(IEnumerable<string> files)
        {
            var plan = new AffectedTestsPlan { changedFiles = files.Select(f => f.Replace('\\', '/')).Distinct().ToArray() };
            var all = AllAssemblies();
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var other = new List<string>();

            foreach (string file in plan.changedFiles)
            {
                string owner = OwnerAssembly(file);
                if (owner != null) changed.Add(owner);
                else if (!file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) other.Add(file);
            }
            plan.changedAssemblies = changed.OrderBy(n => n).ToArray();
            plan.otherChanges = other.ToArray();
            plan.testAssemblies = TestAssembliesDependingOn(changed, all);
            return plan;
        }

        /// <summary>Every test assembly in the project, for a full run.</summary>
        public static List<TestAssemblyInfo> AllTestAssemblies()
        {
            return AllAssemblies().Values.Where(IsTestAssembly)
                .Select(a => new TestAssemblyInfo { name = a.name, mode = ModeOf(a), because = "full run" })
                .OrderBy(t => t.name).ToList();
        }

        /// <summary>
        /// The test assemblies among <paramref name="all"/> that are in <paramref name="changed"/> or depend on one of
        /// them. Public for tests.
        /// </summary>
        public static List<TestAssemblyInfo> TestAssembliesDependingOn(ICollection<string> changed, IDictionary<string, Assembly> all)
        {
            var result = new List<TestAssemblyInfo>();
            foreach (Assembly assembly in all.Values.Where(IsTestAssembly))
            {
                string because = FirstChangedDependency(assembly, changed, new HashSet<string>());
                if (because != null) result.Add(new TestAssemblyInfo { name = assembly.name, mode = ModeOf(assembly), because = because });
            }
            return result.OrderBy(t => t.name).ToList();
        }

        private static string FirstChangedDependency(Assembly assembly, ICollection<string> changed, HashSet<string> seen)
        {
            if (!seen.Add(assembly.name)) return null;
            if (changed.Contains(assembly.name)) return assembly.name;
            foreach (Assembly reference in assembly.assemblyReferences)
            {
                string hit = FirstChangedDependency(reference, changed, seen);
                if (hit != null) return hit;
            }
            return null;
        }

        private static readonly Dictionary<string, bool> s_IsTest = new Dictionary<string, bool>();

        /// <summary>
        /// True for a test assembly: its .asmdef has the <c>UNITY_INCLUDE_TESTS</c> define constraint (what the Test
        /// Framework's "Create Test Assembly" writes) or the old <c>optionalUnityReferences: TestAssemblies</c>. Checking
        /// NUnit references doesn't work: on Unity 6 every assembly references nunit.framework.dll.
        /// </summary>
        public static bool IsTestAssembly(Assembly assembly)
        {
            if (s_IsTest.TryGetValue(assembly.name, out bool cached)) return cached;
            bool result = false;
            string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
            if (!string.IsNullOrEmpty(asmdef))
            {
                try
                {
                    string full = Path.IsPathRooted(asmdef) ? asmdef : Path.GetFullPath(Path.Combine(CliExtPaths.ProjectRoot, asmdef));
                    var data = JsonUtility.FromJson<AsmdefTestInfo>(File.ReadAllText(full));
                    result = (data.defineConstraints?.Contains("UNITY_INCLUDE_TESTS") ?? false)
                             || (data.optionalUnityReferences?.Contains("TestAssemblies") ?? false);
                }
                catch { /* unreadable asmdef: not a test assembly */ }
            }
            return s_IsTest[assembly.name] = result;
        }

        [Serializable]
        private class AsmdefTestInfo
        {
            public string[] defineConstraints;
            public string[] optionalUnityReferences;
        }

        private static string ModeOf(Assembly assembly) =>
            (assembly.flags & AssemblyFlags.EditorAssembly) != 0 ? "EditMode" : "PlayMode";

        private static Dictionary<string, Assembly> AllAssemblies()
        {
            var map = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly a in CompilationPipeline.GetAssemblies(AssembliesType.Editor)) map[a.name] = a;
            foreach (Assembly a in CompilationPipeline.GetAssemblies(AssembliesType.Player)) if (!map.ContainsKey(a.name)) map[a.name] = a;
            return map;
        }

        private static string OwnerAssembly(string file)
        {
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                string dll = CompilationPipeline.GetAssemblyNameFromScriptPath(file);
                return string.IsNullOrEmpty(dll) ? null : Path.GetFileNameWithoutExtension(dll);
            }
            if (file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string full = Path.Combine(CliExtPaths.ProjectRoot, file);
                    if (file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                        return JsonUtility.FromJson<AsmdefName>(File.ReadAllText(full))?.name;
                }
                catch { /* unreadable: treat as another change */ }
            }
            return null;
        }

        [Serializable]
        private class AsmdefName { public string name; }

        private static IEnumerable<string> Git(string arguments)
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = CliExtPaths.ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments} failed: {error.Trim()}");
            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
        }
    }
}

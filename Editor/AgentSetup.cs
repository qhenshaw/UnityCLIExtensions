using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Keeps the agent-facing files of a project current with the installed package: the skills in the package's
    /// <c>Skills~/</c> (each copied into <c>.claude/skills/&lt;name&gt;/</c>; <c>unity-cliext</c> always, the others only when
    /// the project has the package they are about, see <see cref="SkillRequirements"/>) and a marked block in the
    /// project's <c>CLAUDE.md</c> that points agents at them. Runs when the package loads (so on install and on every
    /// update), only writes what differs, and leaves everything outside its own files and block alone.
    /// </summary>
    [InitializeOnLoad]
    public static class AgentSetup
    {
        public const string SkillName = "unity-cliext";
        public const string VfxSkillName = "unity-cliext-vfx";
        private const string BlockBegin = "<!-- BEGIN unity-cliext";
        private const string BlockEnd = "<!-- END unity-cliext -->";
        private static readonly Regex s_Block = new Regex(@"<!-- BEGIN unity-cliext.*?<!-- END unity-cliext -->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex s_Stamp = new Regex(@"<!-- (?<skill>[A-Za-z0-9_-]+) (?<version>[^\s:]+): installed by Unity CLI Extensions", RegexOptions.Compiled);

        /// <summary>Skills that only apply when the project has a package: skill name to package name.</summary>
        public static readonly IReadOnlyDictionary<string, string> SkillRequirements = new Dictionary<string, string>
        {
            { VfxSkillName, "com.unity.visualeffectgraph" },
        };

        static AgentSetup()
        {
            if (Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess()) return;
            EditorDeferred.Run(() =>
            {
                if (!CliExtSettings.instance.ManageAgentFiles) return;
                try
                {
                    var result = Install(force: false);
                    if (result.changes.Count > 0) Debug.Log($"[CLI Extensions] Agent files updated for {result.version}: {string.Join("; ", result.changes)}");
                }
                catch (Exception e) { Debug.LogWarning($"[CLI Extensions] Couldn't update the agent files: {e.Message}"); }
            }, label: "CLI Extensions agent files");
        }

        public sealed class Result
        {
            public string version;
            public string skill;
            public string claudeMd;
            public List<string> changes = new List<string>();
            public List<string> kept = new List<string>();
        }

        private static string PackageVersion => UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AgentSetup).Assembly)?.version ?? "dev";

        private static string SkillsSource => CliExtPaths.PackageRoot == null ? null : Path.Combine(CliExtPaths.PackageRoot, "Skills~");
        private static string SkillsTarget => Path.Combine(CliExtPaths.ProjectRoot, ".claude", "skills");
        public static string SkillTarget => Path.Combine(SkillsTarget, SkillName);
        public static string ClaudeMd => Path.Combine(CliExtPaths.ProjectRoot, "CLAUDE.md");

        /// <summary>The line appended to an installed SKILL.md that records which package version wrote it.</summary>
        public static string Stamp(string skill, string version) =>
            $"<!-- {skill} {version}: installed by Unity CLI Extensions. It is replaced when the package updates; " +
            "to keep your own edits, turn off Project Settings > CLI Extensions > Keep Skill and CLAUDE.md Current. -->";

        /// <summary>The package version recorded in an installed skill file's stamp, or null when it has none.</summary>
        public static string StampedVersion(string fileText)
        {
            Match stamp = s_Stamp.Match(fileText ?? "");
            return stamp.Success ? stamp.Groups["version"].Value : null;
        }

        /// <summary>The skills in <paramref name="available"/> that apply to a project with <paramref name="installedPackages"/>.</summary>
        public static List<string> ApplicableSkills(IEnumerable<string> available, ICollection<string> installedPackages)
        {
            return available
                .Where(skill => !SkillRequirements.TryGetValue(skill, out string package) || installedPackages.Contains(package))
                .OrderBy(skill => skill == SkillName ? 0 : 1).ThenBy(skill => skill, StringComparer.Ordinal)
                .ToList();
        }

        private static HashSet<string> InstalledPackages()
        {
            return new HashSet<string>(UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(p => p.name), StringComparer.Ordinal);
        }

        private static string Block(string version, ICollection<string> skills)
        {
            string text =
                $"{BlockBegin} (managed by Unity CLI Extensions {version}; edits inside this block are replaced) -->\n" +
                "## Unity Editor\n" +
                "This project's Unity Editor is driven from the command line with Unity CLI Extensions (`unity command cliext.*`, on top of the Pipeline package). " +
                $"Before any work that touches the running Editor (scripts and compiles, tests, Play mode, scenes, assets, graphs, settings), load the `{SkillName}` skill " +
                $"(`.claude/skills/{SkillName}/SKILL.md`) and start with `unity command cliext status`.";
            if (skills.Contains(VfxSkillName))
                text += $" Before creating or editing a VFX Graph, also load the `{VfxSkillName}` skill (`.claude/skills/{VfxSkillName}/SKILL.md`).";
            return text + "\n" + BlockEnd;
        }

        /// <summary>
        /// Copies the skills that apply to this project and writes the CLAUDE.md block where they differ from this
        /// package version. A skill file edited by hand since this same version installed it is kept unless
        /// <paramref name="force"/>. A package skill that no longer applies (its package was removed) is taken out.
        /// </summary>
        public static Result Install(bool force)
        {
            string version = PackageVersion;
            var result = new Result { version = version, skill = SkillTarget, claudeMd = ClaudeMd };
            string source = SkillsSource;
            if (source == null || !Directory.Exists(Path.Combine(source, SkillName))) throw new InvalidOperationException($"The package's skills are missing ({source}).");

            var available = Directory.GetDirectories(source).Select(Path.GetFileName).ToList();
            List<string> skills = ApplicableSkills(available, InstalledPackages());
            foreach (string skill in skills)
                InstallSkill(Path.Combine(source, skill), Path.Combine(SkillsTarget, skill), skill, version, force, result);

            foreach (string skill in available.Except(skills))
            {
                string stale = Path.Combine(SkillsTarget, skill);
                string skillMd = Path.Combine(stale, "SKILL.md");
                if (File.Exists(skillMd) && StampedVersion(File.ReadAllText(skillMd)) != null)
                {
                    Directory.Delete(stale, true);
                    result.changes.Add($"skill {skill} removed (its package is not installed)");
                }
            }

            string block = Block(version, skills);
            string md = File.Exists(ClaudeMd) ? File.ReadAllText(ClaudeMd) : null;
            string newline = md != null && md.Contains("\r\n") ? "\r\n" : "\n";
            string blockText = block.Replace("\n", newline);
            string updated;
            if (md == null) updated = $"# {new DirectoryInfo(CliExtPaths.ProjectRoot).Name}{newline}{newline}{blockText}{newline}";
            else if (s_Block.IsMatch(md)) updated = s_Block.Replace(md, blockText.Replace("$", "$$"), 1);
            else updated = md.TrimEnd('\r', '\n') + newline + newline + blockText + newline;
            if (updated != md)
            {
                File.WriteAllText(ClaudeMd, updated, new UTF8Encoding(false));
                result.changes.Add(md == null ? "CLAUDE.md created" : "CLAUDE.md block");
            }
            return result;
        }

        private static void InstallSkill(string source, string targetRoot, string skill, string version, bool force, Result result)
        {
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length + 1);
                string target = Path.Combine(targetRoot, relative);
                string want = File.ReadAllText(file).Replace("\r\n", "\n");
                if (relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) want = want.TrimEnd('\n') + "\n\n" + Stamp(skill, version) + "\n";
                string label = $"{skill}/{relative.Replace('\\', '/')}";
                if (File.Exists(target))
                {
                    string have = File.ReadAllText(target).Replace("\r\n", "\n");
                    if (have == want) continue;
                    string skillMd = Path.Combine(targetRoot, "SKILL.md");
                    bool sameVersion = File.Exists(skillMd) && StampedVersion(File.ReadAllText(skillMd)) == version;
                    if (sameVersion && !force) { result.kept.Add($"{label} (edited since {version} installed it; --force replaces it)"); continue; }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, want, new UTF8Encoding(false));
                result.changes.Add($"skill {label}");
            }
        }

        /// <summary>Removes the installed package skills and the CLAUDE.md block (the rest of CLAUDE.md stays).</summary>
        public static List<string> Remove()
        {
            var removed = new List<string>();
            foreach (string skill in new[] { SkillName }.Concat(SkillRequirements.Keys))
            {
                string target = Path.Combine(SkillsTarget, skill);
                if (Directory.Exists(target)) { Directory.Delete(target, true); removed.Add($"skill {skill}"); }
            }
            if (File.Exists(ClaudeMd))
            {
                string md = File.ReadAllText(ClaudeMd);
                if (s_Block.IsMatch(md))
                {
                    string stripped = Regex.Replace(s_Block.Replace(md, ""), @"(\r?\n){3,}", m => m.Value.Contains("\r") ? "\r\n\r\n" : "\n\n").TrimEnd() + (md.Contains("\r\n") ? "\r\n" : "\n");
                    File.WriteAllText(ClaudeMd, stripped, new UTF8Encoding(false));
                    removed.Add("CLAUDE.md block");
                }
            }
            return removed;
        }

        [MenuItem("Tools/CLI Extensions/Update Agent Files (Skill and CLAUDE.md)", priority = 21)]
        private static void MenuInstall()
        {
            var r = Install(force: true);
            Debug.Log($"[CLI Extensions] Agent files for {r.version}: {(r.changes.Count == 0 ? "already current" : string.Join("; ", r.changes))}.");
        }

        [CliGroupCommand("cliext.setup", "Install or update the agent files: the unity-cliext skill in .claude/skills/unity-cliext/ (plus unity-cliext-vfx when the project has VFX Graph) and a marked block in CLAUDE.md (created if missing; the rest of it is left alone). " +
                                         "Only what differs from this package version is written. This also runs by itself when the package loads (Project Settings > CLI Extensions). " +
                                         "--force replaces a skill edited by hand; --remove takes both out.",
            Name = "agent", Tags = new[] { "cliext", "setup" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode AgentCommand(
            CommandContext cmd,
            [CliArg("force", "Replace a skill file edited by hand")] bool force = false,
            [CliArg("remove", "Remove the skill and the CLAUDE.md block instead")] bool remove = false)
        {
            try
            {
                if (remove) { cmd.Result.Write(new { removed = Remove() }); return ExitCode.Success; }
                var r = Install(force);
                cmd.Result.Write(new { r.version, r.skill, r.claudeMd, changes = r.changes, kept = r.kept, current = r.changes.Count == 0 && r.kept.Count == 0 });
                return ExitCode.Success;
            }
            catch (Exception e) when (e is InvalidOperationException || e is IOException || e is UnauthorizedAccessException) { cmd.Log.Error(e.Message); return ExitCode.Failed; }
        }
    }
}

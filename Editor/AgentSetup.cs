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
    /// Keeps the agent-facing files of a project current with the installed package: the <c>unity-cliext</c> skill
    /// (copied from the package's <c>Skills~/</c> into <c>.claude/skills/unity-cliext/</c>) and a marked block in the
    /// project's <c>CLAUDE.md</c> that points agents at it. Runs when the package loads (so on install and on every
    /// update), only writes what differs, and leaves everything outside its own files and block alone.
    /// </summary>
    [InitializeOnLoad]
    public static class AgentSetup
    {
        public const string SkillName = "unity-cliext";
        private const string BlockBegin = "<!-- BEGIN unity-cliext";
        private const string BlockEnd = "<!-- END unity-cliext -->";
        private static readonly Regex s_Block = new Regex(@"<!-- BEGIN unity-cliext.*?<!-- END unity-cliext -->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex s_Stamp = new Regex(@"<!-- unity-cliext (?<version>[^\s:]+):", RegexOptions.Compiled);

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

        private static string SkillSource => CliExtPaths.PackageRoot == null ? null : Path.Combine(CliExtPaths.PackageRoot, "Skills~", SkillName);
        public static string SkillTarget => Path.Combine(CliExtPaths.ProjectRoot, ".claude", "skills", SkillName);
        public static string ClaudeMd => Path.Combine(CliExtPaths.ProjectRoot, "CLAUDE.md");

        private static string Stamp(string version) =>
            $"<!-- unity-cliext {version}: installed by Unity CLI Extensions. It is replaced when the package updates; " +
            "to keep your own edits, turn off Project Settings > CLI Extensions > Keep Skill and CLAUDE.md Current. -->";

        private static string Block(string version) =>
            $"{BlockBegin} (managed by Unity CLI Extensions {version}; edits inside this block are replaced) -->\n" +
            "## Unity Editor\n" +
            "This project's Unity Editor is driven from the command line with Unity CLI Extensions (`unity command cliext.*`, on top of the Pipeline package). " +
            $"Before any work that touches the running Editor (scripts and compiles, tests, Play mode, scenes, assets, graphs, settings), load the `{SkillName}` skill " +
            $"(`.claude/skills/{SkillName}/SKILL.md`) and start with `unity command cliext status`.\n" +
            BlockEnd;

        /// <summary>
        /// Copies the skill and writes the CLAUDE.md block where they differ from this package version. A skill file
        /// edited by hand since this same version installed it is kept unless <paramref name="force"/>.
        /// </summary>
        public static Result Install(bool force)
        {
            string version = PackageVersion;
            var result = new Result { version = version, skill = SkillTarget, claudeMd = ClaudeMd };
            string source = SkillSource;
            if (source == null || !Directory.Exists(source)) throw new InvalidOperationException($"The package's skill is missing ({source}).");

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length + 1);
                string target = Path.Combine(SkillTarget, relative);
                string want = File.ReadAllText(file).Replace("\r\n", "\n");
                if (relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) want = want.TrimEnd('\n') + "\n\n" + Stamp(version) + "\n";
                if (File.Exists(target))
                {
                    string have = File.ReadAllText(target).Replace("\r\n", "\n");
                    if (have == want) continue;
                    var stamp = s_Stamp.Match(have);
                    bool sameVersion = stamp.Success && stamp.Groups["version"].Value == version;
                    if (sameVersion && !force) { result.kept.Add($"{relative} (edited since {version} installed it; --force replaces it)"); continue; }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, want, new UTF8Encoding(false));
                result.changes.Add($"skill {relative}");
            }

            string block = Block(version);
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

        /// <summary>Removes the installed skill and the CLAUDE.md block (the rest of CLAUDE.md stays).</summary>
        public static List<string> Remove()
        {
            var removed = new List<string>();
            if (Directory.Exists(SkillTarget)) { Directory.Delete(SkillTarget, true); removed.Add("skill"); }
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

        [CliGroupCommand("cliext.setup", "Install or update the agent files: the unity-cliext skill in .claude/skills/unity-cliext/ and a marked block in CLAUDE.md (created if missing; the rest of it is left alone). " +
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

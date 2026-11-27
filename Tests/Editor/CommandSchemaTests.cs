using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// Lints every cliext command's declared schema (the [CliGroupCommand] and [CliArg] attributes):
    /// what an agent reads in --help and the --query schema before its first call.
    /// Each test lists every offender at once, so one run shows the whole backlog.
    /// </summary>
    public class CommandSchemaTests
    {
        private sealed class Arg
        {
            public string Name;
            public string Description;
            public bool Required;
            public Type Type;
            public object Default;
        }

        private sealed class Command
        {
            public string Id;
            public string Group;
            public string Name;
            public string Description;
            public CommandEffect Effect;
            public List<Arg> Args;
        }

        private static List<Command> _commands;

        private static List<Command> Commands => _commands ??= Discover();

        private static List<Command> Discover()
        {
            var found = new List<Command>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (var type in types)
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = method.GetCustomAttribute<CliGroupCommandAttribute>();
                    if (attr == null || !(attr.Group == "cliext" || attr.Group.StartsWith("cliext.", StringComparison.Ordinal)))
                        continue;

                    // Pipeline names an unnamed command after its method in snake_case (SetAgentMode -> set_agent_mode).
                    var name = string.IsNullOrEmpty(attr.Name)
                        ? Regex.Replace(method.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()
                        : attr.Name;
                    found.Add(new Command
                    {
                        Id = attr.Group + " " + name,
                        Group = attr.Group,
                        Name = name,
                        Description = attr.Description ?? "",
                        Effect = attr.Effect,
                        Args = method.GetParameters()
                            .Select(p => (p, a: p.GetCustomAttribute<CliArgAttribute>()))
                            .Where(x => x.a != null)
                            .Select(x => new Arg
                            {
                                Name = x.a.Name,
                                Description = x.a.Description ?? "",
                                Required = x.a.Required,
                                Type = Nullable.GetUnderlyingType(x.p.ParameterType) ?? x.p.ParameterType,
                                Default = x.a.DefaultValue ?? (x.p.HasDefaultValue ? x.p.DefaultValue : null),
                            })
                            .ToList(),
                    });
                }
            }
            return found.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<(Command cmd, Arg arg)> AllArgs =>
            Commands.SelectMany(c => c.Args.Select(a => (c, a)));

        private static void AssertNone(IEnumerable<string> offenders, string rule)
        {
            var list = offenders.ToList();
            Assert.IsEmpty(list, $"{rule} ({list.Count}):\n- " + string.Join("\n- ", list));
        }

        [Test]
        public void Discovery_FindsTheCliextCommands()
        {
            Assert.That(Commands.Count, Is.GreaterThanOrEqualTo(60), "cliext commands found by reflection");
            AssertNone(Commands.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key), "Duplicate command ids");
        }

        [Test]
        public void EveryCommand_HasADescriptionOfAtLeast20Characters()
        {
            AssertNone(Commands.Where(c => c.Description.Trim().Length < 20)
                .Select(c => $"{c.Id}: \"{c.Description}\""), "Command descriptions too short to act on");
        }

        [Test]
        public void EveryParameter_HasADescriptionOfAtLeast12Characters()
        {
            AssertNone(AllArgs.Where(x => x.arg.Description.Trim().Length < 12)
                .Select(x => $"{x.cmd.Id} --{x.arg.Name}: \"{x.arg.Description}\""),
                "Parameter descriptions too short to give a format");
        }

        [Test]
        public void ParameterNames_AreKebabCase()
        {
            var kebab = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");
            AssertNone(AllArgs.Where(x => !kebab.IsMatch(x.arg.Name)).Select(x => $"{x.cmd.Id} --{x.arg.Name}"),
                "Parameter names that aren't kebab-case");
        }

        [Test]
        public void Descriptions_AreAscii()
        {
            bool NonAscii(string s) => s.Any(ch => ch > 127);
            var offenders = Commands.Where(c => NonAscii(c.Description)).Select(c => c.Id)
                .Concat(AllArgs.Where(x => NonAscii(x.arg.Description)).Select(x => $"{x.cmd.Id} --{x.arg.Name}"));
            AssertNone(offenders, "Descriptions with non-ASCII characters (garbled by Windows PowerShell 5.1)");
        }

        [Test]
        public void BatchEdits_TakeOpsAndOpsFile()
        {
            AssertNone(Commands
                .Where(c => c.Args.Any(a => a.Name == "ops") != c.Args.Any(a => a.Name == "ops-file"))
                .Select(c => c.Id), "Commands with only one of --ops and --ops-file");
        }

        [Test]
        public void BatchEdits_AreAllOrNothingAndSaySo()
        {
            // An agent retrying a failed batch must know nothing from it landed.
            AssertNone(Commands
                .Where(c => c.Args.Any(a => a.Name == "ops"))
                .Where(c => !Regex.IsMatch(c.Description, @"An op that fails saves nothing", RegexOptions.IgnoreCase))
                .Select(c => c.Id), "Batch edits whose description doesn't say \"An op that fails saves nothing\"");
        }

        private static readonly HashSet<string> ReadNames = new HashSet<string>
        {
            "inspect", "list", "status", "types", "query", "tree", "check", "validate", "diagnostics", "result",
            "report", "scan", "uses", "used_by", "entries", "surfaces", "devices", "actions", "takes", "clip",
            "live", "audit", "affected", "replay_status", "analyze", "log", "matrix", "settings", "levels", "sample",
        };

        private static readonly HashSet<string> ListingNames = new HashSet<string>
        {
            "clips", "entries", "takes", "devices", "actions", "surfaces", "levels", "settings", "types", "list",
        };

        private static readonly HashSet<string> WriteArgNames = new HashSet<string>
        {
            "update", "set", "force", "remove", "ops", "ops-file", "clean",
        };

        [Test]
        public void Effects_MatchWhatTheCommandDoes()
        {
            var offenders = new List<string>();
            foreach (var c in Commands)
            {
                if (ReadNames.Contains(c.Name) && c.Effect != CommandEffect.ReadOnly)
                    offenders.Add($"{c.Id}: reads like a query but is {c.Effect}");
                if (c.Effect == CommandEffect.ReadOnly)
                    foreach (var a in c.Args.Where(a => WriteArgNames.Contains(a.Name)))
                        offenders.Add($"{c.Id}: ReadOnly but --{a.Name} writes");
                if ((c.Name == "delete" || c.Name.StartsWith("remove", StringComparison.Ordinal)) && c.Effect != CommandEffect.Destructive)
                    offenders.Add($"{c.Id}: deletes but is {c.Effect}, not Destructive");
                if (ListingNames.Contains(c.Name) && c.Effect != CommandEffect.ReadOnly)
                    offenders.Add($"{c.Id}: named like a listing but changes things ({c.Effect})");
                if (c.Effect == CommandEffect.Unspecified)
                    offenders.Add($"{c.Id}: Effect not set");
            }
            AssertNone(offenders, "Commands whose Effect misstates what they do");
        }

        [Test]
        public void ListParameters_UseSemicolons()
        {
            AssertNone(AllArgs.Where(x => Regex.IsMatch(x.arg.Description, @"comma-separated|separated by ','", RegexOptions.IgnoreCase))
                .Select(x => $"{x.cmd.Id} --{x.arg.Name}: \"{x.arg.Description}\""),
                "List parameters split on commas (every other list uses ';')");
        }

        [Test]
        public void SentinelDefaults_AreExplained()
        {
            bool IsSentinel(object d) => d switch
            {
                float f => float.IsNaN(f) || f < 0,
                double v => double.IsNaN(v) || v < 0,
                int i => i < 0,
                _ => false,
            };
            AssertNone(AllArgs.Where(x => IsSentinel(x.arg.Default))
                    .Where(x => !Regex.IsMatch(x.arg.Description, @"default|(?<![\d])-1\b|NaN|auto|not set|unset|leave|empty", RegexOptions.IgnoreCase))
                    .Select(x => $"{x.cmd.Id} --{x.arg.Name}={x.arg.Default}: \"{x.arg.Description}\""),
                "Sentinel defaults (NaN or negative) whose meaning isn't stated");
        }

        private static readonly HashSet<string> TypeVaries = new HashSet<string>
        {
            "value", // a value whose type depends on the target slot, parameter or field
        };

        private static string Kind(Type t) =>
            t == typeof(float) || t == typeof(double) ? "number" : t == typeof(int) || t == typeof(long) ? "integer" : t.Name;

        [Test]
        public void SameParameterName_HasTheSameTypeEverywhere()
        {
            AssertNone(AllArgs.Where(x => !TypeVaries.Contains(x.arg.Name))
                    .GroupBy(x => x.arg.Name)
                    .Where(g => g.Select(x => Kind(x.arg.Type)).Distinct().Count() > 1)
                    .Select(g => $"--{g.Key}: " + string.Join(", ", g.Select(x => $"{x.cmd.Id}={x.arg.Type.Name}"))),
                "Parameter names typed differently across commands");
        }

        [Test]
        public void SetCommands_AgreeOnWhetherValueIsRequired()
        {
            // Optional is fine when the description says when it can be left out (vfx set --space alone).
            var values = AllArgs.Where(x => x.cmd.Name == "set" && x.arg.Name == "value").ToList();
            var disagree = values.Select(x => x.arg.Required).Distinct().Count() > 1;
            AssertNone(disagree
                    ? values.Where(x => !x.arg.Required && !Regex.IsMatch(x.arg.Description, @"leave (it )?out|optional when|omit", RegexOptions.IgnoreCase))
                        .Select(x => $"{x.cmd.Id}: optional, and doesn't say when it can be left out")
                    : Enumerable.Empty<string>(),
                "set commands disagree on whether --value is required");
        }

        private static readonly HashSet<string> EnumLikeNames = new HashSet<string>
        {
            "type", "kind", "mode", "rig", "style", "action", "filter-type", "scope", "target", "material",
        };

        [Test]
        public void EnumLikeParameters_ListTheirValues()
        {
            AssertNone(AllArgs.Where(x => EnumLikeNames.Contains(x.arg.Name) && x.arg.Type == typeof(string))
                    .Where(x => !x.arg.Description.Contains(",") && !Regex.IsMatch(x.arg.Description, @"\bsee\b|\w+ or \w+", RegexOptions.IgnoreCase))
                    .Select(x => $"{x.cmd.Id} --{x.arg.Name}: \"{x.arg.Description}\""),
                "Enum-like parameters that don't list their values or point to where they're listed");
        }

        [Test]
        public void AssetPaths_SayGuidWhereverTheGroupAcceptsIt()
        {
            var groupsWithGuid = AllArgs.Where(x => x.arg.Name == "path" && x.arg.Description.Contains("GUID"))
                .Select(x => x.cmd.Group).ToHashSet();
            AssertNone(AllArgs.Where(x => x.arg.Name == "path" && groupsWithGuid.Contains(x.cmd.Group))
                    .Where(x => !x.arg.Description.Contains("GUID") && !x.arg.Description.StartsWith("Where", StringComparison.Ordinal))
                    .Select(x => $"{x.cmd.Id} --path: \"{x.arg.Description}\""),
                "Asset --path parameters that don't say GUID while others in their group do");
        }
    }
}

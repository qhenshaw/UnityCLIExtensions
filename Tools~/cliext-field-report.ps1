<#
.SYNOPSIS
Field report for Unity CLI Extensions: what agents met in real use of the package, read from the project's Claude Code
transcripts, its failure log and its field notes.

.DESCRIPTION
Reads, for the last -Days days:
- the Claude Code transcripts of -Project: $env:USERPROFILE\.claude\projects\<slug>\*.jsonl and each session's
  subagents\*.jsonl (the slug is the project path with every character other than a letter or digit replaced by '-',
  so E:\Projects\MyGame is E--Projects-MyGame);
- Library/CLIExtensions/failures.jsonl (failed cliext commands) and field-notes.jsonl (cliext.failures note).

and reports, grouped by cliext command or area:
- failed tool calls by cause (cliext, pipeline, shell, hook, permission, other), classified as T3's score.py does;
- reads and searches of the package's own source or README;
- Edit or Write of files cliext commands edit (.shadergraph, .vfx, .controller, .anim, .overrideController, .mask,
  .playable, .inputactions, .uxml, .uss, .preset), and shell commands that write them;
- 3 or more --help calls on the same command in one transcript;
- script eval and cliext.eval calls, with a short excerpt;
- for each area, whether its skill was loaded before the transcript's first call in that area;
- the field notes and failures in the window. Each note and each failure is matched to the tool call that made it
  (by time and by its text or command), and the report shows the calls before and after it, with their arguments
  and a short excerpt of each output, the session id and the transcript path. One that matches no call says so.

Writes field-report.md and field-report.json to -Out (default <project>/Library/CLIExtensions/field-report/) and
prints their paths. The transcripts, failures.jsonl and field-notes.jsonl are only read. The report stays on this
machine; it includes tool arguments and outputs, which can hold project code, paths and prompts.

Exit codes: 0 when the report was written, 2 for a bad argument (no such project).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-field-report.ps1 -Project E:/Games/MyGame
powershell -NoProfile -ExecutionPolicy Bypass -File cliext-field-report.ps1 -Project E:/Games/MyGame -Days 3 -Out C:/Temp/report
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [int]$Days = 14,
    [string]$Out = '',
    # The Claude Code projects folder; default $env:USERPROFILE\.claude\projects.
    [string]$TranscriptRoot = '',
    # Tool calls shown before and after each matched note or failure.
    [int]$Before = 15,
    [int]$After = 5,
    # Characters kept of each output excerpt.
    [int]$Excerpt = 300,
    # Matched failures shown with their context in the Markdown (all are in the JSON).
    [int]$MaxFailureContexts = 40,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Project -PathType Container)) { Write-Error "No project folder at '$Project'."; exit 2 }
if ($Days -lt 1) { Write-Error "-Days $Days`: give 1 or more."; exit 2 }
$projectPath = (Resolve-Path -LiteralPath $Project).Path.TrimEnd('\', '/')
if (-not $TranscriptRoot) { $TranscriptRoot = Join-Path $env:USERPROFILE '.claude\projects' }
if (-not $Out) { $Out = Join-Path $projectPath 'Library\CLIExtensions\field-report' }
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Split-Path -Parent $tools

# The package's folders as the transcripts may name them: this copy, the manifest's file: path, the PackageCache copy.
$roots = New-Object Collections.Generic.List[string]
$roots.Add($packageRoot)
$manifest = Join-Path $projectPath 'Packages\manifest.json'
if (Test-Path -LiteralPath $manifest) {
    $m = [regex]::Match([IO.File]::ReadAllText($manifest), '"com\.qhenshaw\.unitycliextensions"\s*:\s*"file:([^"]+)"')
    if ($m.Success) {
        $p = $m.Groups[1].Value
        if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path (Join-Path $projectPath 'Packages') $p }
        $roots.Add($p)
    }
}
$roots.Add((Join-Path $projectPath 'Library\PackageCache\com.qhenshaw.unitycliextensions'))
$roots.Add((Join-Path $projectPath 'Packages\com.qhenshaw.unitycliextensions'))

$source = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

public static class CliextFieldReport
{
    // ---------------------------------------------------------------------------------------------------------------
    // Model

    public class Call
    {
        public int Index;
        public string Id = "";
        public string Name = "";
        public string Summary = "";      // as score.py: command, skill, file_path or pattern
        public string Input = "";        // the exact command, or the input as JSON (trimmed)
        public string FilePath = "";
        public string WriteContent = ""; // for Write: the start of the content
        public DateTime? Ts;
        public DateTime? ResultTs;
        public bool HasResult;
        public bool IsError;
        public string Result = "";
        public bool Failed;
        public bool EmbeddedOnly;
        public string Cause = "";
        public List<string> EmbeddedLines = new List<string>();
    }

    public class Transcript
    {
        public string Path;
        public string SessionId = "";
        public string AgentId = "";
        public string Description = "";
        public bool IsSubagent;
        public List<Call> Calls = new List<Call>();
        public List<KeyValuePair<int, string>> Skills = new List<KeyValuePair<int, string>>();
        public DateTime? Start, End;
        public int Work;                 // tool calls without the final SubagentHandback
        public int FailedCount, EmbeddedCount;
        public Dictionary<string, int> FailedByCause = new Dictionary<string, int>();
        public string Label { get { return IsSubagent ? "agent " + AgentId : "session " + Short(SessionId, 8, false); } }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // score.py, ported (T3/score.py: failed calls and their causes). Keep in step with it.

    static readonly Regex NotFound = new Regex(@"The command [\w. -]+? was not found in group|Unknown (command|group)\b");
    static readonly Regex EmbeddedRx = new Regex(@"^.*(No command named '[^']+'|has no parameter --[\w-]+).*$|^error: .*$|Op \d+ \(\w+\) failed.*$|COMPILATION_ERROR.*$", RegexOptions.Multiline);
    static readonly Regex PipeCut = new Regex(@"\|\s*(Select(-Object)?\b[^|;\n]*-First\b|head\b)", RegexOptions.IgnoreCase);
    static readonly Regex ShellUnityRx = new Regex(@"\bunity(\.exe)?\s+(command|cmd)\b");
    static readonly Regex ExitCodeRx = new Regex(@"Exit code (-?\d+)");
    static readonly Regex SkillMd = new Regex(@"[\\/]([\w.-]+)[\\/]SKILL\.md$");

    static bool ShellUnity(Call c) { return (c.Name == "PowerShell" || c.Name == "Bash") && ShellUnityRx.IsMatch(c.Summary); }

    static List<string> Embedded(Call c)
    {
        var list = new List<string>();
        if (!ShellUnity(c)) return list;
        foreach (Match m in EmbeddedRx.Matches(c.Result)) list.Add(m.Value);
        return list;
    }

    static string EmbeddedCause(string call, string line)
    {
        Match m = Regex.Match(line, @"No command named '([\w.]+)|([\w.]+(?: \w+)?) has no parameter");
        if (m.Success)
        {
            string g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return g.StartsWith("cliext", StringComparison.Ordinal) ? "cliext" : "pipeline";
        }
        if (line.Contains("cliext")) return "cliext";
        var groups = Regex.Matches(call, @"\bunity(?:\.exe)?\s+(?:command|cmd)\s+([\w.]+)").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
        if (groups.Count > 0 && groups.All(g => g.StartsWith("cliext", StringComparison.Ordinal))) return "cliext";
        if (groups.Count > 0 && !groups.Any(g => g.StartsWith("cliext", StringComparison.Ordinal))) return "pipeline";
        return Regex.IsMatch(line, @"Op \d+ \(|for the new mask|Motion reference|cliext") ? "cliext" : "pipeline";
    }

    static bool ExitFailed(Call c)
    {
        string text = c.Result;
        if (NotFound.IsMatch(text)) return true;
        Match m = ExitCodeRx.Match(text.Length > 300 ? text.Substring(0, 300) : text);
        if (m.Success && m.Groups[1].Value == "255" && PipeCut.IsMatch(c.Summary)) return false;
        if (m.Success) return m.Groups[1].Value != "0";
        return c.IsError;
    }

    static string CauseOf(Call c)
    {
        string text = c.Result;
        if (Regex.IsMatch(text, @"denied by the Claude Code auto mode classifier|Permission for this action was denied")) return "permission";
        if (Regex.IsMatch(text, @"hook error|Blocked:")) return "hook";
        if (!ExitFailed(c)) return EmbeddedCause(c.Summary, Embedded(c)[0]);
        if (Regex.IsMatch(c.Summary, @"\bunity(\.exe)?\s+(command|cmd)\b[^;&|\n]*\bcliext")) return "cliext";
        if (ShellUnityRx.IsMatch(c.Summary)) return "pipeline";
        if (c.Name == "PowerShell" || c.Name == "Bash") return "shell";
        return "other";
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Commands and areas

    static readonly Regex UnityCmd = new Regex(@"\bunity(?:\.exe)?\s+(?:command|cmd)\s+(?:--project-path\s+(?:""[^""]*""|'[^']*'|\S+)\s+)?([A-Za-z][\w.]*)(?:[ \t]+([a-z][a-z_]*)\b)?");
    static readonly Regex HelpRx = new Regex(@"\bunity(?:\.exe)?\s+(?:command|cmd)\s+(?:--project-path\s+(?:""[^""]*""|'[^']*'|\S+)\s+)?([A-Za-z][\w.]*)(?:[ \t]+([a-z][a-z_]*)\b)?[^;&|\r\n]*?\s--help\b");
    static readonly Regex EvalRx = new Regex(@"\bunity(?:\.exe)?\s+(?:command|cmd)\s+(?:--project-path\s+(?:""[^""]*""|'[^']*'|\S+)\s+)?(script\s+eval|cliext\.eval\s+run|cliext\.playmode\s+wait_until)\b[^\r\n;|&]*");
    static readonly Regex FileArg = new Regex(@"--(?:file|code-file)\s+(?:""([^""]+)""|'([^']+)'|(\S+))");
    static readonly Regex CodeArg = new Regex(@"--code\s+(?:""((?:[^""\\]|\\.)*)""|'((?:[^']|'')*)'|(\S+))");
    static readonly string[] OpExts = { ".shadergraph", ".vfx", ".controller", ".anim", ".overrideController", ".mask", ".playable", ".inputactions", ".uxml", ".uss", ".preset" };
    // A path into the package that names it without one of the known roots (another checkout, a Git Bash path).
    static readonly Regex PackageNamed = new Regex(@"unitycliextensions[^/\s""']*/(editor|runtime|tests|tools~|skills~|readme|changelog|documentation~)", RegexOptions.IgnoreCase);
    // The file a shell write goes to: WriteAllText(x, ...), Set-Content x, Out-File x, > x, sed -i ... x.
    static readonly Regex WriteTarget = new Regex(@"(?:WriteAll(?:Text|Lines|Bytes)\(\s*|(?:^|\s)>{1,2}\s*)(?:""([^""]+)""|'([^']+)'|([^\s,);|]+))", RegexOptions.IgnoreCase);
    static readonly Regex WriteCmdlet = new Regex(@"\b(Set-Content|Add-Content|Out-File)\b([^|;\r\n]*)", RegexOptions.IgnoreCase);
    static readonly Regex SedInPlace = new Regex(@"\b(?:sed\s+-i|perl\s+-pi)[^|;&\r\n]*", RegexOptions.IgnoreCase);
    static readonly Regex HereBody = new Regex(@"@'.*?'@|@"".*?""@|<<-?\s*['""]?(\w+)['""]?.*?\n\1\b", RegexOptions.Singleline);
    static readonly Regex VarAssign = new Regex(@"(\$[\w:]+)\s*=\s*(?:""([^""]+)""|'([^']+)')");

    static List<string> ShellWriteTargets(string text)
    {
        // A here-string or heredoc body is data being written, not where it goes.
        text = HereBody.Replace(text, "@''@");
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match v in VarAssign.Matches(text)) vars[v.Groups[1].Value] = v.Groups[2].Success ? v.Groups[2].Value : v.Groups[3].Value;
        var targets = new List<string>();
        foreach (Match m in WriteTarget.Matches(text))
        {
            string p = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            string value;
            if (p.StartsWith("$") && vars.TryGetValue(p, out value)) p = value;
            targets.Add(p);
        }
        foreach (Match m in WriteCmdlet.Matches(text))
        {
            bool skipNext = false;
            foreach (Match tok in Regex.Matches(m.Groups[2].Value, @"""[^""]*""|'[^']*'|\S+"))
            {
                string s = tok.Value;
                if (skipNext) { skipNext = false; continue; }
                if (s.StartsWith("-"))
                {
                    skipNext = Regex.IsMatch(s, @"^-(Encoding|Value|Width|Stream|Delimiter|InputObject)$", RegexOptions.IgnoreCase);
                    continue;
                }
                string p = s.Trim('"', '\''), value;
                if (p.StartsWith("$") && vars.TryGetValue(p, out value)) p = value;
                targets.Add(p);
            }
        }        foreach (Match m in SedInPlace.Matches(text))
            foreach (string token in m.Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) targets.Add(token.Trim('"', '\''));
        return targets;
    }

    static readonly Regex UnityStripped =new Regex(@"\bunity(\.exe)?\s+(command|cmd)\b[^;\r\n]*");
    static readonly Regex ShellRead = new Regex(@"\b(Get-Content|gc|cat|type|Select-String|sls|grep|rg|findstr|head|tail|less|more|sed|awk|Get-ChildItem|gci|ls|dir|diff|fc|Compare-Object)\b", RegexOptions.IgnoreCase);

    static string AreaOfGroup(string group)
    {
        if (group == null) return null;
        if (group.StartsWith("cliext.vfx")) return "cliext.vfx";
        if (group.StartsWith("cliext.shadergraph")) return "cliext.shadergraph";
        if (group == "cliext.animator" || group == "cliext.anim" || group == "cliext.timeline" || group.StartsWith("animation.")) return "animation";
        if (group == "cliext.input") return "cliext.input";
        if (group == "cliext.uitk") return "cliext.uitk";
        if (group == "cliext.addressables") return "cliext.addressables";
        if (group == "cliext.import" || group == "cliext.presets" || group == "cliext.package") return "import";
        return group;
    }

    static string AreaOfExt(string ext)
    {
        switch (ext.ToLowerInvariant())
        {
            case ".shadergraph": return "cliext.shadergraph";
            case ".vfx": return "cliext.vfx";
            case ".controller": case ".anim": case ".overridecontroller": case ".mask": case ".playable": return "animation";
            case ".inputactions": return "cliext.input";
            case ".uxml": case ".uss": return "cliext.uitk";
            case ".preset": return "import";
        }
        return null;
    }

    static readonly Dictionary<string, string> AreaSkill = new Dictionary<string, string>
    {
        { "cliext.vfx", "unity-cliext-vfx" },
        { "cliext.shadergraph", "unity-cliext-shadergraph" },
        { "animation", "unity-cliext-animation" },
        { "cliext.input", "unity-cliext-input" },
        { "cliext.uitk", "unity-cliext-uitk" },
        { "cliext.addressables", "unity-cliext-addressables" },
        { "import", "unity-cliext-import" },
    };

    class UnityCall { public string Group; public string Command; public int Pos; }

    static List<UnityCall> UnityCommands(string text)
    {
        var list = new List<UnityCall>();
        foreach (Match m in UnityCmd.Matches(text ?? ""))
        {
            string g = m.Groups[1].Value;
            string sub = m.Groups[2].Success ? m.Groups[2].Value : "";
            list.Add(new UnityCall { Group = g, Command = sub.Length > 0 ? g + " " + sub : g, Pos = m.Index });
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Reading

    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

    static object Get(object o, string key)
    {
        var d = o as IDictionary<string, object>;
        object v;
        return d != null && d.TryGetValue(key, out v) ? v : null;
    }

    static string Str(object o, string key)
    {
        object v = Get(o, key);
        return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // Python truthiness for the score.py summary: "or" skips null, "" and other empty values.
    static bool Truthy(object v)
    {
        if (v == null) return false;
        if (v is string) return ((string)v).Length > 0;
        if (v is bool) return (bool)v;
        if (v is ICollection) return ((ICollection)v).Count > 0;
        if (v is int) return (int)v != 0;
        if (v is decimal) return (decimal)v != 0;
        return true;
    }

    public static DateTime? ParseTime(string s)
    {
        DateTime t;
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t))
            return t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
        return null;
    }

    static string Trim(string s, int n)
    {
        if (s == null) return "";
        return s.Length > n ? s.Substring(0, n) + "..." : s;
    }

    public static string Short(string s, int n, bool collapse)
    {
        if (s == null) return "";
        if (collapse) s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > n ? s.Substring(0, n) + (collapse ? "..." : "") : s;
    }

    // Path.GetFileName and GetExtension throw on characters such as " or | in .NET Framework; these don't.
    static string SafeName(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        string s = p.Trim().Trim('"', '\'');
        int i = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        return i >= 0 ? s.Substring(i + 1) : s;
    }

    static string SafeExt(string p)
    {
        string n = SafeName(p);
        int i = n.LastIndexOf('.');
        return i > 0 ? n.Substring(i) : "";
    }

    static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim(); }

    public static Transcript Parse(string path, bool subagent)
    {
        var t = new Transcript { Path = path, IsSubagent = subagent };
        var byId = new Dictionary<string, Call>();
        var results = new Dictionary<string, KeyValuePair<bool, string>>();
        var resultTimes = new Dictionary<string, DateTime?>();
        var readSkills = new List<KeyValuePair<int, string>>();
        using (var reader = new StreamReader(path, new UTF8Encoding(false), true))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.IndexOf("\"tool_use\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"tool_result\"", StringComparison.Ordinal) < 0) continue;
                object rec;
                try { rec = Js.DeserializeObject(line.Trim()); } catch { continue; }
                if (!(rec is IDictionary<string, object>)) continue;
                DateTime? ts = ParseTime(Str(rec, "timestamp"));
                if (ts.HasValue)
                {
                    if (!t.Start.HasValue || ts < t.Start) t.Start = ts;
                    if (!t.End.HasValue || ts > t.End) t.End = ts;
                }
                if (t.SessionId.Length == 0) t.SessionId = Str(rec, "sessionId") ?? "";
                if (t.AgentId.Length == 0) t.AgentId = Str(rec, "agentId") ?? "";
                object msg = Get(rec, "message");
                var content = Get(msg, "content") as IList;
                if (content == null) continue;
                foreach (object item in content)
                {
                    if (!(item is IDictionary<string, object>)) continue;
                    string type = Str(item, "type");
                    if (type == "tool_use")
                    {
                        string id = Str(item, "id") ?? "";
                        string name = Str(item, "name") ?? "";
                        object input = Get(item, "input");
                        if (!Truthy(input)) input = new Dictionary<string, object>();
                        object summary = null;
                        foreach (string k in new[] { "command", "skill", "file_path", "pattern" })
                        {
                            object v = Get(input, k);
                            if (Truthy(v)) { summary = v; break; }
                        }
                        Call call;
                        if (!byId.TryGetValue(id, out call))
                        {
                            call = new Call { Id = id, Index = t.Calls.Count, Ts = ts };
                            if (name == "Skill") t.Skills.Add(new KeyValuePair<int, string>(call.Index, Str(input, "skill") ?? ""));
                            Match sm = SkillMd.Match(Str(input, "file_path") ?? "");
                            if (name == "Read" && sm.Success) t.Skills.Add(new KeyValuePair<int, string>(call.Index, sm.Groups[1].Value + " (Read)"));
                            byId[id] = call;
                            t.Calls.Add(call);
                        }
                        call.Name = name;
                        call.Summary = summary == null ? "" : (summary is string ? (string)summary : Js.Serialize(summary));
                        string cmd = Str(input, "command");
                        call.Input = Trim(!string.IsNullOrEmpty(cmd) && (name == "PowerShell" || name == "Bash") ? cmd : Js.Serialize(input), 4000);
                        call.FilePath = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path") ?? "";
                        if (name == "Write") call.WriteContent = Trim(Str(input, "content"), 1500);
                    }
                    else if (type == "tool_result")
                    {
                        string tid = Str(item, "tool_use_id") ?? "";
                        object c = Get(item, "content");
                        string text;
                        if (c is string) text = (string)c;
                        else if (c is IList)
                        {
                            var parts = new List<string>();
                            foreach (object part in (IList)c) if (part is IDictionary<string, object>) parts.Add(Str(part, "text") ?? "");
                            text = string.Join(" ", parts);
                        }
                        else text = c == null ? "" : Convert.ToString(c, CultureInfo.InvariantCulture);
                        results[tid] = new KeyValuePair<bool, string>(Truthy(Get(item, "is_error")), text ?? "");
                        resultTimes[tid] = ts;
                    }
                }
            }
        }

        foreach (Call c in t.Calls)
        {
            KeyValuePair<bool, string> r;
            if (results.TryGetValue(c.Id, out r)) { c.HasResult = true; c.IsError = r.Key; c.Result = r.Value; c.ResultTs = resultTimes[c.Id]; }
            c.EmbeddedLines = Embedded(c);
            c.Failed = ExitFailed(c) || c.EmbeddedLines.Count > 0;
        }
        foreach (Call c in t.Calls)
        {
            if (c.Name == "SubagentHandback") continue;
            t.Work++;
            if (!c.Failed) continue;
            c.EmbeddedOnly = !ExitFailed(c);
            c.Cause = CauseOf(c);
            t.FailedCount++;
            if (c.EmbeddedOnly) t.EmbeddedCount++;
            int n;
            t.FailedByCause[c.Cause] = t.FailedByCause.TryGetValue(c.Cause, out n) ? n + 1 : 1;
        }
        // Keep only what the report shows and matches on; the full outputs were needed for the classification only.
        foreach (Call c in t.Calls) c.Result = Trim(c.Result, 6000);

        string meta = System.IO.Path.ChangeExtension(path, null) + ".meta.json";
        if (subagent && File.Exists(meta))
        {
            try { t.Description = Str(Js.DeserializeObject(File.ReadAllText(meta)), "description") ?? ""; } catch { }
        }
        if (t.AgentId.Length == 0 && subagent)
        {
            string f = System.IO.Path.GetFileNameWithoutExtension(path);
            t.AgentId = f.StartsWith("agent-") ? f.Substring(6) : f;
        }
        if (t.SessionId.Length == 0) t.SessionId = subagent ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(path))) : System.IO.Path.GetFileNameWithoutExtension(path);
        return t;
    }

    static List<Dictionary<string, object>> ReadJsonLines(string file)
    {
        var list = new List<Dictionary<string, object>>();
        if (!File.Exists(file)) return list;
        string text;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                var d = Js.DeserializeObject(line) as Dictionary<string, object>;
                if (d != null) list.Add(d);
            }
            catch { }
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Matching a note or a failure to the tool call that made it

    class Match2 { public Transcript T; public Call C; public int Score; public double Seconds; }

    static bool InWindow(Call c, DateTime when)
    {
        if (!c.Ts.HasValue) return false;
        DateTime start = c.Ts.Value.AddSeconds(-5);
        DateTime end = (c.ResultTs ?? c.Ts.Value.AddMinutes(10)).AddSeconds(5);
        return when >= start && when <= end;
    }

    static Dictionary<string, object> Context(Match2 m, int before, int after, int excerpt)
    {
        var d = new Dictionary<string, object>();
        d["sessionId"] = m.T.SessionId;
        d["agentId"] = m.T.AgentId;
        d["description"] = m.T.Description;
        d["transcript"] = m.T.Path;
        d["callIndex"] = m.C.Index;
        var rows = new List<object>();
        int from = Math.Max(0, m.C.Index - before), to = Math.Min(m.T.Calls.Count - 1, m.C.Index + after);
        for (int i = from; i <= to; i++)
        {
            Call c = m.T.Calls[i];
            var r = new Dictionary<string, object>();
            r["index"] = c.Index;
            r["offset"] = c.Index - m.C.Index;
            r["time"] = c.Ts.HasValue ? c.Ts.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : "";
            r["tool"] = c.Name;
            r["input"] = c.Index == m.C.Index ? c.Input : Trim(c.Input, 1200);
            r["failed"] = c.Failed;
            r["output"] = Short(c.Result, excerpt, true);
            rows.Add(r);
        }
        d["calls"] = rows;
        return d;
    }

    static Match2 Best(IEnumerable<Match2> candidates)
    {
        return candidates.OrderByDescending(x => x.Score).ThenBy(x => x.Seconds).FirstOrDefault();
    }

    static string FileArgOf(string command)
    {
        Match m = FileArg.Match(command ?? "");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    // The content of the file a --file argument names, from the last Write of it before the call.
    static string WrittenBefore(Transcript t, Call call, string file)
    {
        if (string.IsNullOrEmpty(file)) return "";
        string name = SafeName(file).ToLowerInvariant();
        for (int i = call.Index - 1; i >= 0; i--)
        {
            Call w = t.Calls[i];
            if (w.Name == "Write" && SafeName(w.FilePath).ToLowerInvariant() == name) return w.WriteContent;
        }
        return "";
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The report

    public static string Run(string project, string transcriptRoot, string[] packageRoots, int days, string outDir,
                             int before, int after, int excerpt, int maxFailureContexts)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DateTime now = DateTime.UtcNow;
        DateTime since = now.AddDays(-days);

        string slug = Regex.Replace(project, "[^A-Za-z0-9]", "-");
        string dir = System.IO.Path.Combine(transcriptRoot, slug);
        if (!Directory.Exists(dir) && Directory.Exists(transcriptRoot))
            dir = Directory.GetDirectories(transcriptRoot).FirstOrDefault(x => string.Equals(System.IO.Path.GetFileName(x), slug, StringComparison.OrdinalIgnoreCase)) ?? dir;

        var files = new List<KeyValuePair<string, bool>>();
        if (Directory.Exists(dir))
        {
            foreach (string f in Directory.GetFiles(dir, "*.jsonl")) files.Add(new KeyValuePair<string, bool>(f, false));
            foreach (string s in Directory.GetDirectories(dir))
            {
                string sub = System.IO.Path.Combine(s, "subagents");
                if (Directory.Exists(sub)) foreach (string f in Directory.GetFiles(sub, "*.jsonl")) files.Add(new KeyValuePair<string, bool>(f, true));
            }
        }
        files = files.Where(f => File.GetLastWriteTimeUtc(f.Key) >= since).ToList();

        var transcripts = new List<Transcript>();
        foreach (var f in files)
        {
            Transcript t;
            try { t = Parse(f.Key, f.Value); } catch (IOException) { continue; }
            if (t.Calls.Count > 0 && (!t.End.HasValue || t.End.Value >= since)) transcripts.Add(t);
        }
        transcripts = transcripts.OrderBy(t => t.Start ?? DateTime.MinValue).ToList();

        var normRoots = packageRoots.Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r.Replace('\\', '/').TrimEnd('/').ToLowerInvariant()).Distinct().ToList();
        // A read verb, then (in the same pipeline segment) a path into the package.
        var shellRead = new Regex(ShellRead.ToString() + @"[^|;&\r\n]*(" + string.Join("|", normRoots.Select(r => Regex.Escape(r))) +
                                  (normRoots.Count > 0 ? "|" : "") + PackageNamed.ToString() + ")", RegexOptions.IgnoreCase);

        // --- per transcript findings ---
        var failedByCause = new Dictionary<string, int>();
        var failedByCommand = new Dictionary<string, Dictionary<string, object>>();
        var packageReads = new List<object>();
        var opEdits = new List<object>();
        var helpRepeats = new List<object>();
        var evals = new List<object>();
        var areaUse = new Dictionary<string, int[]>();   // area -> [transcripts, loaded before, loaded after, never]
        var areaCounts = new Dictionary<string, int[]>(); // area -> [failed, package reads, op edits, help repeats, evals, notes, failures]
        Action<string, int> bump = (area, slot) =>
        {
            if (string.IsNullOrEmpty(area)) area = "(none)";
            int[] a;
            if (!areaCounts.TryGetValue(area, out a)) areaCounts[area] = a = new int[7];
            a[slot]++;
        };
        int toolCalls = 0;

        foreach (Transcript t in transcripts)
        {
            toolCalls += t.Work;
            foreach (var kv in t.FailedByCause) failedByCause[kv.Key] = (failedByCause.ContainsKey(kv.Key) ? failedByCause[kv.Key] : 0) + kv.Value;
            var firstInArea = new Dictionary<string, int>();
            var helps = new Dictionary<string, int>();
            foreach (Call c in t.Calls)
            {
                if (c.Name == "SubagentHandback") continue;
                bool shell = c.Name == "PowerShell" || c.Name == "Bash";
                List<UnityCall> unity = shell ? UnityCommands(c.Summary) : new List<UnityCall>();
                foreach (UnityCall u in unity)
                {
                    string area = AreaOfGroup(u.Group);
                    if (!firstInArea.ContainsKey(area)) firstInArea[area] = c.Index;
                    if (!firstInArea.ContainsKey("unity")) firstInArea["unity"] = c.Index;
                }

                // Failed calls, by the command that failed.
                if (c.Failed)
                {
                    string key, area;
                    if (unity.Count > 0)
                    {
                        UnityCall u = unity[unity.Count - 1];
                        if (c.EmbeddedOnly)
                        {
                            string line = c.EmbeddedLines[0];
                            Match nm = Regex.Match(line, @"No command named '([\w.]+)");
                            UnityCall named = unity.FirstOrDefault(x => line.Contains(x.Command));
                            u = named ?? unity.FirstOrDefault(x => x.Group.StartsWith("cliext")) ?? unity[0];
                            if (nm.Success) u = new UnityCall { Group = nm.Groups[1].Value, Command = nm.Groups[1].Value + " (no such command)" };
                        }
                        key = u.Command; area = AreaOfGroup(u.Group);
                    }
                    else { key = "(" + c.Name + ")"; area = "(none)"; }
                    Dictionary<string, object> g;
                    if (!failedByCommand.TryGetValue(key, out g))
                    {
                        g = new Dictionary<string, object>();
                        g["command"] = key; g["area"] = area; g["count"] = 0; g["causes"] = new Dictionary<string, object>(); g["examples"] = new List<object>();
                        failedByCommand[key] = g;
                    }
                    g["count"] = (int)g["count"] + 1;
                    var causes = (Dictionary<string, object>)g["causes"];
                    causes[c.Cause] = (causes.ContainsKey(c.Cause) ? (int)causes[c.Cause] : 0) + 1;
                    var ex = (List<object>)g["examples"];
                    if (ex.Count < 3)
                    {
                        var e = new Dictionary<string, object>();
                        e["transcript"] = t.Label; e["cause"] = c.Cause; e["embedded"] = c.EmbeddedOnly;
                        e["call"] = Short(c.Summary, 200, true);
                        e["result"] = Short(c.EmbeddedOnly ? string.Join(" | ", c.EmbeddedLines.Take(2)) : c.Result, 200, true);
                        ex.Add(e);
                    }
                    bump(area, 0);
                }

                // Reads and searches of the package's own files.
                {
                    string low = null;
                    bool hit = false;
                    if (c.Name == "Read" || c.Name == "Grep" || c.Name == "Glob" || c.Name == "NotebookRead")
                    {
                        low = (c.FilePath + " " + c.Summary).Replace('\\', '/').ToLowerInvariant();
                        hit = normRoots.Any(r => low.Contains(r)) || PackageNamed.IsMatch(low);
                    }
                    else if (shell)
                    {
                        // Running a Tools~ script is use, not reading: the package path must follow a read verb.
                        low = c.Summary.Replace('\\', '/').ToLowerInvariant();
                        hit = shellRead.IsMatch(low);
                    }
                    if (hit)
                    {
                        {
                            string kind = Regex.IsMatch(low, @"readme|changelog|documentation~") ? "readme" : low.Contains("skills~") ? "skills~" : "source";
                            string area = "(package)";
                            Match am = Regex.Match(low, @"/editor/(vfx|shadergraph|animation|timeline|inputsystem|uitoolkit|addressables|assets)/");
                            if (am.Success)
                            {
                                string a = am.Groups[1].Value;
                                area = a == "vfx" ? "cliext.vfx" : a == "shadergraph" ? "cliext.shadergraph" : a == "animation" || a == "timeline" ? "animation"
                                    : a == "inputsystem" ? "cliext.input" : a == "uitoolkit" ? "cliext.uitk" : a == "addressables" ? "cliext.addressables" : "import";
                            }
                            var r = new Dictionary<string, object>();
                            r["transcript"] = t.Label; r["sessionId"] = t.SessionId; r["agentId"] = t.AgentId; r["callIndex"] = c.Index;
                            r["tool"] = c.Name; r["kind"] = kind; r["area"] = area; r["target"] = Short(c.Name == "Read" ? c.FilePath : c.Summary, 240, true);
                            packageReads.Add(r);
                            bump(area, 1);
                        }
                    }
                }

                // Hand edits of files cliext commands edit.
                {
                    string file = null, how = null;
                    if ((c.Name == "Edit" || c.Name == "Write" || c.Name == "MultiEdit" || c.Name == "NotebookEdit") && c.FilePath.Length > 0)
                    {
                        string ext = SafeExt(c.FilePath);
                        if (OpExts.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase))) { file = c.FilePath; how = c.Name; }
                    }
                    else if (shell)
                    {
                        // The unity commands themselves (and what they pipe into) are cliext's own edits, not hand edits.
                        file = ShellWriteTargets(UnityStripped.Replace(c.Summary, ""))
                            .FirstOrDefault(p => OpExts.Any(x => string.Equals(x, SafeExt(p), StringComparison.OrdinalIgnoreCase)));
                        if (file != null) how = c.Name + " (shell write)";
                    }
                    if (file != null)
                    {
                        string area = AreaOfExt(SafeExt(file)) ?? "(none)";
                        var r = new Dictionary<string, object>();
                        r["transcript"] = t.Label; r["sessionId"] = t.SessionId; r["agentId"] = t.AgentId; r["callIndex"] = c.Index;
                        r["tool"] = how; r["file"] = file; r["area"] = area;
                        r["created"] = c.Name == "Write" && Regex.IsMatch(c.Result, @"File created successfully", RegexOptions.IgnoreCase);
                        opEdits.Add(r);
                        bump(area, 2);
                        if (!firstInArea.ContainsKey(area)) firstInArea[area] = c.Index;
                    }
                }

                // --help calls.
                if (shell)
                    foreach (Match hm in HelpRx.Matches(c.Summary))
                    {
                        string key = hm.Groups[1].Value + (hm.Groups[2].Success ? " " + hm.Groups[2].Value : "");
                        helps[key] = (helps.ContainsKey(key) ? helps[key] : 0) + 1;
                    }

                // script eval, cliext.eval run and wait_until snippets.
                if (shell)
                    foreach (Match em in EvalRx.Matches(c.Summary))
                    {
                        string head = em.Groups[1].Value;
                        if (head.StartsWith("cliext.playmode")) continue;
                        string file = FileArgOf(em.Value);
                        string code = "";
                        Match cm = CodeArg.Match(em.Value);
                        if (cm.Success) code = cm.Groups[1].Success ? cm.Groups[1].Value : cm.Groups[2].Success ? cm.Groups[2].Value : cm.Groups[3].Value;
                        if (code.Length == 0 && file != null) code = WrittenBefore(t, c, file);
                        string low = code.ToLowerInvariant();
                        string area = low.Contains("shadergraph") || low.Contains("shader graph") ? "cliext.shadergraph"
                            : low.Contains("visualeffect") || low.Contains(".vfx") ? "cliext.vfx"
                            : low.Contains("animator") || low.Contains("animationclip") || low.Contains("playabledirector") || low.Contains("timeline") ? "animation"
                            : low.Contains("inputaction") || low.Contains("inputsystem") ? "cliext.input"
                            : low.Contains("uidocument") || low.Contains("visualelement") ? "cliext.uitk"
                            : low.Contains("addressable") ? "cliext.addressables"
                            : low.Contains("importer") || low.Contains("preset") ? "import" : "(general)";
                        var r = new Dictionary<string, object>();
                        r["transcript"] = t.Label; r["sessionId"] = t.SessionId; r["agentId"] = t.AgentId; r["callIndex"] = c.Index;
                        r["via"] = Regex.Replace(head, @"\s+", " "); r["area"] = area; r["file"] = file ?? "";
                        r["failed"] = c.Failed;
                        r["excerpt"] = Short(code.Length > 0 ? code : em.Value, 200, true);
                        evals.Add(r);
                        bump(area, 4);
                    }
            }
            foreach (var h in helps.Where(h => h.Value >= 3))
            {
                var r = new Dictionary<string, object>();
                r["transcript"] = t.Label; r["sessionId"] = t.SessionId; r["agentId"] = t.AgentId; r["command"] = h.Key; r["count"] = h.Value;
                helpRepeats.Add(r);
                bump(AreaOfGroup(h.Key.Split(' ')[0]), 3);
            }

            // Skill loaded before the first call in each area.
            foreach (var kv in firstInArea)
            {
                string area = kv.Key == "unity" ? "(any unity command)" : kv.Key;
                string skill = kv.Key == "unity" ? "unity-cliext" : AreaSkill.ContainsKey(kv.Key) ? AreaSkill[kv.Key] : null;
                if (skill == null) continue;
                int loaded = t.Skills.Where(s => { string n = s.Value.Replace(" (Read)", ""); int colon = n.LastIndexOf(':'); if (colon >= 0) n = n.Substring(colon + 1); return n == skill; })
                    .Select(s => s.Key).DefaultIfEmpty(-1).Min();
                int[] a;
                if (!areaUse.TryGetValue(area, out a)) areaUse[area] = a = new int[4];
                a[0]++;
                if (loaded < 0) a[3]++; else if (loaded < kv.Value) a[1]++; else a[2]++;
            }
        }

        // --- the failure log and the field notes ---
        string state = System.IO.Path.Combine(project, "Library", "CLIExtensions");
        var notes = ReadJsonLines(System.IO.Path.Combine(state, "field-notes.jsonl"))
            .Where(n => { DateTime? w = ParseTime(Str(n, "time")); return w.HasValue && w.Value >= since; }).ToList();
        var failures = ReadJsonLines(System.IO.Path.Combine(state, "failures.jsonl"))
            .Where(n => { DateTime? w = ParseTime(Str(n, "time")); return w.HasValue && w.Value >= since; }).ToList();

        // Every call with a time, for matching.
        var timed = transcripts.SelectMany(t => t.Calls.Where(c => c.Ts.HasValue && c.Name != "SubagentHandback").Select(c => new KeyValuePair<Transcript, Call>(t, c)))
            .OrderBy(x => x.Value.Ts.Value).ToList();
        Func<DateTime, IEnumerable<KeyValuePair<Transcript, Call>>> near = when =>
        {
            // Calls start at most a few hours before the entry; the window check does the rest.
            DateTime lo = when.AddHours(-3);
            int i = LowerBound(timed, lo);
            var list = new List<KeyValuePair<Transcript, Call>>();
            for (; i < timed.Count && timed[i].Value.Ts.Value <= when.AddSeconds(5); i++)
                if (InWindow(timed[i].Value, when)) list.Add(timed[i]);
            return list;
        };

        // One context per call: several failures (and a note) can come from the same call.
        var contexts = new Dictionary<string, object>();
        var contextIds = new Dictionary<string, string>();
        Func<Match2, string> contextId = m =>
        {
            string key = m.T.Path + "#" + m.C.Index, id;
            if (!contextIds.TryGetValue(key, out id))
            {
                id = "c" + (contextIds.Count + 1);
                contextIds[key] = id;
                contexts[id] = Context(m, before, after, excerpt);
            }
            return id;
        };

        var noteItems = new List<object>();
        var sessionsWithContext = new HashSet<string>();
        int notesMatched = 0;
        foreach (var n in notes)
        {
            var item = new Dictionary<string, object>();
            foreach (string k in new[] { "time", "kind", "command", "what", "workaround", "unity", "pipeline", "package" }) item[k] = Str(n, k) ?? "";
            DateTime when = ParseTime(Str(n, "time")).Value;
            string what = Norm(Str(n, "what"));
            string probe = what.Length > 60 ? what.Substring(0, 60) : what;
            var cands = near(when).Where(x => Regex.IsMatch(x.Value.Summary, @"failures\s+note\b")).ToList();
            var matched = cands.Where(x => probe.Length > 0 && (Norm(x.Value.Summary).Contains(probe) || Norm(x.Value.Result).Contains(probe)
                                                              || Norm(WrittenBefore(x.Key, x.Value, FileArgOf(x.Value.Summary))).Contains(probe)))
                .Select(x => new Match2 { T = x.Key, C = x.Value, Score = 1, Seconds = Math.Abs((x.Value.Ts.Value - when).TotalSeconds) });
            Match2 best = Best(matched);
            if (best != null)
            {
                item["context"] = contextId(best);
                sessionsWithContext.Add(best.T.Path);
                notesMatched++;
            }
            else item["unmatched"] = cands.Count == 0
                ? "No transcript in the window has a cliext.failures note call at that time (written by hand, by a script, or from a session outside the window)."
                : cands.Count + " cliext.failures note call(s) ran at that time, but none carries this note's text; not guessing.";
            noteItems.Add(item);
            bump(Str(n, "command") == null || Str(n, "command").Length == 0 ? "(none)" : AreaOfGroup(Str(n, "command").Split(' ')[0]), 5);
        }

        var failureItems = new List<object>();
        int failuresMatched = 0;
        var failureGroups = new Dictionary<string, Dictionary<string, object>>();
        foreach (var f in failures)
        {
            string command = Str(f, "command") ?? "";
            string message = Str(f, "message") ?? "";
            string key = command.Length > 0 ? command : "(exception: " + (Str(f, "exception") ?? "") + ")";
            Dictionary<string, object> g;
            if (!failureGroups.TryGetValue(key, out g))
            {
                g = new Dictionary<string, object>();
                g["command"] = key; g["count"] = 0; g["matched"] = 0; g["lastTime"] = ""; g["lastMessage"] = "";
                failureGroups[key] = g;
            }
            g["count"] = (int)g["count"] + 1; g["lastTime"] = Str(f, "time") ?? ""; g["lastMessage"] = Short(message, 200, true);
            bump(command.Length > 0 ? AreaOfGroup(command.Split(' ')[0]) : "(none)", 6);

            DateTime when = ParseTime(Str(f, "time")).Value;
            string msg = Norm(message);
            string probe = msg.Length > 60 ? msg.Substring(0, 60) : msg;
            var parameters = (Get(f, "parameters") as IList ?? new object[0]).Cast<object>().Select(p => Convert.ToString(p)).ToList();
            var cands = near(when).Where(x =>
            {
                string s = x.Value.Summary;
                if (!ShellUnityRx.IsMatch(s) && x.Value.Name != "PowerShell" && x.Value.Name != "Bash") return false;
                if (command.Length > 0 && !Regex.IsMatch(s, Regex.Escape(command).Replace("\\ ", @"\s+") + @"\b")) return false;
                if (command.Length == 0 && !s.Contains("cliext")) return false;
                return parameters.All(p => s.Contains("--" + p));
            }).ToList();
            var scored = cands.Select(x => new Match2
            {
                T = x.Key, C = x.Value,
                Score = (probe.Length > 0 && Norm(x.Value.Result).Contains(probe) ? 2 : 0) + (x.Value.Failed ? 1 : 0),
                Seconds = Math.Abs((x.Value.Ts.Value - when).TotalSeconds),
            }).Where(x => x.Score > 0 || command.Length > 0);
            Match2 best = Best(scored.Where(x => x.Score > 0 || cands.Count == 1));
            if (best == null || (command.Length == 0 && best.Score < 2)) continue;   // unmatched: counted in its group only
            failuresMatched++;
            g["matched"] = (int)g["matched"] + 1;
            var item = new Dictionary<string, object>();
            foreach (string k in new[] { "time", "command", "message", "exception", "unity", "pipeline", "package" }) item[k] = k == "message" ? Short(Str(f, k), 600, true) : (Str(f, k) ?? "");
            item["parameters"] = parameters;
            item["matchedBy"] = best.Score >= 2 ? "time, command and error text" : best.C.Failed ? "time, command and a failed call" : "time and command";
            item["context"] = contextId(best);
            sessionsWithContext.Add(best.T.Path);
            failureItems.Add(item);
        }

        // --- assemble ---
        var report = new Dictionary<string, object>();
        report["generated"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
        report["project"] = project;
        report["days"] = days;
        report["since"] = since.ToString("yyyy-MM-ddTHH:mm:ssZ");
        report["transcriptFolder"] = dir;
        report["transcriptFolderFound"] = Directory.Exists(dir);
        report["transcripts"] = transcripts.Count(t => !t.IsSubagent);
        report["subagentTranscripts"] = transcripts.Count(t => t.IsSubagent);
        report["toolCalls"] = toolCalls;
        report["failedCalls"] = transcripts.Sum(t => t.FailedCount);
        report["failedByCause"] = failedByCause.OrderByDescending(x => x.Value).ToDictionary(x => x.Key, x => (object)x.Value);
        report["failedByCommand"] = failedByCommand.Values.OrderByDescending(x => (int)x["count"]).Cast<object>().ToList();
        report["packageReads"] = packageReads;
        report["opFileEdits"] = opEdits;
        report["helpRepeats"] = helpRepeats;
        report["evals"] = evals;
        report["skillsBeforeFirstCall"] = areaUse.OrderBy(x => x.Key).Select(x =>
        {
            var d = new Dictionary<string, object>();
            d["area"] = x.Key;
            d["skill"] = x.Key == "(any unity command)" ? "unity-cliext" : AreaSkill[x.Key];
            d["transcripts"] = x.Value[0]; d["loadedBefore"] = x.Value[1]; d["loadedAfter"] = x.Value[2]; d["never"] = x.Value[3];
            return (object)d;
        }).ToList();
        report["areas"] = areaCounts.OrderBy(x => x.Key).Select(x =>
        {
            var d = new Dictionary<string, object>();
            d["area"] = x.Key; d["failedCalls"] = x.Value[0]; d["packageReads"] = x.Value[1]; d["opFileEdits"] = x.Value[2];
            d["helpRepeats"] = x.Value[3]; d["evals"] = x.Value[4]; d["notes"] = x.Value[5]; d["failures"] = x.Value[6];
            return (object)d;
        }).ToList();
        var noteGroups = notes.GroupBy(n => (Str(n, "kind") ?? "") + "|" + (Str(n, "command") ?? "")).Select(gr =>
        {
            var d = new Dictionary<string, object>();
            d["kind"] = Str(gr.First(), "kind") ?? ""; d["command"] = Str(gr.First(), "command") ?? ""; d["count"] = gr.Count();
            d["lastWhat"] = Str(gr.Last(), "what") ?? ""; d["lastTime"] = Str(gr.Last(), "time") ?? "";
            return d;
        }).OrderByDescending(d => (int)d["count"]).Cast<object>().ToList();
        var notesSection = new Dictionary<string, object>();
        notesSection["count"] = notes.Count; notesSection["matched"] = notesMatched; notesSection["groups"] = noteGroups; notesSection["items"] = noteItems;
        report["notes"] = notesSection;
        var failSection = new Dictionary<string, object>();
        failSection["count"] = failures.Count; failSection["matched"] = failuresMatched;
        failSection["groups"] = failureGroups.Values.OrderByDescending(x => (int)x["count"]).Cast<object>().ToList();
        failSection["items"] = failureItems;
        report["failures"] = failSection;
        report["contexts"] = contexts;
        report["sessionsWithContext"] = sessionsWithContext.OrderBy(x => x).Cast<object>().ToList();
        report["sessions"] = transcripts.Select(t =>
        {
            var d = new Dictionary<string, object>();
            d["label"] = t.Label; d["sessionId"] = t.SessionId; d["agentId"] = t.AgentId; d["description"] = t.Description; d["path"] = t.Path;
            d["start"] = t.Start.HasValue ? t.Start.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : "";
            d["end"] = t.End.HasValue ? t.End.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : "";
            d["toolCalls"] = t.Work; d["failedCalls"] = t.FailedCount; d["embeddedFailures"] = t.EmbeddedCount;
            d["failedByCause"] = t.FailedByCause.ToDictionary(x => x.Key, x => (object)x.Value);
            d["skillsLoaded"] = t.Skills.Select(s => (object)s.Value).ToList();
            return (object)d;
        }).ToList();
        report["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);

        Directory.CreateDirectory(outDir);
        string jsonPath = System.IO.Path.Combine(outDir, "field-report.json");
        string mdPath = System.IO.Path.Combine(outDir, "field-report.md");
        var sb = new StringBuilder();
        WriteJson(report, sb, 0);
        File.WriteAllText(jsonPath, sb.ToString() + "\n", new UTF8Encoding(false));
        File.WriteAllText(mdPath, Markdown(report, maxFailureContexts), new UTF8Encoding(false));
        return mdPath + "\n" + jsonPath;
    }

    static int LowerBound(List<KeyValuePair<Transcript, Call>> list, DateTime t)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (list[mid].Value.Ts.Value < t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Output

    static void WriteJson(object o, StringBuilder sb, int indent)
    {
        string pad = new string(' ', indent * 2), pad1 = new string(' ', (indent + 1) * 2);
        if (o == null) { sb.Append("null"); return; }
        if (o is string) { sb.Append(Js.Serialize((string)o)); return; }
        if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
        if (o is int || o is long || o is double || o is decimal || o is float) { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
        var dict = o as IDictionary<string, object>;
        if (dict != null)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            int i = 0;
            foreach (var kv in dict)
            {
                sb.Append(pad1).Append(Js.Serialize(kv.Key)).Append(": ");
                WriteJson(kv.Value, sb, indent + 1);
                if (++i < dict.Count) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(pad).Append('}');
            return;
        }
        var list = o as IEnumerable;
        if (list != null)
        {
            var items = list.Cast<object>().ToList();
            if (items.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(pad1);
                WriteJson(items[i], sb, indent + 1);
                if (i < items.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(pad).Append(']');
            return;
        }
        sb.Append(Js.Serialize(Convert.ToString(o, CultureInfo.InvariantCulture)));
    }

    static string Cell(object o)
    {
        string s = Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
        return Regex.Replace(s, @"\s+", " ").Replace("|", "\\|").Trim();
    }

    static string Code(string s)
    {
        return "`" + Cell(s).Replace("`", "'") + "`";
    }

    static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> r, string key)
    {
        return ((IEnumerable)r[key]).Cast<Dictionary<string, object>>();
    }

    static void ContextBlock(StringBuilder md, Dictionary<string, object> r, string id, Dictionary<string, string> shown, string here)
    {
        string first;
        if (shown.TryGetValue(id, out first)) { md.Append("Same call and context as ").Append(first).Append(".\n\n"); return; }
        shown[id] = here;
        var m = (Dictionary<string, object>)((Dictionary<string, object>)r["contexts"])[id];
        md.Append("Session `").Append(m["sessionId"]).Append("`");
        if (Convert.ToString(m["agentId"]).Length > 0) md.Append(", agent `").Append(m["agentId"]).Append("`");
        if (Convert.ToString(m["description"]).Length > 0) md.Append(" (").Append(Cell(m["description"])).Append(")");
        md.Append(", call ").Append(m["callIndex"]).Append(" of `").Append(m["transcript"]).Append("`\n\n");
        md.Append("````text\n");
        foreach (Dictionary<string, object> c in (IEnumerable)m["calls"])
        {
            int off = (int)c["offset"];
            md.Append(off == 0 ? ">>> " : "    ").Append("[").Append(off >= 0 ? "+" + off : off.ToString()).Append("] ")
              .Append(c["time"]).Append(' ').Append(c["tool"]).Append((bool)c["failed"] ? " (failed)" : "").Append('\n');
            string input = Convert.ToString(c["input"]).Replace("````", "'''");
            foreach (string line in input.Split('\n')) md.Append("      $ ").Append(line.TrimEnd('\r')).Append('\n');
            string output = Convert.ToString(c["output"]).Replace("````", "'''");
            if (output.Length > 0) md.Append("      = ").Append(output).Append('\n');
        }
        md.Append("````\n\n");
    }

    static string Markdown(Dictionary<string, object> r, int maxFailureContexts)
    {
        var md = new StringBuilder();
        var shown = new Dictionary<string, string>();
        md.Append("# CLI Extensions field report\n\n");
        md.Append("Project `").Append(r["project"]).Append("`, the last ").Append(r["days"]).Append(" days (since ").Append(r["since"]).Append("), generated ").Append(r["generated"]).Append(" in ").Append(r["seconds"]).Append(" s.\n\n");
        if (!(bool)r["transcriptFolderFound"]) md.Append("**No transcript folder at `").Append(r["transcriptFolder"]).Append("`**: only the failure log and the field notes are reported.\n\n");
        md.Append("Read ").Append(r["transcripts"]).Append(" session and ").Append(r["subagentTranscripts"]).Append(" subagent transcripts with ").Append(r["toolCalls"]).Append(" tool calls; ")
          .Append(r["failedCalls"]).Append(" failed (")
          .Append(string.Join(", ", ((Dictionary<string, object>)r["failedByCause"]).Select(x => x.Value + " " + x.Key))).Append("). ");
        var notes = (Dictionary<string, object>)r["notes"]; var fails = (Dictionary<string, object>)r["failures"];
        md.Append(notes["count"]).Append(" field notes (").Append(notes["matched"]).Append(" matched to a call) and ")
          .Append(fails["count"]).Append(" failure log entries (").Append(fails["matched"]).Append(" matched to a call). ")
          .Append("Outputs are excerpts; tool arguments are shown in full and can hold project code, paths and prompts.\n\n");

        md.Append("## By area\n\n| Area | Failed calls | Package reads | Hand edits | Repeated --help | Evals | Notes | Failure log |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var a in Rows(r, "areas"))
            md.Append("| ").Append(Cell(a["area"])).Append(" | ").Append(a["failedCalls"]).Append(" | ").Append(a["packageReads"]).Append(" | ").Append(a["opFileEdits"]).Append(" | ")
              .Append(a["helpRepeats"]).Append(" | ").Append(a["evals"]).Append(" | ").Append(a["notes"]).Append(" | ").Append(a["failures"]).Append(" |\n");
        md.Append('\n');

        md.Append("## Skill loaded before the first call in its area\n\n| Area | Skill | Transcripts | Before | After | Never |\n|---|---|---|---|---|---|\n");
        foreach (var a in Rows(r, "skillsBeforeFirstCall"))
            md.Append("| ").Append(Cell(a["area"])).Append(" | ").Append(Cell(a["skill"])).Append(" | ").Append(a["transcripts"]).Append(" | ").Append(a["loadedBefore"]).Append(" | ")
              .Append(a["loadedAfter"]).Append(" | ").Append(a["never"]).Append(" |\n");
        md.Append('\n');

        md.Append("## Failed tool calls by command\n\nCauses as T3's score.py classifies them; *embedded* is an error printed by a call that exited 0.\n\n| Command | Area | Count | Causes | Example |\n|---|---|---|---|---|\n");
        foreach (var g in Rows(r, "failedByCommand").Take(60))
        {
            var ex = ((List<object>)g["examples"]).Cast<Dictionary<string, object>>().FirstOrDefault();
            md.Append("| ").Append(Code((string)g["command"])).Append(" | ").Append(Cell(g["area"])).Append(" | ").Append(g["count"]).Append(" | ")
              .Append(Cell(string.Join(", ", ((Dictionary<string, object>)g["causes"]).Select(x => x.Value + " " + x.Key)))).Append(" | ")
              .Append(ex == null ? "" : Cell(ex["transcript"]) + ": " + Code(Convert.ToString(ex["result"]))).Append(" |\n");
        }
        md.Append('\n');

        Action<string, string, string[], string[]> table = (title, key, heads, cols) =>
        {
            var rows = Rows(r, key).ToList();
            md.Append("## ").Append(title).Append(" (").Append(rows.Count).Append(")\n\n");
            if (rows.Count == 0) { md.Append("None.\n\n"); return; }
            md.Append("| ").Append(string.Join(" | ", heads)).Append(" |\n|").Append(string.Concat(heads.Select(h => "---|"))).Append('\n');
            foreach (var row in rows.Take(80))
                md.Append("| ").Append(string.Join(" | ", cols.Select(c => c == "excerpt" || c == "target" || c == "file" || c == "command" ? Code(Convert.ToString(row[c])) : Cell(row[c])))).Append(" |\n");
            if (rows.Count > 80) md.Append("\n").Append(rows.Count - 80).Append(" more in field-report.json.\n");
            md.Append('\n');
        };
        table("Reads of the package's source or README", "packageReads", new[] { "Transcript", "Call", "Tool", "Kind", "Area", "Target" }, new[] { "transcript", "callIndex", "tool", "kind", "area", "target" });
        table("Edits of files cliext commands edit", "opFileEdits", new[] { "Transcript", "Call", "Tool", "Area", "New file", "File" }, new[] { "transcript", "callIndex", "tool", "area", "created", "file" });
        table("Three or more --help calls on one command in one transcript", "helpRepeats", new[] { "Transcript", "Command", "Calls" }, new[] { "transcript", "command", "count" });
        table("script eval and cliext.eval calls", "evals", new[] { "Transcript", "Call", "Via", "Area (guessed)", "Failed", "Excerpt" }, new[] { "transcript", "callIndex", "via", "area", "failed", "excerpt" });

        md.Append("## Field notes (").Append(notes["count"]).Append(")\n\n");
        var noteGroups = ((List<object>)notes["groups"]).Cast<Dictionary<string, object>>().ToList();
        if (noteGroups.Count == 0) md.Append("None in the window.\n\n");
        else
        {
            md.Append("| Kind | Command | Count | Last |\n|---|---|---|---|\n");
            foreach (var g in noteGroups) md.Append("| ").Append(Cell(g["kind"])).Append(" | ").Append(Code((string)g["command"])).Append(" | ").Append(g["count"]).Append(" | ").Append(Cell(g["lastWhat"])).Append(" |\n");
            md.Append('\n');
            int i = 0;
            foreach (Dictionary<string, object> n in (List<object>)notes["items"])
            {
                md.Append("### Note ").Append(++i).Append(": ").Append(Cell(n["kind"])).Append(", ").Append(Code(Convert.ToString(n["command"]))).Append(", ").Append(n["time"]).Append("\n\n");
                md.Append("- What: ").Append(Cell(n["what"])).Append('\n');
                if (Convert.ToString(n["workaround"]).Length > 0) md.Append("- Workaround: ").Append(Cell(n["workaround"])).Append('\n');
                md.Append("- Versions: Unity ").Append(n["unity"]).Append(", Pipeline ").Append(n["pipeline"]).Append(", package ").Append(n["package"]).Append("\n\n");
                if (n.ContainsKey("context")) ContextBlock(md, r, (string)n["context"], shown, "note " + i);
                else md.Append("Not matched to a transcript: ").Append(n["unmatched"]).Append("\n\n");
            }
        }

        md.Append("## Failure log (").Append(fails["count"]).Append(" entries, ").Append(fails["matched"]).Append(" matched to a call)\n\n");
        var fgroups = ((List<object>)fails["groups"]).Cast<Dictionary<string, object>>().ToList();
        if (fgroups.Count == 0) md.Append("None in the window.\n\n");
        else
        {
            md.Append("Entries that match no call came from scripts (such as cliext-clitest.ps1), the Editor itself, or sessions outside the window.\n\n");
            md.Append("| Command | Entries | Matched | Last error |\n|---|---|---|---|\n");
            foreach (var g in fgroups.Take(60)) md.Append("| ").Append(Code((string)g["command"])).Append(" | ").Append(g["count"]).Append(" | ").Append(g["matched"]).Append(" | ").Append(Cell(g["lastMessage"])).Append(" |\n");
            md.Append('\n');
            var items = ((List<object>)fails["items"]).Cast<Dictionary<string, object>>().OrderByDescending(x => (string)x["time"]).ToList();
            int i = 0;
            foreach (var f in items.Take(maxFailureContexts))
            {
                md.Append("### Failure ").Append(++i).Append(": ").Append(Code((string)f["command"])).Append(", ").Append(f["time"]).Append("\n\n");
                md.Append("- Error: ").Append(Cell(f["message"])).Append('\n');
                md.Append("- Parameters (names): ").Append(Cell(string.Join(", ", ((List<string>)f["parameters"])))).Append('\n');
                md.Append("- Matched by ").Append(f["matchedBy"]).Append("\n\n");
                ContextBlock(md, r, (string)f["context"], shown, "failure " + i);
            }
            if (items.Count > maxFailureContexts) md.Append(items.Count - maxFailureContexts).Append(" older matched failures are in field-report.json.\n\n");
        }

        md.Append("## Transcripts\n\n| Transcript | Description | Start | Calls | Failed | Skills loaded |\n|---|---|---|---|---|---|\n");
        foreach (var s in Rows(r, "sessions").OrderByDescending(x => (string)x["start"]).Take(150))
            md.Append("| ").Append(Cell(s["label"])).Append(" | ").Append(Cell(s["description"])).Append(" | ").Append(s["start"]).Append(" | ").Append(s["toolCalls"]).Append(" | ")
              .Append(s["failedCalls"]).Append(((Dictionary<string, object>)s["failedByCause"]).Count > 0 ? " (" + string.Join(", ", ((Dictionary<string, object>)s["failedByCause"]).Select(x => x.Value + " " + x.Key)) + ")" : "")
              .Append(" | ").Append(Cell(string.Join(", ", ((List<object>)s["skillsLoaded"])))).Append(" |\n");
        return md.ToString();
    }
}
'@

if (-not ('CliextFieldReport' -as [type])) {
    Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies System.Web.Extensions, System.Core
}
$written = [CliextFieldReport]::Run($projectPath, $TranscriptRoot, $roots.ToArray(), $Days, $Out, $Before, $After, $Excerpt, $MaxFailureContexts)
if (-not $Quiet) { $written -split "`n" | ForEach-Object { Write-Output $_ } }
exit 0

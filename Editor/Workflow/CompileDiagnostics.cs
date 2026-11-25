using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>One compiler error or warning.</summary>
    [Serializable]
    public class CompileMessage
    {
        /// <summary><c>error</c> or <c>warning</c>.</summary>
        public string severity;
        /// <summary>The compiler's code, such as <c>CS0103</c>, when it gave one.</summary>
        public string code;
        /// <summary>Project-relative path, as the compiler reports it.</summary>
        public string file;
        public int line;
        public int column;
        /// <summary>The message without the file, position and code.</summary>
        public string message;
        /// <summary>The assembly being compiled, such as <c>Assembly-CSharp.dll</c>.</summary>
        public string assembly;
    }

    /// <summary>
    /// Keeps the errors and warnings of each assembly's last compile in <c>Library/CLIExtensions/compile.json</c>, so they
    /// survive the domain reload that follows a compile and stay readable when nothing recompiled.
    /// </summary>
    /// <remarks>
    /// Pipeline 1.0's <c>script recompile_status</c> lists the last compile's error lines as strings, with no warnings, and
    /// none when scripts still fail but nothing recompiled. This fills that gap: errors and warnings with file, line,
    /// column and code, per assembly, standing until that assembly compiles again.
    /// </remarks>
    [InitializeOnLoad]
    public static class CompileDiagnostics
    {
        [Serializable]
        private class AssemblyMessages
        {
            public string assembly;
            public string compiledAtUtc;
            public List<CompileMessage> messages = new List<CompileMessage>();
        }

        [Serializable]
        private class Store
        {
            public string lastCompileStartedUtc;
            public string lastCompileFinishedUtc;
            public List<AssemblyMessages> assemblies = new List<AssemblyMessages>();
        }

        // "Assets/Foo.cs(12,5): error CS0103: The name 'x' does not exist in the current context"
        private static readonly Regex s_Line = new Regex(@"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning)\s*(?<code>[A-Z]+\d+)?\s*:\s*(?<msg>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        private static string StoreFile => Path.Combine(CliExtPaths.StateDirectory, "compile.json");

        static CompileDiagnostics()
        {
            CompilationPipeline.compilationStarted -= OnStarted;
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            CompilationPipeline.compilationFinished -= OnFinished;
            CompilationPipeline.compilationFinished += OnFinished;
        }

        /// <summary>When the last compile started and finished (UTC, ISO 8601), or null.</summary>
        public static (string started, string finished) LastCompile
        {
            get
            {
                Store store = Load();
                return (store.lastCompileStartedUtc, store.lastCompileFinishedUtc);
            }
        }

        /// <summary>True while a compile that started has not finished yet.</summary>
        public static bool CompileInProgress => EditorApplication.isCompiling;

        /// <summary>
        /// The standing errors and warnings: each existing assembly's messages from its last compile. Errors first.
        /// </summary>
        /// <param name="includeWarnings">False for errors only.</param>
        public static List<CompileMessage> Current(bool includeWarnings = true)
        {
            var existing = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player))
                .Select(a => Path.GetFileName(a.outputPath)), StringComparer.OrdinalIgnoreCase);

            return Load().assemblies
                .Where(a => existing.Count == 0 || existing.Contains(a.assembly))
                .SelectMany(a => a.messages)
                .Where(m => includeWarnings || m.severity == "error")
                .OrderBy(m => m.severity == "error" ? 0 : 1).ThenBy(m => m.file).ThenBy(m => m.line)
                .ToList();
        }

        /// <summary>Parses one compiler message. Public for tests.</summary>
        public static CompileMessage Parse(string assembly, string text, CompilerMessageType type)
        {
            var result = new CompileMessage
            {
                assembly = Path.GetFileName(assembly),
                severity = type == CompilerMessageType.Error ? "error" : "warning",
                message = text?.Trim()
            };
            if (text == null) return result;
            Match m = s_Line.Match(text.Trim());
            if (!m.Success) return result;
            result.file = m.Groups["file"].Value.Replace('\\', '/');
            result.line = int.Parse(m.Groups["line"].Value);
            result.column = int.Parse(m.Groups["col"].Value);
            result.code = m.Groups["code"].Success ? m.Groups["code"].Value : null;
            result.message = m.Groups["msg"].Value.Trim();
            return result;
        }

        private static void OnStarted(object _)
        {
            Store store = Load();
            store.lastCompileStartedUtc = DateTime.UtcNow.ToString("o");
            Save(store);
        }

        private static void OnAssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            Store store = Load();
            string name = Path.GetFileName(assembly);
            store.assemblies.RemoveAll(a => string.Equals(a.assembly, name, StringComparison.OrdinalIgnoreCase));
            store.assemblies.Add(new AssemblyMessages
            {
                assembly = name,
                compiledAtUtc = DateTime.UtcNow.ToString("o"),
                messages = (messages ?? Array.Empty<CompilerMessage>())
                    .Where(m => m.type == CompilerMessageType.Error || m.type == CompilerMessageType.Warning)
                    .Select(m => Parse(assembly, m.message, m.type))
                    .ToList()
            });
            Save(store);
        }

        /// <summary>The file that says a compile was asked for (cliext.compile run --async) and hasn't finished; cliext-wait.ps1 reads it.</summary>
        public static string PendingFile => Path.Combine(CliExtPaths.StateDirectory, "compile-pending");

        public static void MarkPending()
        {
            try { File.WriteAllText(PendingFile, DateTime.UtcNow.ToString("o")); } catch (IOException) { }
        }

        public static void ClearPending()
        {
            try { if (File.Exists(PendingFile)) File.Delete(PendingFile); } catch (IOException) { }
        }

        private static void OnFinished(object _)
        {
            ClearPending();
            Store store = Load();
            store.lastCompileFinishedUtc = DateTime.UtcNow.ToString("o");
            Save(store);
        }

        private static Store Load()
        {
            try
            {
                if (File.Exists(StoreFile)) return JsonUtility.FromJson<Store>(File.ReadAllText(StoreFile)) ?? new Store();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CLI Extensions] Could not read {StoreFile}: {e.Message}");
            }
            return new Store();
        }

        private static void Save(Store store)
        {
            try { File.WriteAllText(StoreFile, JsonUtility.ToJson(store)); }
            catch (IOException e) { Debug.LogWarning($"[CLI Extensions] Could not write {StoreFile}: {e.Message}"); }
        }
    }
}

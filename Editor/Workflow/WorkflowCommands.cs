using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Commands for the agent's edit-compile-play loop: wait for the Editor to be idle, compile and read the errors and
    /// warnings as data, and turn on faster Play mode with a check for the static state it breaks.
    /// </summary>
    internal static class WorkflowCommands
    {
        private const string WaitGroup = "cliext.wait";
        private const string CompileGroup = "cliext.compile";
        private const string PlayModeGroup = "cliext.playmode";

        // ---------------------------------------------------------------------------------------------------------
        // cliext.wait

        [CliGroupCommand(WaitGroup, "Wait until the Editor is idle: not compiling, not importing, not changing Play mode, for a few frames in a row. " +
                                    "Returns how long it waited and the standing compile errors. A compile that succeeds reloads the domain, which drops this request: " +
                                    "call it again (or use Tools~/cliext-wait.ps1, which waits from outside the Editor across reloads).",
            Tags = new[] { "editor", "cliext", "wait" }, Effect = CommandEffect.ReadOnly)]
        internal static async Task<ExitCode> Idle(
            CommandContext cmd,
            [CliArg("timeout", "Seconds to wait")] double timeoutInSeconds = 120,
            [CliArg("refresh", "Refresh the AssetDatabase first, so files written outside the Editor are imported (and scripts compiled) before waiting")] bool refresh = false,
            [CliArg("settle-frames", "Idle frames in a row that count as idle")] int settleFrames = 3)
        {
            var clock = Stopwatch.StartNew();
            // Stop a little before the request's own timeout, so the reply says "timed out" instead of the server.
            double budget = double.IsPositiveInfinity(timeoutInSeconds) ? double.MaxValue : Math.Max(0, timeoutInSeconds - 1);
            if (refresh) AssetDatabase.Refresh();

            int idleFrames = 0, frames = 0;
            while (true)
            {
                cmd.CancellationToken.ThrowIfCancellationRequested();
                idleFrames = EditorDeferred.IsIdle() ? idleFrames + 1 : 0;
                if (idleFrames >= Math.Max(1, settleFrames)) break;
                if (clock.Elapsed.TotalSeconds >= budget)
                {
                    cmd.Result.Write(IdleResult(false, clock, frames));
                    cmd.Log.Error($"The Editor was still busy after {clock.Elapsed.TotalSeconds:0} s (compiling: {EditorApplication.isCompiling}, importing: {EditorApplication.isUpdating}).");
                    return ExitCode.TimedOut;
                }
                frames++;
                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Yield();
            }

            cmd.Result.Write(IdleResult(true, clock, frames));
            return ExitCode.Success;
        }

        private static object IdleResult(bool idle, Stopwatch clock, int frames)
        {
            var errors = CompileDiagnostics.Current(includeWarnings: false);
            return new
            {
                idle,
                waitedMs = clock.ElapsedMilliseconds,
                frames,
                playMode = EditorApplication.isPlaying ? (EditorApplication.isPaused ? "paused" : "playing") : "edit",
                compiling = EditorApplication.isCompiling,
                importing = EditorApplication.isUpdating,
                compileErrors = errors.Count,
                firstCompileError = errors.FirstOrDefault()
            };
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.compile

        [CliGroupCommand(CompileGroup, "Refresh the AssetDatabase, compile what changed, wait for the compile, and return its errors and warnings with file, line, column and code. " +
                                       "Fails (exit 6) when there are errors, with them in the result. When the compile succeeds, the domain reload that follows may drop the reply " +
                                       "(the CLI then also exits 6, 'closed the connection'): then read the result with cliext.compile diagnostics. " +
                                       "--async true answers at once (exit 0) and compiles after the reply: wait with Tools~/cliext-wait.ps1, then read cliext.compile diagnostics. " +
                                       "--stop-play true leaves Play mode first (nothing compiles while playing).",
            Name = "run", Tags = new[] { "editor", "cliext", "scripts/compile" }, Effect = CommandEffect.Idempotent)]
        internal static Task<ExitCode> CompileRun(
            CommandContext cmd,
            [CliArg("timeout", "Seconds to wait for the compile")] double timeoutInSeconds = 300,
            [CliArg("warnings", "Include warnings")] bool warnings = true,
            [CliArg("async", "Answer at once and compile after the reply, so the exit code doesn't depend on the domain reload")] bool runAsync = false,
            [CliArg("stop-play", "Leave Play mode first (nothing compiles while playing)")] bool stopPlay = false)
        {
            if (stopPlay && EditorApplication.isPlayingOrWillChangePlaymode) return StopPlayThenCompile(cmd, timeoutInSeconds, warnings, runAsync);
            return CompileAndReport(cmd, timeoutInSeconds, warnings, runAsync);
        }

        private static async Task<ExitCode> StopPlayThenCompile(CommandContext cmd, double timeoutInSeconds, bool warnings, bool runAsync)
        {
            EditorApplication.ExitPlaymode();
            var clock = Stopwatch.StartNew();
            while (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (clock.Elapsed.TotalSeconds > 60) { cmd.Log.Error("Play mode didn't stop within 60 s."); return ExitCode.TimedOut; }
                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Yield();
            }
            cmd.Log.Info("Left Play mode.");
            return await CompileAndReport(cmd, timeoutInSeconds, warnings, runAsync);
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.eval

        [CliGroupCommand("cliext.eval", "Run a C# snippet file through Pipeline's script eval after filling its {{key}} placeholders from --vars (key=value;key=value), " +
                                        "so one snippet serves many values. A placeholder left unfilled is an error. Returns the snippet's value. " +
                                        "Pass the CLI's --timeout for anything slower than 30 s.",
            Name = "run", Tags = new[] { "editor", "cliext", "scripts/eval" }, MainThreadRequired = true, Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> EvalRun(
            CommandContext cmd,
            [CliArg("file", "The .cs snippet (relative to the project root unless absolute)", Required = true)] string file,
            [CliArg("vars", "Placeholder values: key=value;key=value")] string vars = "",
            [CliArg("internals", "Compile under an assembly name Unity's modules grant internal access, as script eval --internals does")] bool internals = false,
            [CliArg("print", "Only fill the placeholders and return the code, without running it")] bool print = false,
            [CliArg("timeout", "Seconds the CLI waits for the snippet (as script eval's --timeout)")] double timeoutInSeconds = 30)
        {
            string code;
            try { code = TemplatedEval.Load(file, TemplatedEval.ParseVars(vars)); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.InvalidUsage; }
            if (print)
            {
                cmd.Result.Write(new { code });
                return ExitCode.Success;
            }
            var result = await TemplatedEval.Evaluate(code, internals);
            foreach (var d in result.diagnostics) (d.Contains(": error ") ? (Action<string>)cmd.Log.Error : cmd.Log.Warning)(d);
            if (!result.success)
            {
                cmd.Log.Error($"{result.errorCode}: {result.error}");
                return ExitCode.Failed;
            }
            cmd.Result.Write(result.value);
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.edits

        [CliGroupCommand("cliext.edits", "Start an edit batch: the Editor stops importing changed files and reloading assemblies, so several files written one after another compile once, at cliext.edits end. " +
                                         "A batch that is never ended ends itself (and refreshes) after --expire-after seconds.",
            Tags = new[] { "editor", "cliext", "scripts/compile" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode Begin(
            CommandContext cmd,
            [CliArg("expire-after", "Seconds after which the batch ends by itself if cliext.edits end never comes")] double expireAfter = 300)
        {
            bool extended = EditBatch.IsActive;
            EditBatch.Begin(expireAfter);
            cmd.Result.Write(new { active = true, extended, startedUtc = EditBatch.StartedUtc?.ToString("o"), expiresUtc = EditBatch.DeadlineUtc?.ToString("o") });
            return ExitCode.Success;
        }

        [CliGroupCommand("cliext.edits", "End the edit batch, then refresh, compile once and return the errors and warnings, as cliext.compile run does. " +
                                         "A compile that succeeds reloads the domain, which usually drops the reply: then wait with Tools~/cliext-wait.ps1.",
            Tags = new[] { "editor", "cliext", "scripts/compile" }, Effect = CommandEffect.Idempotent)]
        internal static Task<ExitCode> End(
            CommandContext cmd,
            [CliArg("timeout", "Seconds to wait for the compile")] double timeoutInSeconds = 300,
            [CliArg("warnings", "Include warnings")] bool warnings = true,
            [CliArg("async", "Answer at once and compile after the reply, as cliext.compile run --async does")] bool runAsync = false)
        {
            if (!EditBatch.End()) cmd.Log.Warning("No edit batch was open; compiling anyway.");
            return CompileAndReport(cmd, timeoutInSeconds, warnings, runAsync);
        }

        [CliGroupCommand("cliext.edits", "Whether an edit batch is open, and when it ends by itself.",
            Tags = new[] { "editor", "cliext", "scripts/compile" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Status(CommandContext cmd)
        {
            cmd.Result.Write(new { active = EditBatch.IsActive, startedUtc = EditBatch.StartedUtc?.ToString("o"), expiresUtc = EditBatch.DeadlineUtc?.ToString("o") });
            return ExitCode.Success;
        }

        private static async Task<ExitCode> CompileAndReport(CommandContext cmd, double timeoutInSeconds, bool warnings, bool runAsync)
        {
            if (EditBatch.IsActive)
            {
                cmd.Log.Error("An edit batch is open, so nothing compiles. End it with cliext.edits end, which also compiles.");
                return ExitCode.Failed;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                cmd.Log.Error("The Editor is in Play mode, where it doesn't compile scripts. Stop Play mode first (editor.playmode stop).");
                return ExitCode.Failed;
            }

            var clock = Stopwatch.StartNew();
            double budget = double.IsPositiveInfinity(timeoutInSeconds) ? double.MaxValue : Math.Max(0, timeoutInSeconds - 1);
            string startedBefore = CompileDiagnostics.LastCompile.started;

            if (runAsync)
            {
                // Answer first, then refresh: the compile and the domain reload that follows a successful one happen
                // after the reply has gone, so this exits 0. Wait with Tools~/cliext-wait.ps1, then read
                // cliext.compile diagnostics. (Holding the reload with LockReloadAssemblies to answer after the compile
                // was tried: in Unity 6.5 the lock defers the compile itself.)
                var started = Stopwatch.StartNew();
                EditorDeferred.Run(() => AssetDatabase.Refresh(), () => started.Elapsed.TotalSeconds >= RefreshAfterReplySeconds,
                    timeoutSeconds: 30, onTimeout: () => AssetDatabase.Refresh(), label: "cliext.compile async refresh");
                cmd.Result.Write(new
                {
                    status = "started",
                    next = "Wait with Tools~/cliext-wait.ps1 (it rides out the domain reload), then read cliext.compile diagnostics."
                });
                return ExitCode.Success;
            }

            // A compile that succeeds reloads the domain, which drops this request before it can answer. (Resuming
            // inline from compilationFinished was tried: the continuation did not land on the main thread.) Errors stop
            // the reload, so failures, the case that needs the details, always get their reply. Use --async true for
            // an exit code that doesn't depend on that.
            AssetDatabase.Refresh();
            bool compiled = CompileDiagnostics.LastCompile.started != startedBefore;
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                cmd.CancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds >= budget)
                {
                    cmd.Log.Error($"The compile was still running after {clock.Elapsed.TotalSeconds:0} s. Read the result later with cliext.compile diagnostics.");
                    return ExitCode.TimedOut;
                }
                compiled = true;
                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Yield();
            }
            return WriteCompileResult(cmd, clock, warnings, compiled);
        }

        /// <summary>Seconds between answering an --async compile and starting it, so the reply leaves first.</summary>
        private const double RefreshAfterReplySeconds = 0.5;

        private static ExitCode WriteCompileResult(CommandContext cmd, Stopwatch clock, bool warnings, bool compiled)
        {
            var messages = CompileDiagnostics.Current(warnings);
            var errors = messages.Where(m => m.severity == "error").ToArray();
            cmd.Result.Write(new
            {
                status = errors.Length > 0 ? "failed" : compiled ? "compiled" : "up_to_date",
                compiledMs = clock.ElapsedMilliseconds,
                errors,
                warnings = messages.Where(m => m.severity == "warning").ToArray()
            });
            if (errors.Length > 0)
            {
                cmd.Log.Error($"{errors.Length} compile error(s). First: {errors[0].file}({errors[0].line},{errors[0].column}): {errors[0].code}: {errors[0].message}");
                return ExitCode.Failed;
            }
            return ExitCode.Success;
        }

        [CliGroupCommand(CompileGroup, "The standing compile errors and warnings: each assembly's messages from its last compile, with file, line, column and code. Errors first. " +
                                       "Kept in Library/CLIExtensions/compile.json, so it answers after a domain reload and when nothing recompiled.",
            Tags = new[] { "editor", "cliext", "scripts/compile" }, MainThreadRequired = true, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Diagnostics(
            CommandContext cmd,
            [CliArg("warnings", "Include warnings")] bool warnings = true)
        {
            foreach (var message in CompileDiagnostics.Current(warnings))
                cmd.Result.WriteRecord(message);
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // cliext.playmode

        [CliGroupCommand(PlayModeGroup, "Turn fast Play mode on or off: Enter Play Mode Options with domain and scene reload off, which makes entering Play mode take a fraction of the time. " +
                                        "With it on, static fields keep their values between Play sessions: run cliext.playmode check_statics first.",
            Tags = new[] { "editor", "cliext", "editor/playmode" }, Effect = CommandEffect.Idempotent)]
        internal static ExitCode SetFast(
            CommandContext cmd,
            [CliArg("enabled", "true for fast Play mode, false to put back the settings from before it was turned on (Unity's default when there are none)")] bool enabled = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                cmd.Log.Error("Stop Play mode first: the setting applies on the next entry.");
                return ExitCode.Failed;
            }
            bool restored = FastPlayMode.SetEnabled(enabled);
            var unreset = enabled ? StaticStateScanner.Scan().Where(f => !f.hasResetHook).ToList() : null;
            cmd.Result.Write(new
            {
                fastPlayMode = FastPlayMode.IsEnabled,
                enterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                options = EditorSettings.enterPlayModeOptions.ToString(),
                // Turning it off: true when the project's settings from before set_fast were put back, false for Unity's default.
                restoredPrevious = enabled ? (bool?)null : restored,
                staticsWithoutReset = unreset?.Count,
                firstStaticsWithoutReset = unreset?.Take(10).Select(f => $"{f.type}.{f.member}").ToArray()
            });
            if (unreset != null && unreset.Count > 0)
                cmd.Log.Warning($"{unreset.Count} static member(s) in the project's runtime code have no [RuntimeInitializeOnLoadMethod] reset and will keep their values between Play sessions. See cliext.playmode check_statics.");
            return ExitCode.Success;
        }

        [CliGroupCommand(PlayModeGroup, "Run a C# snippet (--file, with {{key}} placeholders filled from --vars) every --interval seconds until it returns \"ready\", " +
                                        "for waiting on a game state in Play mode. The snippet may also push the game forward each time (start the run, skip a wave) and return what it is waiting for otherwise. " +
                                        "Pass the CLI's --timeout a little above this command's --timeout.",
            Tags = new[] { "editor", "cliext", "editor/playmode", "scripts/eval" }, MainThreadRequired = true, Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> WaitUntil(
            CommandContext cmd,
            [CliArg("file", "The .cs snippet (relative to the project root unless absolute)", Required = true)] string file,
            [CliArg("vars", "Placeholder values: key=value;key=value")] string vars = "",
            [CliArg("timeout", "Seconds before giving up")] double timeoutInSeconds = 60,
            [CliArg("interval", "Seconds between runs")] double interval = 1)
        {
            string code;
            try { code = TemplatedEval.Load(file, TemplatedEval.ParseVars(vars)); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.InvalidUsage; }

            var clock = Stopwatch.StartNew();
            int runs = 0;
            string last = null;
            while (true)
            {
                cmd.CancellationToken.ThrowIfCancellationRequested();
                var result = await TemplatedEval.Evaluate(code);
                runs++;
                if (!result.success)
                {
                    foreach (var d in result.diagnostics) cmd.Log.Error(d);
                    cmd.Result.Write(new { ready = false, runs, waitedMs = clock.ElapsedMilliseconds, last, result.errorCode, result.error });
                    cmd.Log.Error($"The snippet failed: {result.errorCode}: {result.error}");
                    return ExitCode.Failed;
                }
                last = TemplatedEval.AsText(result.value);
                if (string.Equals(last.Trim(), "ready", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.Result.Write(new { ready = true, runs, waitedMs = clock.ElapsedMilliseconds, last });
                    return ExitCode.Success;
                }
                if (clock.Elapsed.TotalSeconds + interval >= timeoutInSeconds)
                {
                    cmd.Result.Write(new { ready = false, runs, waitedMs = clock.ElapsedMilliseconds, last });
                    cmd.Log.Error($"Not ready after {clock.Elapsed.TotalSeconds:0} s; the snippet last returned '{last}'.");
                    return ExitCode.TimedOut;
                }
                var pause = Stopwatch.StartNew();
                while (pause.Elapsed.TotalSeconds < interval)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    await Task.Yield();
                }
            }
        }

        [CliGroupCommand(PlayModeGroup, "List the static fields, auto-properties and static events in the project's runtime code that keep their values between Play sessions when domain reload is off, " +
                                        "and whether their type has a [RuntimeInitializeOnLoadMethod] reset. Those without one come first.",
            Tags = new[] { "editor", "cliext", "editor/playmode", "scripts" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode CheckStatics(
            CommandContext cmd,
            [CliArg("include-reset", "Also list members whose type has a reset hook")] bool includeReset = false)
        {
            foreach (var finding in StaticStateScanner.Scan().Where(f => includeReset || !f.hasResetHook))
                cmd.Result.WriteRecord(finding);
            return ExitCode.Success;
        }
    }
}

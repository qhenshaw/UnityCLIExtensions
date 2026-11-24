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
                                       "Fails (exit 6) when there are errors, with them in the result. When the compile succeeds, the domain reload that follows may drop the reply: " +
                                       "then read the result with cliext.compile diagnostics.",
            Tags = new[] { "editor", "cliext", "scripts/compile" }, Effect = CommandEffect.Idempotent)]
        internal static async Task<ExitCode> Run(
            CommandContext cmd,
            [CliArg("timeout", "Seconds to wait for the compile")] double timeoutInSeconds = 300,
            [CliArg("warnings", "Include warnings")] bool warnings = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                cmd.Log.Error("The Editor is in Play mode, where it doesn't compile scripts. Stop Play mode first (editor.playmode stop).");
                return ExitCode.Failed;
            }

            var clock = Stopwatch.StartNew();
            double budget = double.IsPositiveInfinity(timeoutInSeconds) ? double.MaxValue : Math.Max(0, timeoutInSeconds - 1);
            string startedBefore = CompileDiagnostics.LastCompile.started;

            // A compile that succeeds reloads the domain, which drops this request before it can answer. (Resuming
            // inline from compilationFinished was tried: the continuation did not land on the main thread.) Errors stop
            // the reload, so failures, the case that needs the details, always get their reply.
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
            [CliArg("enabled", "true for fast Play mode, false for Unity's default (reload the domain and scene)")] bool enabled = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                cmd.Log.Error("Stop Play mode first: the setting applies on the next entry.");
                return ExitCode.Failed;
            }
            FastPlayMode.SetEnabled(enabled);
            var unreset = enabled ? StaticStateScanner.Scan().Where(f => !f.hasResetHook).ToList() : null;
            cmd.Result.Write(new
            {
                fastPlayMode = FastPlayMode.IsEnabled,
                options = EditorSettings.enterPlayModeOptions.ToString(),
                staticsWithoutReset = unreset?.Count,
                firstStaticsWithoutReset = unreset?.Take(10).Select(f => $"{f.type}.{f.member}").ToArray()
            });
            if (unreset != null && unreset.Count > 0)
                cmd.Log.Warning($"{unreset.Count} static member(s) in the project's runtime code have no [RuntimeInitializeOnLoadMethod] reset and will keep their values between Play sessions. See cliext.playmode check_statics.");
            return ExitCode.Success;
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

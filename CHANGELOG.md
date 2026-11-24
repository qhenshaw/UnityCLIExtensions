# Changelog
All notable changes to Unity CLI Extensions. Versions are git tags: install one with `https://github.com/qhenshaw/UnityCLIExtensions.git#<version>`.

## 0.3.0
- Added `cliext.wait idle`: waits until the Editor isn't compiling, importing or changing Play mode, optionally after a refresh, and returns the standing compile errors. `Tools~/cliext-wait.ps1` waits from outside the Editor, across domain reloads, and returns at once when a dialog blocks the Editor.
- Added `cliext.compile run` and `cliext.compile diagnostics`: compile errors and warnings with file, line, column, code and assembly, kept per assembly in `Library/CLIExtensions/compile.json` so they survive domain reloads and stay readable when nothing recompiled. Fills what Pipeline's `script recompile_status` leaves out.
- Added fast Play mode: `cliext.playmode set_fast` (Enter Play Mode Options without domain and scene reload) and `cliext.playmode check_statics`, which lists static fields, auto-properties and events in the project's runtime code that have no `[RuntimeInitializeOnLoadMethod]` reset. The `enter-play-mode-options` setup check reports the same count.
- The heartbeat now carries `compileFailed`.
- Watchdog: a native window with no buttons (Unity's "Reloading Domain" and other progress windows) counts as busy, not as blocking; the report's `state` is `busy` while only such windows are open.

## 0.2.0
- Added `EditorDeferred`: a replacement for `EditorApplication.delayCall`, whose callbacks can wait minutes in an unfocused Editor driven from the command line. `Run` (with an optional condition, timeout, timeout callback and `Cancel`) and `RunWhenIdle` run the work from `EditorApplication.update`, nudge the Editor with `QueuePlayerLoopUpdate`, and log a warning when the timeout passes.
- The watchdog auto-start and the scene reload after an external change now use `EditorDeferred`; the reload waits until the import has finished.

## 0.1.0
- First version: keeps an Editor driven by `unity command` free of blocking dialogs.
- Agent mode, on with `-automated`, `-cliext-agent`, `CLIEXT_AGENT=1`, batch mode or the per-project setting: saves scenes before Play, keeps Play mode running unfocused, pre-answers the Input System restart prompt, reloads scenes changed on disk under `-automated`, and starts the watchdog.
- `AgentDialog` and `AgentProgress`: non-blocking replacements for `EditorUtility.DisplayDialog`, `DisplayDialogComplex` and the progress bars, with the answers logged to `Library/CLIExtensions/dialogs.jsonl`.
- Setup checks (`launch-flags`, `tmp-essentials`, `input-handling`, `hdrp-wizard`, `interaction-mode`, optional `enter-play-mode-options`) in Project Settings > CLI Extensions and `cliext.setup check` / `apply`.
- Watchdog (`Tools~/Watchdog/cliext-watchdog.ps1`, Windows): finds the Editor's modal dialogs with Win32, reports them in `Library/CLIExtensions/watchdog-status.json`, presses the safe default for known dialogs, escalates the rest, and notices crashes and hangs.
- Commands: `cliext status`, `cliext set_agent_mode`, `cliext.setup check|apply`, `cliext.dialogs log|scan|clear_log`, `cliext.watchdog start|stop|report`, `cliext.package import`, `cliext.scene save_open`.
- A catalog of the dialogs Unity 6 is known to block on, in `Documentation~/blocking-dialogs.md`.

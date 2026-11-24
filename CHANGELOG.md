# Changelog
All notable changes to Unity CLI Extensions. Versions are git tags: install one with `https://github.com/qhenshaw/UnityCLIExtensions.git#<version>`.

## 0.1.0
- First version: keeps an Editor driven by `unity command` free of blocking dialogs.
- Agent mode, on with `-automated`, `-cliext-agent`, `CLIEXT_AGENT=1`, batch mode or the per-project setting: saves scenes before Play, keeps Play mode running unfocused, pre-answers the Input System restart prompt, reloads scenes changed on disk under `-automated`, and starts the watchdog.
- `AgentDialog` and `AgentProgress`: non-blocking replacements for `EditorUtility.DisplayDialog`, `DisplayDialogComplex` and the progress bars, with the answers logged to `Library/CLIExtensions/dialogs.jsonl`.
- Setup checks (`launch-flags`, `tmp-essentials`, `input-handling`, `hdrp-wizard`, `interaction-mode`, optional `enter-play-mode-options`) in Project Settings > CLI Extensions and `cliext.setup check` / `apply`.
- Watchdog (`Tools~/Watchdog/cliext-watchdog.ps1`, Windows): finds the Editor's modal dialogs with Win32, reports them in `Library/CLIExtensions/watchdog-status.json`, presses the safe default for known dialogs, escalates the rest, and notices crashes and hangs.
- Commands: `cliext status`, `cliext set_agent_mode`, `cliext.setup check|apply`, `cliext.dialogs log|scan|clear_log`, `cliext.watchdog start|stop|report`, `cliext.package import`, `cliext.scene save_open`.
- A catalog of the dialogs Unity 6 is known to block on, in `Documentation~/blocking-dialogs.md`.

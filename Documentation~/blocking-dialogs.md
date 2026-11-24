# Blocking dialogs in Unity 6

A modal dialog runs a nested message loop on the Editor's main thread. While it is open, no main-thread `unity command`
runs: the Pipeline package queues commands one at a time, so the command that opened the dialog waits, and every
command behind it waits too. Pipeline 1.0 reports dialogs (`blocked_by_dialog`) only on Unity 6.7 and later, where
`EditorDialogEvents` exists. On 6.0 to 6.6 a blocked command simply times out.

This catalog lists the dialogs an agent-driven Unity 6 Editor is known to stop on, what prevents each, and what the
watchdog does when one appears anyway. "Verified" rows were reproduced on 6000.5.8f1 with Pipeline 1.0.0 while
building this package; the others come from Unity's documentation, package sources or experience and are matched by
title, so check the journal (`unity command cliext.dialogs log`) the first time one comes up.

## What `-automated` does

Starting the Editor with `-automated` (`unity open <project> --args "-automated"`) is the strongest single prevention.
Verified on 6000.5.8f1:

| Call | With `-automated` |
|---|---|
| `EditorUtility.DisplayDialog` | returns `false` (Cancel) at once, no window |
| `EditorUtility.DisplayDialogComplex` | returns `1` (Cancel) at once |
| `EditorUtility.SaveFilePanel` (and the other pickers) | returns `""` at once |
| "The open scene(s) have been modified externally" | no dialog; Unity silently keeps the stale scene (Ignore) |
| `InternalEditorUtility.isHumanControllingUs` | `false`, so packages that check it (the HDRP Wizard, for one) skip their popups |

Two catches. Nothing is logged, so the agent can't tell what was cancelled for it. And Cancel is not always the safe
answer: Ignore on an externally modified scene means the next save overwrites the file on disk. Agent mode covers
both: `AgentDialog` calls return the answer the call site chose and log it, and `ExternalSceneReloader` reloads open
scenes that changed on disk (unless they have unsaved changes, which it logs instead).

Dialogs that come up before scripts run (Safe Mode, package resolution errors, licensing, version mismatch) and OS
windows that are not `DisplayDialog` are outside what `-automated` changes; the watchdog handles those.

## Catalog

| Dialog (title) | When | Prevent it | Watchdog default |
|---|---|---|---|
| **Enter Safe Mode?** (Enter Safe Mode / Ignore / Quit) | Startup with compile errors. Before any package code runs. **Verified.** | Fix compile errors before closing the Editor. | `safe-mode`: **Ignore**. The Editor opens with the errors, so the agent can read and fix them. Safe Mode would not load package editor code, the Pipeline server included. |
| **Input System native platform backend not enabled** (Enable & Restart / Don't Enable) | Every start, when the Input System package is installed and Active Input Handling is Input Manager (Old). **Verified.** | Setup check `input-handling` (sets Both; restart). Agent mode pre-answers its "don't ask again this session" key with Don't Enable. | `input-system-backends`: **Don't Enable** (Enable & Restart would restart the Editor under the agent). |
| **The open scene(s) have been modified externally** (Reload / Ignore) | An open scene's file changed on disk: checkout, pull, an agent editing the .unity. Shown on the next refresh. **Verified.** | With `-automated`: `ExternalSceneReloader` reloads it. | `scene-modified-externally`: **Reload** (the file on disk is what git or the agent wrote). |
| **Scene(s) Have Been Modified** (Save / Don't Save / Cancel) | Opening another scene, building, quitting, with unsaved changes. Pipeline's own `scene` commands refuse instead of prompting. | Agent mode saves scenes with a file before Play; `unity command cliext.scene save_open` before switching. | `scene-save-prompt`: **escalate** by default; Project Settings > CLI Extensions > Scene Save Prompt can make it Save or Don't Save. |
| **Script Updating Consent** (API Updater) | Startup or import, when scripts use obsolete APIs. | Launch flag `-accept-apiupdate`. | `api-updater`: escalate (it rewrites scripts). |
| **Opening Project in Non-Matching Editor Installation** / project upgrade | Opening a project with a different Editor version. Before scripts run. | `unity open --accept-upgrades` (`-skipUpgradeDialogs`), or open with the project's own version. | `editor-version`: escalate. |
| **Unity Package Manager Error** (Retry / Continue / Quit) | Startup, when a package can't be resolved (bad git URL, offline, auth). Before scripts run. | Keep `Packages/manifest.json` resolvable; check `Logs/upm.log`. | `package-manager-error`: escalate. |
| Licensing / sign-in / Hub windows | No valid licence, or the Hub not running. | `unity license`, Hub running and signed in. | `licensing`: escalate. |
| **Moving file failed** / **Deleting file failed** (Try Again / Cancel / Force Quit) | A file is locked by an IDE, antivirus or another process during import or a move. | Close the locking program. | `file-operation-failed`: escalate. |
| OS file and folder pickers (**Open**, **Save As**, **Select Folder**) | `EditorUtility.OpenFilePanel`, `SaveFilePanel`, `OpenFolderPanel` from project or package code. Pipeline does not detect these even on 6.7. | `-automated` (they return ""), or take the path as a parameter in agent mode. `cliext.dialogs scan` finds the calls. | `file-picker`: **close** (= Cancel). |
| `EditorWindow.ShowModal` windows (for example a package's import or setup window) | Opened by code. Drawn by Unity, so they have no Win32 buttons. | `cliext.dialogs scan`; use `ShowUtility` or skip in agent mode. | Reported as `unity-modal`, escalated. `-Close` cancels it. |
| **TMP Importer** (Import TMP Essentials) | First TextMeshPro component in a project without TMP Essential Resources. A window, not modal, but it covers the Scene view. | Setup check `tmp-essentials` (imports them without the Import dialog). | Not a modal; left alone. |
| **HDRP Wizard** | Every start in an HDRP project with "Show on start" on. Not modal. Skipped with `-automated`. | Setup check `hdrp-wizard`. | Not a modal; left alone. |
| Project code's own `EditorUtility.DisplayDialog` / `DisplayDialogComplex` | Any time. **Verified** (blocks without `-automated`). | `AgentDialog.Display` / `DisplayComplex` with a headless answer; `cliext.dialogs scan` lists the calls. | No rule: escalate. |
| `EditorUtility.DisplayProgressBar` / `DisplayCancelableProgressBar` | Long editor operations. The bar holds the main thread and a crashed caller can leave it up. | `AgentProgress`, or the `Progress` API. | `busy`: wait. |
| **Hold on...** / **Opening project...** / **Compiling...** busy windows | The main thread is busy (import, compile, domain reload). **Verified** ("Opening project..."). | Nothing to prevent; they go away. | `busy`: wait. |
| Editor not responding (no dialog) | An infinite loop or a deadlock in editor code. | n/a | Reported as `hung`. With Kill When Hung set, the watchdog kills and reopens the Editor (unsaved work is lost). |
| Crash (process gone, no quit) | Native crash. Unity's Bug Reporter may open as a separate process. | n/a | Logged as `crashed`; with Reopen After Crash on, reopens the project (at most 3 times an hour). |

## Notes found along the way

- `EditorApplication.delayCall` callbacks can sit in the queue for minutes in an unfocused Editor driven from the
  command line, after a domain reload, while `EditorApplication.update` keeps ticking (seen on 6000.5.8f1 with
  Interaction Mode set to No Throttling: that setting speeds an unfocused Editor up, it doesn't guarantee delayCall).
  Code that must run soon after a reload in an agent-driven Editor should run from `EditorApplication.update`;
  `EditorDeferred.Run` does that with a timeout, and the package uses it for all of its own deferred work.
- Button text on Unity's dialogs carries Win32 mnemonics: "Enable & Restart" reads as `Enable & Restart` with a single
  `&`, which Windows draws as an underline. The watchdog strips them, so `-Click "Enable Restart"` and
  `-Click "Enable & Restart"` both work.
- The dialog's message is in an `Edit` control; reading it from another process needs `WM_GETTEXT`, not
  `GetWindowText`.
- Before the Editor's main window exists (Safe Mode, startup errors), the window title doesn't name the project. The
  watchdog matches the Editor by its `-projectPath` argument instead.

---
name: unity-cliext-addressables
description: Set up and change Addressables from the command line with Unity CLI Extensions' cliext.addressables commands (status, init, entries, edit with ops files, analyze, build) instead of the Addressables Groups and Analyze windows. Use before creating Addressables settings, adding groups, marking assets addressable, setting addresses or labels, running the Analyze rules or building Addressables content, and when reporting what a content build produced or changed. Load the unity-cliext skill too for the general Editor loop.
---

# Addressables with cliext.addressables

Groups, entries, addresses and labels change through one JSON ops file applied with one `edit` call, saved once. An
op that fails saves nothing: the result says `saved: false`, `failedAt` and `error`, and the settings, groups, entries
and labels are left as they were, on disk and in memory. Fix that op and rerun the whole file.

## The loop

1. `unity command cliext.addressables status --format json`. `initialized: false` means run `cliext.addressables init`
   (it creates `Assets/AddressableAssetsData`; exit 6 when settings exist already). Otherwise read what is there first:
   the groups (name, entry count, packing, compression, `buildPath`, `loadPath`), the labels, and
   `cliext.addressables entries --group <g>` (or `--label`, `--filter`). An op that would change nothing fails the
   whole file: `add_group` for a group that exists, `add_entry` for an asset that is already an entry (in any group;
   use `move_entry`, `set_address` or `set_labels` for those).
2. List the assets the request names (`asset find --search-in <folder>` with its type filter; read its help), and
   generate one ops file: an `add_group` (if the group is new), then one `add_entry` per asset with `group` and
   `labels`. `ops/make-entry-ops.ps1` writes it from a folder and a list of extensions.
   ```json
   [{"op":"add_group","name":"<Group>"},
    {"op":"add_entry","path":"Assets/<folder>/<file>","group":"<Group>","labels":"<label>"}]
   ```
   Paths use forward slashes and start at `Assets/` or `Packages/`. A label the project lacks is added by the entry op.
   Leave `address` out to keep Addressables' default (the asset path).
3. `unity command cliext.addressables edit --ops-file <ops.json> --format json`. Read `applied` against the op count.
4. Check: `cliext.addressables entries --group <Group> --label <label>`, and count the records against your asset list.
5. Save or discard scene changes first (`cliext.scene save_open --dry-run true` shows them): `analyze` and `build`
   exit 6 on an unsaved scene rather than stop at Unity's save dialog.
6. `unity command cliext.addressables analyze --timeout 600 --format json`. One record per rule with `issues` and
   `results` (`severity`, `name`). `issues` counts only real issues: a "No issues found" result with severity `None`
   counts 0. Exit 6 means
   an `Error` result or a rule that failed to run (its record has `error`); `Warning` results exit 0 and still belong
   in the report. Analyze changes nothing: the render pipeline assets its content build rewrites are put back.
7. `unity command cliext.addressables build --timeout 900 --format json` (size `--timeout` to the content; it is the
   CLI's own option). The reply: `success`, `error`, `seconds`, `output`, `bundles`, `target`.
8. Diff what the build rewrote (`git status`, `git diff --stat`, or the files' timestamps against the build's start).
   The build help says that, like a player build, it runs the render pipeline's build step, which can update the
   pipeline assets' shader prefiltering (URP). Cite that when you report such a change, and say whether you kept it.

## Where the build goes

- `output` in the build reply is the file the build wrote its runtime settings to; the bundles go to each group's
  `buildPath` from `status`.
  With the default local profile that is under `Library/com.unity.addressables/aa/<platform>`, not `ServerData/`:
  list the folder you report rather than naming one from memory.
- The output persists in Library across builds and sessions, so files there may be from an earlier build: compare
  their timestamps with this build's start, or build with `--clean true` (clears the previous build first).
- `bundles` counts the bundles built; `bundleMode` decides how many (PackTogether: one per group; PackSeparately: one
  per entry).

## Ops

| Op | Fields |
|---|---|
| `add_group` | `name`, optional `set` (`bundleMode=PackTogether\|PackSeparately\|PackTogetherByLabel;compression=Uncompressed\|LZ4\|LZMA;includeInBuild=true`) |
| `set_group`, `rename_group`, `remove_group` | `group`, plus `set` or `name`; the default group can't be removed |
| `add_entry` | `path` (or GUID; a folder makes a folder entry), optional `group` (default group), `address`, `labels` (`;`-separated) |
| `move_entry`, `set_address`, `remove_entry` | `path`, plus `group` or `address` |
| `set_labels` | `path`, `labels`: the full set, replacing the old one |
| `add_label`, `remove_label` | `name`; `remove_label` also takes it off every entry |

## Gotchas

- Windows PowerShell 5.1: always `--ops-file`. `ConvertTo-Json` of a one-element array writes a bare object: use
  `ConvertTo-Json -InputObject $ops -Depth 3`.
- `init` registers the settings in `ProjectSettings/EditorBuildSettings.asset`; both are new files to report.
- `build` exits 6 in Play mode, with an unsaved scene, and when the build fails (`error` says why).
- To undo entries, `remove_entry` per asset (or `remove_group` for a group you made) in one ops file; to undo `init`,
  delete `Assets/AddressableAssetsData` and revert `EditorBuildSettings.asset` only when the user asked for that.

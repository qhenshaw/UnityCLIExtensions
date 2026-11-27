---
name: unity-cliext-uitk
description: Edit UXML and USS files and drive the live UI Toolkit UI in Play mode from the command line with Unity CLI Extensions' cliext.uitk commands (inspect, edit with ops files, tree, query, click, set_value) instead of the UI Builder. Use before changing any .uxml or .uss file through commands, and whenever a task asks to press a button, set a field or slider, or read a label in a running UIDocument and confirm the result (HUDs, menus, settings screens). Load the unity-cliext skill too for the general Editor loop, and unity:ui-uitk for how to lay the UI out.
---

# UI Toolkit with cliext.uitk

`inspect` and `edit` work on the files; `tree`, `query`, `click` and `set_value` work on the live UIDocuments in the
open scenes. Layout and styling choices (flex, which elements, USS properties) are `unity:ui-uitk`'s job; this skill is
how to apply them and test them through commands. Read each command's `--help` before its first use.

## Editing files

1. The file must exist: write a bare `<ui:UXML xmlns:ui="UnityEngine.UIElements"></ui:UXML>` or a `.uss` holding
   only a comment (`/* Menu */`) first, e.g. with `asset write_text --path <file> --contents <text>` (it refuses empty
   `--contents`). Make and fill the `.uss` before the UXML op that links it: a `Style` whose `src` doesn't exist
   yet is an importer error and the UXML edit saves nothing. A bare `src=Menu.uss` resolves next to the UXML.
2. Copy a template from `ops/` (below) to a scratch file, then
   `unity command cliext.uitk edit --path Assets/<...>/Menu.uxml --ops-file <ops.json> --format json`. On Windows
   PowerShell 5.1 always use `--ops-file`: inline JSON loses its quotes. A relative `--ops-file` is read from the
   project root, not your shell's folder: pass an absolute path.
3. Read the result: `saved`, `applied`, `results`, and `import` (importer messages the file already had).
4. `unity command cliext.uitk inspect --path <file> --format json`: a UXML's `tree`, `styles` and `templates`, or a
   USS file's `rules`. Use `styles` to confirm the UXML references its style sheet.

The batch is all or nothing. An op that fails, an op that would change nothing ("would change nothing: the elements
are already like that"), or an importer error or *new* importer warning (an unknown USS property, a bad value) saves
nothing: exit 6, `saved: false`, `failedAt` and `error`; on an importer rejection the file is put back and reimported.
Fix that op and rerun the whole file. After a good run don't rerun it: `add` adds again; follow up with small files.
Errors name what is missing with "Did you mean" (`no element named 'strat'. Did you mean: #start?`, unknown element
types, unknown ops, `No rule '.x'` with the rules there are). Line endings are kept (a CRLF file stays CRLF).

| Op | File | Fields |
|---|---|---|
| `add` | UXML | `selector` = parent (none: top level), `type` (`Button`, `ui:Toggle`, a custom element), `name`, `class` (space-separated), `text`, `attributes` `"k=v;k=v"`, `index` (-1 last) |
| `remove`, `move` | UXML | `selector`; `move` takes `to` (new parent) and `index`. Every match is removed or moved |
| `set_attributes` | UXML | `selector`, `attributes` (an empty value removes one), `text`, `name` |
| `add_class`, `remove_class` | UXML | `selector`, `class` |
| `set_rule` | USS | `selector` exactly as written in the file, `properties` `"color: #fff; width: 120px"` (merged; `replace: true` replaces; a missing rule is appended) |
| `remove_rule`, `remove_property` | USS | `selector`; `remove_property` takes `properties` `"color; width"` |

Templates: `ops/uxml-edit.json` (a container with a label, a button and a slider, a style sheet link, classes),
`ops/uss-edit.json` (rules for them). Replace the names and the style sheet path before applying, and apply the USS
one first.

## Driving the live UI (Play mode)

`click` and `set_value` refuse in Edit mode (exit 6, "enter Play mode first (editor.playmode start)"). The command is
`editor.playmode start`, not `play`: list `unity command editor.playmode` unfiltered if unsure. Run each step as its
own call and check its exit code before the next; never chain Play-mode commands after an unchecked start.

1. Save the scene (`cliext.scene save_open --scene <path>`), so Play mode starts from what is on disk.
2. `unity command editor.playmode start --format json`, then `cliext-wait.ps1 -Timeout 120`. An
   `EDITOR_CONNECTION_LOST` reply here is the domain reload, not a failure: wait, then go on.
3. Wait for the UI: `cliext.playmode wait_until --file .claude/skills/unity-cliext-uitk/snippets/wait-uidocument.cs --vars
   "document=<GameObject>;element=<name>" --timeout 20`. It returns `ready` once the document has a live panel and
   the element exists; still not ready at the timeout, it exits 14 with the snippet's last answer (`no #<name> yet`).
4. `cliext.uitk query --selector "#<name>" --document <GameObject> --format json`: the value before.
5. Act: `cliext.uitk click --selector "#<button>" --document <GameObject>` (check `clicked` and `matches`), or
   `cliext.uitk set_value --selector "#<field>" --value 0.5 --document <GameObject>` (returns `before` and `value`).
6. Read back with `query` (the label's `text`, the field's value) and, for game state the UI drives (a volume, a
   score), a snippet (`snippets/read-ui.cs` shows the working pattern).
7. `editor.playmode stop`, wait, then put back anything the game changed that outlives Play mode (e.g.
   `AudioListener.volume`, `Time.timeScale`, PlayerPrefs, ScriptableObject assets) and check it.
8. `unity command scene list_open --format json`: no scene `isDirty`. Report the values you read back, not the ones you set.

`set_value` takes text, numbers, `true`/`false`, enum names, `[x,y]` for vectors, or a dropdown choice. It refuses a
value out of a slider's range, not one of a dropdown's choices, a disabled field, the value the field already has, and
a Button (use `click`). On a Label it only rewrites the text (no event).

## Selectors

`Type`, `#name`, `.class`, combined (`Button.primary#start`), nested with spaces or `>` (`#menu > Button`), `*`.
Types match the C# type's name, case-insensitive (`ui:` prefixes are dropped); names and classes are case-sensitive.
No pseudo-classes (`:hover`, `:checked`: exit 2), attribute selectors or comma lists. In files, selectors never match
the `<UXML>` root (except the selector `UXML`) or `<Style>`/`<Template>` elements. No match exits 13 naming the part the
tree lacks; `cliext.uitk tree --document <GameObject> --depth 3` shows the live tree.

## Gotchas

- `query` and `tree` also work in Edit mode on documents with a live panel, before any game script ran: an Edit-mode
  read is not proof of Play-mode behaviour.
- `click` and `set_value` act on the first match only (documents in GameObject-name order); select by `#name`.
- `click` sends a navigation submit and a `ClickEvent`. Button `clicked` handlers and `ClickEvent` callbacks run;
  handlers on pointer or mouse events don't. It refuses disabled and hidden (`display: none`, `visibility: hidden`)
  elements and non-Buttons with no `ClickEvent` callback. A Label inside a Button clicks the Button.
- `--document` is the UIDocument's GameObject name. A UIDocument on an inactive GameObject isn't found ("No active
  UIDocument ... Documents: <the ones there are>"). One with Panel Settings but no Source Asset is found but holds
  only its empty root (`UIDocumentRootElement`), so every selector misses (exit 13): check `sourceAsset` with
  `component get_fields --target <go> --component UIDocument`.
- `script eval` snippets: `root.Q(...)`/`.Query(...)` fail with CS1061 (the extension methods don't resolve). Call them
  as static methods: `UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Label>(root, "score")`.
- `attributes` splits on `;`, so an inline `style` can hold only one property, and text with `;` goes in `text`. Put
  styling in USS.
- `inspect` parses the XML or reads the rules; it doesn't run Unity's importer. Only `edit` reports importer errors,
  so prefer edits over writing whole files.
- A `<Style>` can be added (`{"op":"add","type":"Style","attributes":"src=<path>","index":0}`) but not changed or
  removed with ops, since selectors don't match it.

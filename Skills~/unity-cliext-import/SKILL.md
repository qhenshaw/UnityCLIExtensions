---
name: unity-cliext-import
description: Change import settings across folders, set up default presets per folder, and import .unitypackage files from the command line with Unity CLI Extensions' cliext.import, cliext.presets and cliext.package commands instead of the Inspector, the Preset Manager or Assets > Import Package. Use before changing texture, model or audio import settings on more than one asset, before making a preset a folder's default ("new textures there should import as ..."), when auditing which assets break their presets, and before importing any .unitypackage (never AssetDatabase.ImportPackage, never unpacking it by hand). Load the unity-cliext skill too for the general Editor loop.
---

# Import settings, presets and packages with cliext

Every command here checks all of its arguments and every matching asset before it changes anything, and is all or
nothing: when applying fails on one asset, the .meta files of the ones already changed are written back and
reimported. Exit 13 is a missing folder, asset or preset (or nothing matched); 2 is a bad setting, value, type,
platform or a path outside Assets/ or Packages/; 6 is "nothing would change" (expected when re-running, not a failure).
Read each command's `--help` before its first use.

## Import settings: `cliext.import batch`

```
unity command cliext.import batch --folder <folder> --type Texture2D --set "maxTextureSize=1024;mipmapEnabled=false" --dry-run true --format json
```
- `--set` takes the importer's own public property names (TextureImporter: `textureType`, `textureCompression`,
  `spriteImportMode`, `sRGBTexture`, `alphaSource`, ...; ModelImporter: `globalScale`, `materialImportMode`, ...).
  An unknown name or enum value exits 2 with "Did you mean" and the valid names: use that list, don't guess twice.
- Subfolders are included. `--filter` matches the file name only (`*.png`, `Hero_*`), never a path.
- A mixed folder fails as a whole when one importer type lacks a setting: add `--type Texture2D` (or `Model`,
  `AudioClip`) or `--filter`. `--platform Android|iOS|WebGL|Standalone|...` writes that platform's override and turns
  `overridden` on.
- Only assets that differ are changed and reimported; each comes back as `name: old -> new`. Keep that output: it is
  the record you need to put the old values back with a second `batch`.

## Default preset for a folder (the recipe)

1. Look first: `cliext.presets list` (`defaults: 0` with a note when there are none; a record with `"defaults":[]`
   is a type whose last default was removed), and `asset find --search-in <folder>` for what is there by type and
   extension.
2. Make one asset the template: `cliext.import batch --folder <folder> --filter <one file name> --set "<the requested
   settings>" --dry-run true` (check it lists exactly one asset), then the same without `--dry-run`.
3. `cliext.presets create --from <that asset> --out <folder>/<Name>.preset --only "<the settings the request
   names>"` (`;` between them; `--exclude "<names>"` is the other way round). The names are the same ones `import
   batch --set` took (`textureType;mipmapEnabled;maxTextureSize`) or `m_` property paths; read `resolved` in the reply
   to see what each became. Without either, the preset captures the whole importer and forces the template's colour
   space, alpha source and swizzle onto every file. `--only` keeps the platform settings block (`m_PlatformSettings`:
   max size, compression, format) whole, whether or not you name those: a preset without it gives new files the
   importer defaults, so don't exclude it (`--exclude textureCompression` is refused for that reason). Unknown names
   exit 2 with the property names; a missing out folder is made; an existing .preset exits 6.
4. Check it: `cliext.presets apply --preset <p> --folder <folder> --dry-run true` lists `propertyPath: is X, preset Y`
   per asset. Only the requested settings (and platform entries) may differ. If anything else does, delete the
   preset (`asset delete`) and create it again with the right `--only` list.
5. `cliext.presets set_default --preset <p> --filter <folder>` (a folder becomes `glob:"<folder>/**"`; exit 6 if it is
   already the first default there). It saves `ProjectSettings/PresetManager.asset`; confirm the file on disk holds
   the preset's GUID (from its `.preset.meta`), since an Editor restart drops a default that isn't on disk.
6. `cliext.presets apply --preset <p> --folder <folder> --dry-run true`, then without `--dry-run` (pass the CLI a
   `--timeout` sized to the folder). Per-asset data (sprite rects, user data, bundle names) is left alone.
7. Test the default with a NEW file: copy one source file (without its .meta) into the folder under a new name,
   `cliext.wait idle --refresh true`, read its settings (`asset get_import_settings`), then `asset delete` it. Applying
   to existing assets doesn't prove that new ones get the preset.
8. `cliext.presets audit --folder <folder>`: a clean audit is one record (`folder`, `audited`, `presets`,
   `breaking: 0`, `note`): report its numbers. Exit 6 lists each asset and setting that breaks its default.

Report which assets the preset changed type on (the texture type differences in `apply --dry-run`: normal maps,
sprites, lightmaps become the requested type), and why some still import differently from others, with the check that
showed it. Example: a texture whose source has no alpha channel imports as RGB24 where others are RGBA32
(`TextureImporter.DoesSourceTextureHaveAlpha()` tells which). Never call that "normal for this pack" without a check.

Undo: `presets set_default --preset <p> --remove true --filter <folder>` (`--preset` is required with `--remove`;
save the Preset Manager as in step 5 again), then `asset delete` the .preset, and a `batch` with the
old values from the first output.

## .unitypackage files: `cliext.package import`

Always this command. `AssetDatabase.ImportPackage` (even with `interactive` false) stops at a blocking "Missing
signature" modal for an unsigned package, and unpacking the archive and copying files by hand skips its checks.

1. Note the time (`Get-Date -Format o`) and the console's last entry (`console get`; read its help) before you start.
2. `unity command cliext.package import --path "<file>.unitypackage" --dry-run true --format json`: `created`,
   `replaced` (files that would be overwritten: ask before going on), `unchanged`, and `atExistingGuid` (assets whose
   GUID is already in the project are written there, not at the package's path). Exit 2 means paths outside Assets/ or
   a missing Packages/ folder; 6 means everything is already there unchanged.
3. The same without `--dry-run`. It is imported when the call returns; a package with scripts starts a compile, so run
   `cliext-wait.ps1` and `cliext.compile diagnostics`.
4. What it added is the `created` list (and `replaced`); check it against the files on disk.
5. Errors: only console entries newer than step 1's time are this import's. Say which earlier ones you left out.
6. Missing references: `cliext.eval run --file .claude/skills/unity-cliext-import/ops/missing-refs.cs --vars "folder=<the imported folder>"
   --timeout 120` (a relative `--file` is read from the project root: give the full path). On Unity
   6.6 use `objectReferenceEntityIdValue`; `objectReferenceInstanceIDValue` is obsolete there (CS0619). It also lists
   each material's shader: report the names, and mark whether they render in this pipeline as unchecked unless you
   captured it.

## Templates (`ops/` next to this file)

| File | What it does |
|---|---|
| `missing-refs.cs` | Under `folder`: missing object references, missing scripts, sub-objects that don't load, and every material's shader. |

## Gotchas

- `presets apply` and `import batch` exit 6 when nothing would change, dry runs included: after a good apply, that is
  the expected result.
- `audit` checks only assets that have a default preset for their type: run `list` to see what it audits.
- A `.preset` made from one asset type applies only to that importer type; other assets in the folder are skipped.
- Several defaults can match one asset; the Preset Manager applies them in order and `audit` reports each.

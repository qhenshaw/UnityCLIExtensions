---
name: unity-cliext-asset-packs
description: Import only the assets a Unity project actually uses from large .unitypackage art packs (Asset Store model, prop, character and environment packs), with GUIDs preserved and dependencies followed, then convert the pack's materials to the project's own shader (URP Lit or a custom uber shader). Use when a project wants a few models, props or characters from multi-GB packs, wants to avoid importing a vendor's shaders, scripts and demo scenes, has pink or grey materials after importing a pack, or asks to "replace placeholder art with <pack>". For importing a whole .unitypackage, use cliext.package import (the unity-cliext-import skill). Load the unity-cliext skill too for the general Editor loop.
---

# Selective asset pack extraction and material conversion

Big art packs are 1 to 10 GB each and bring their own shaders, scripts, demo scenes and render-pipeline assumptions.
Instead of importing everything, read the `.unitypackage` directly, write just the chosen assets (and what they
reference) into `Assets/`, then point their materials at the project's shader. To import a whole package, use
`cliext.package import` instead (no dialogs, all or nothing; see `unity-cliext-import`).

A `.unitypackage` is a gzipped tar of `<guid>/pathname`, `<guid>/asset`, `<guid>/asset.meta`. Writing `asset` to
`pathname` with its `.meta` beside it reproduces the import exactly: the GUIDs are the same, so prefab, mesh,
material and texture references resolve, and importing the full pack later matches instead of duplicating.

The scripts and template are in this skill's folder (`.claude/skills/unity-cliext-asset-packs/`).

## The loop

1. **Find the packages.** Asset Store downloads are in
   `%APPDATA%\Unity\Asset Store-5.x\<Publisher>\<Category>\*.unitypackage` (Windows) or
   `~/Library/Unity/Asset Store-5.x/` (macOS). Ask the user if they're elsewhere.
2. **Index them** (no extraction): `python -I scripts/index_unitypackage.py <out_dir> <pkg>...` writes
   `guid  pathname  sizeKB` per entry. Grep the index for what the game needs, using the pack's own naming in its
   `Prefabs/` folder. Index every pack the user owns once and keep the indexes (one `.txt` per package, in a
   git-ignored folder such as `Library/PackIndexes/`): grepping saved indexes is instant, so a later request becomes
   one search across every pack, and often finds a better source in another pack. Re-index when a pack updates.
3. **Dry-run the extraction** to see what the patterns pull in with dependencies, and the size:
   `python -I scripts/extract_unitypackage.py <project_root> <pkg> "Prefabs/Props/<name>_0[1-3]" --dry-run`.
   Tighten the patterns until the list and the MB are what you expect. Patterns are regexes searched anywhere in
   the path, and a match that is a skipped type (scripts, shaders, scenes) isn't counted. Dependencies are followed
   by GUID only: a model whose `.meta` has no `externalObjects` (older packs, e.g. Kenney: `materialSearch` finds
   materials by name in a `Materials/` folder at import) lists no materials in the dry run. Add a pattern for that
   folder (`"FBX format/Materials/"`): without it the model can't find the pack's materials.
   Run the scripts by their full path (`python -I .claude/skills/unity-cliext-asset-packs/scripts/...`), not from inside a pack folder.
4. **Extract** (the same command without `--dry-run`). It's safe to re-run: existing files are skipped, so add
   patterns later as the game needs them. Packs write under `Assets/<Vendor>/...`; move afterwards only in the
   Editor (`asset move`), so GUIDs and references stay intact.
5. **Import:** `unity command cliext.wait idle --refresh true`, then read the console for import errors
   (`cliext.compile diagnostics` or `console get`) by timestamp against your first call.
6. **Convert materials.** Copy `templates/Editor/PackMaterialConverter.cs` into an Editor folder, set `PackRoot`, the
   target shader names and this vendor's source slot names (below), and compile. Run it with a snippet file holding
   `return AssetPackTools.Editor.PackMaterialConverter.ConvertAll();` (it returns a summary):
   `unity command cliext.eval run --file <snippet>.cs`. Re-run it after every new extraction.
7. **Look at it.** Put a few prefabs in a scene and capture them (`capture scene_view`, or `cliext-play-capture.ps1`).
   Check for grey, black or pink materials, missing alpha cut-outs on foliage and over-bright emission before
   wiring them into gameplay. Check references with the `missing-refs.cs` snippet from `unity-cliext-import`.
8. **Commit** with the project's LFS rules covering `.fbx`, `.png`, `.tga`, `.psd` and the like. Never change a GUID.

## Choosing what to extract

- Extract **prefabs**, not raw FBX, when the pack has them: prefabs pull in their meshes, materials and textures.
- Characters: also extract the avatar and animation assets they reference; animation packs are often separate.
- Skipped by default: `.cs .asmdef .asmref .dll .shader .shadergraph .shadersubgraph .hlsl .cginc .unity .lighting`.
  Vendor shaders target a specific pipeline version and drag in subgraphs and scripts; converting is cheaper.
  `--keep-code` only when the user asks for a vendor script (a character creator runtime, say).
- Shared texture atlases come along through the materials. `--no-deps` extracts only the matches (textures, audio).

## Mapping a vendor's shader properties

Open one extracted `.mat` in a text editor. Under `m_SavedProperties`:
- `m_TexEnvs` lists texture slots (`_Albedo_Map`, `_MainTex`, `_BaseColorMap`, ...): add the base, normal and
  emission names to `BaseSlots`, `NormalSlots`, `EmissionSlots`.
- `m_Floats` holds toggles and values (`_AlphaClip`, `_Alpha_Clip_Threshold`, `_Smoothness`): `AlphaClipFlags`,
  `CutoffFloats`.

The saved properties stay after the shader is missing or swapped, which is why conversion works without the vendor
shader and can be re-run. Name rules are cheap and fine: foliage (`Leaf`, `Fern`) gets alpha clip and double-sided,
crystals get emission.

## Gotchas

- **Palette-textured models turn grey.** Many low-poly packs colour meshes from tiny palette textures; mipmaps and
  bilinear filtering blend neighbouring cells at gameplay distance. Import those point-sampled, uncompressed, with
  no mipmaps (`PaletteTextureFilter` in the template, or `cliext.import batch`).
- **Materials embedded in an FBX** are read-only: skip them (the template does), or extract the model's external
  `.mat` files, which the importer's `.meta` references by GUID (the extractor follows those).
- Git Bash rewrites arguments starting with `/` (paths and regexes): set `MSYS_NO_PATHCONV=1` and pass
  Windows-style script paths, or run from PowerShell. Write Python helpers with the Write tool, not heredocs.
- **Licensing:** extracted assets stay under the vendor's licence. Don't commit them to public repos or ship them in
  plugins or templates; keep them in the private game repo only.
- Pink after conversion: the target shader name is wrong, or the shader isn't in the build's pipeline.

## Files

- `scripts/index_unitypackage.py`: every entry (GUID, path, size) of one or more packages.
- `scripts/extract_unitypackage.py`: selective, dependency-following, GUID-preserving extraction (`--dry-run`,
  `--no-deps`, `--keep-code`).
- `templates/Editor/PackMaterialConverter.cs`: re-runnable material conversion and the palette texture fix.

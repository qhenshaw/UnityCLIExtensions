---
name: unity-cliext-shadergraph
description: Build and edit Shader Graphs from the command line with Unity CLI Extensions' cliext.shadergraph commands (types, create, inspect, edit with ops files) instead of the Shader Graph window, then make and check the material. Use before creating or changing any .shadergraph (dissolve, alpha-clipped cutouts, masks, edge glow, emission, triplanar or noise-driven effects), when a block such as Alpha or Alpha Clip Threshold is refused, when a property must be a slider with a range or an HDR colour, and when a threshold effect cuts away the wrong amount of a mesh. Load the unity-cliext skill too for the general Editor loop.
---

# Shader Graph with cliext.shadergraph

Every change is a JSON ops file applied with one `edit` call. It loads the graph from the file, applies the ops in
order, then saves, reimports and compiles every pass once. The reply has `results` (the id of everything added) and
`compile` (`shaderErrors`, `shaderWarnings`, `nodeErrors`, `hasErrors`). Nodes, blocks and properties are addressed
by their object id; a slot is `<node id>:<slot>`, by slot id, display name or shader name.

## The loop

1. `unity command cliext.shadergraph types --targets true` lists targets and materials. Create with
   `cliext.shadergraph create --path Assets/<...>/Name.shadergraph --target Universal --material Lit` (it makes the
   folder).
2. Need Alpha or Alpha Clip Threshold? Put `set_target` alphaClip first in the ops file (below); the blocks are
   refused until then.
3. `cliext.shadergraph inspect --path <graph> --details false`: `targets`, the `vertex` and `fragment` blocks with
   ids, `blocksAvailable` (what `add_block` accepts now), `properties`, `compile`. `types --filter <text>` finds a
   node's key (`Math/Basic/Multiply`); never guess one.
4. Copy a template from `ops/` to a scratch file, replace the texture path and numbers, then
   `cliext.shadergraph edit --path <graph> --ops-file <ops.json> --format json`. Always `--ops-file` on Windows
   PowerShell 5.1. `edit` refuses while the graph is open in a Shader Graph window: close it (with `--force true` the
   window's copy may overwrite the edit).
5. Read the reply, set property modes (below), make the material, then verify (see "Checking it").

## Failures and rollback

- **An op fails: nothing is saved.** The reply has `saved: false`, `failedAt` (0-based), `error`; exit 6. Fix that op
  and rerun the whole file.
- **Compile or node errors: the edit is kept** and still exits 6. Don't rerun the file (`add_node` and
  `add_property` add duplicates). Fix it with a small follow-up file (`unlink`, `remove`, `set`) and read `compile`.
- `add_block` is safe to repeat: a block that exists is returned with its id, and `as` binds to it.

## Op shapes (what the baselines got wrong)

- `$alias` lives only inside one `edit` call. Across calls use the ids from that reply's `results`, or a fresh
  `inspect`. Ids from an older `inspect` go stale once nodes are removed or the file is rewritten by hand.
- Link to a block by its node id, never its name: `"to":"<block id>:0"` (a block's one input slot is id 0). In the
  same file, get the id with `{"op":"add_block","block":"Emission","as":"em"}` (it returns the existing block), then
  `"$em:0"`. `add_block` takes the display name (`Alpha Clip Threshold`), the name (`AlphaClipThreshold`) or
  `<tag>.<name>` (`SurfaceDescription.NormalTS`, to tell apart blocks that share a name).
- `link` with a property as `from` (its name, reference, id or `$alias`, no colon) drops a new property node in.
- Dynamic slots (Multiply, Add, Step ...) resolve from their links; unlinked, a Multiply input is a 4x4 matrix and
  `set` wants 16 numbers. Link the vector input first, then `inspect --path <graph> --id <node>` and read the slot's
  `type` before `set`. A linked slot refuses `set`: unlink it first.
- `set` takes `slot` (an input), `setting` (a node's own setting; `inspect --id` lists them) or neither (a property's
  default). Values: numbers, `true`/`false`, `[x,y,z,w]`, `#RRGGBB`, asset paths, enum names.
- Other ops: `add_property` (`type`, `name`, `reference`, `value`), `remove`, `unlink`, `move`, `rename`
  (`name`/`reference` of a property), `expose` (`value` true/false), `add_port`/`remove_port` (Custom Function).

## Graph Settings and property modes

Never edit the .shadergraph file by hand: two ops cover what the Graph Settings tab and a property's mode do.
- `{"op":"set_target","setting":"alphaClip","value":"true"}` turns alpha clipping on and adds Alpha Clip Threshold
  (the reply's `blocksAdded`); only then does `add_block` offer Alpha. Other settings: `surfaceType`
  (Opaque/Transparent), `allowMaterialOverride`, `castShadows`, `renderFace`, and the material's own (Lit's
  `workflowMode`). `inspect` lists them under `targets[].settings`; `target` picks one when the graph has several.
- `{"op":"set_mode","id":"<property name, reference or id>","mode":"slider","min":0,"max":1}` for a float
  (`default`, `slider` with `min`/`max`, `integer`); `"mode":"hdr"` for a colour (`default`, `hdr`). A default
  outside the range is clamped. `inspect` shows each property's `mode`.

Both fail when they would change nothing, so leave out a setting that's already on. Put `set_target` before the
`add_block` ops that need it, in the same file.

## Templates (`ops/` next to this file)

| File | What it is |
|---|---|
| `threshold-clip.json` | Dissolve-style cutout: `_Amount`, `_EdgeWidth`, `_EdgeColor`, `_MaskTex`; mask R to Alpha, Amount to Alpha Clip Threshold, a Step band times the edge colour to Emission (Lit). Turns alpha clipping on itself and makes Amount a 0-1 slider and Edge Color HDR. The band also shows at Amount 0 on the darkest texels. |
| `measure-texture.cs` | `cliext.eval run` snippet: import `sRGBTexture`, project colour space, per-channel percentiles of what the shader samples, share below a threshold. |
| `uv-layout.cs` | `cliext.eval run` snippet: per mesh under an object, UV0 bounds, `uvArea` (summed UV triangle area), object-space size (`boundsSize`, mesh units) and, for a scene object, world size (`worldSize`). |

## Check the input data first

A threshold effect is only as even as the values it compares. Before choosing a texture and UVs:
- `unity command asset get_import_settings --asset <texture>` (it is `--asset`, not `--path`): `sRGBTexture` true
  means the shader gets linearised, darker values.
- `cliext.eval run --file .claude/skills/unity-cliext-shadergraph/ops/measure-texture.cs --vars "texture=<path>;threshold=0.3" --timeout 60`:
  if most texels sit below your threshold range (p50 near 0, `belowThreshold` near 1), pick another texture or
  remap it before tuning numbers.
- `cliext.eval run --file .claude/skills/unity-cliext-shadergraph/ops/uv-layout.cs --vars "target=<object path>" --timeout 60`: a small
  `uvArea` or a UV island packed into a corner means UV0 samples a small patch; use `UV/Triplanar` or
  `Input/Geometry/Position` (Object space) scaled by `boundsSize` instead. `boundsSize` is in the mesh's own
  units and can be 100x off the `worldSize` of an imported model with a scaled child (a Kenney character's mesh
  spans about 0.016 units inside a 1.8 m renderer): pick the scale from the one the graph's space uses, and look.

## Material and checking it

- Shader name: `material list_shaders --filter <graph name>` (new graphs are under `Shader Graphs/`). Make it with
  `asset create --path <...>.mat --type UnityEngine.Material --shader "<name>"`; set values with
  `material set_properties --material <mat> --properties '{"_Amount":0.3,"_MaskTex":"<texture path>"}'` from the
  Bash tool (PowerShell 5.1 strips the quotes).
- Graph: `inspect` `compile.hasErrors` false, no `hasError` on any node.
- Shader: `material get_shader_properties --material <mat>`: each property's `type` (`Range` with its limits, not
  `Float`), HDR in a colour's `flags`, the texture. `material get_properties --material <mat>`: the values.
- Every renderer that should use it: read each one's materials, all slots, children and skinned meshes included.
- Look at it: `capture scene_view --output-path Temp/shots/<name>.png` (or `capture game_view --source camera
  --camera <name>`) at two or three amounts, and read the image. A threshold that seems to cut everything away is
  usually the data (above), not the graph.

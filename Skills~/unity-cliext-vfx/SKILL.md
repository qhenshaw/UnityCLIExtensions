---
name: unity-cliext-vfx
description: Build and edit VFX Graph effects from the command line with Unity CLI Extensions' cliext.vfx commands (create, edit with ops files, inspect, check) instead of the VFX Graph window. Use before creating or changing any .vfx graph (bursts, muzzle flashes, impacts, sparks, embers, tracers, trails, flipbook effects), when wiring a graph to C# (VFXEventAttribute, SendEvent, OnPlay), when an effect plays but shows nothing, and when capturing short effects in slow motion. Load the unity-cliext skill too for the general Editor loop.
---

# VFX Graph with cliext.vfx

Every change is a JSON ops file applied with one call; the graph is saved and compiled once and the result comes back
with the compile log, `warnings` and `eventAttributes`. Ids are stable file ids, so later edits address models by id.

## The loop

1. Plan the systems: one Event context (`OnPlay`), and per visual layer a Spawn → Initialize → Update → Output chain.
2. Start from a template in this skill's `ops/` folder (below). Copy it to a scratch file, replace the texture paths
   and numbers, and when combining templates keep a single `{"op":"add","kind":"context","type":"Event","as":"ev"}`.
3. `unity command cliext.vfx create --path Assets/<...>/VFX_Name.vfx` once, then
   `unity command cliext.vfx edit --path <vfx> --ops-file <ops.json> --format json`. On Windows PowerShell 5.1
   always use `--ops-file`: inline JSON loses its quotes.
4. Read the result: `applied`/`total`, `compile` (shader errors), `warnings` (fix every one) and `eventAttributes`
   (the exact attribute names C# must send). `unity command cliext.vfx check --path <vfx>` reruns the checks.
5. Look at it (see "Seeing it"), tune with small `set` ops, repeat.

An op that fails saves nothing: the graph is left as it was (`saved: false`, `failedAt` and `error` say which op and
why). Fix that op and rerun the whole file. Once a file has applied, don't rerun it: `add` is not idempotent and you
get duplicate systems; change the graph with `set`, `remove` or a small follow-up file.
`$alias` names live only inside one `edit` call; afterwards use the ids from `results` or `inspect`.

## Templates (`ops/` next to this file)

| File | What it builds |
|---|---|
| `burst.json` | One camera-facing additive flash per event, tinted by the event colour, random size, angle and lifetime, size and alpha curves. The base of cores, rings and impacts; for a flipbook, set `uvMode`, `flipBookSize` and a texture-index block (see the table). |
| `sparks.json` | A handful of velocity-stretched sparks flying along the event `direction` with spread and random speed, drag, shrinking. |
| `streak-from-source.json` | One streak per event that takes position, velocity, lifetime, colour, size and scale from the event: tracers and projectiles whose length and speed C# decides. |

Each template's texture is `Assets/REPLACE/WithYourTexture.png`; set it to a real texture before applying, or the
`set` op fails (and everything after it is skipped).

## Block keys and slots (VFX Graph 17.6)

`cliext.vfx types --kind block --filter <text>` finds any other key. Slot names below are what `set --slot` takes.

| Key | Slots and settings |
|---|---|
| `Spawn/Single Burst`, `Spawn/Periodic Burst` | `Count` **defaults to 0** (spawns nothing; always set it), `Delay`. Setting `spawnMode` Random turns `Count` into a range `[min,max]`. |
| `Constant Spawn Rate` | `Rate` (per second, default 10): continuous effects and trails. |
| `Set/Set Position From Source` (also Color, Size, Direction, Velocity, Lifetime, Scale) | No slots: the value comes from the event. Scale is read per channel, see "C# side". |
| `Multiply/Multiply Color` | `Color` `[r,g,b]`: HDR boost, 2 to 7 for glow with bloom. |
| `Blend/Blend Color` | `Color`, `Blend` **default 0** (does nothing until set; 0.75 pulls most of the way to the colour, e.g. a white-hot core). |
| `Multiply/Multiply Size Random Uniform`, `Set/Set Lifetime Random Uniform` | `A`, `B` (range). |
| `Set/Set Angle Random Uniform` | Setting `channels` defaults to `XYZ`: set `Z` for quads, then `A`, `B` in degrees. |
| `Attribute/Advanced Simulation/Set Angle` | Setting `channels` `Z`, slot `Angle`. 90 turns a horizontal texture to lie along the velocity. |
| `Attribute/Rendering/Set Scale`, `Multiply/Multiply Scale` | `Scale` `[x,y,z]`: non-square quads (`[0.12,1,1]` is a thin needle). |
| `Attribute/Rendering/Set Pivot` | `Pivot` `[x,y,z]` in quad units (±0.5 is an edge): `[0,0.4,0]` puts the head of a streak at the particle. |
| `Velocity From Direction & Speed/Set Velocity from Direction & Speed Change Speed` | `Speed`, along the `direction` attribute. |
| `Velocity From Direction & Speed/Set Velocity from Direction & Speed Random Direction` | `DirectionBlend` **default 1 = fully random**; 0.25 is a tight cone around `direction`. Setting `speedMode` Random gives `MinSpeed`, `MaxSpeed`. |
| `Force/Linear Drag` | `dragCoefficient`. |
| `Force/Gravity` | `Force` `[0,-9.81,0]`. |
| `Orientation/Orient Along Velocity` | Quad Y axis along the velocity: streaks, sparks, directional bursts. Output contexts face the camera without it. |
| `Multiply/Multiply Size Over Life`, `Multiply/Multiply Alpha Over Life` | `Size` / `Alpha` curves `[[t,v],...]`, e.g. `[[0,1],[1,0]]`. |
| `Set/Set Tex Index Random Uniform` | `A`, `B`: a random flipbook frame, `B` = frames minus 0.01. |
| `Attribute from Curve/Rendering/Set Tex Index Over Life` (Update) | `TexIndex` curve `[[0,0],[1,frames-0.01]]`: play a flipbook once. |

Contexts: `Event` (setting `eventName`, default `OnPlay`), `Spawn`, `Initialize Particle` (settings `capacity`,
default 128, and `boundsMode` Manual with slot `bounds` `{"center":{"x":0,"y":0,"z":0},"size":{"x":4000,"y":4000,"z":4000}}`
for world-positioned bursts), `Update Particle`, `Output Particle Unlit Quad` (settings `blendMode` Additive or Alpha,
`uvMode` Flipbook with slot `flipBookSize` `{"x":3,"y":2}`; slot `mainTexture` an asset path).

Confirm a texture's flipbook grid before using it as one; a wrong grid shows half sprites.

## C# side

- Send bursts with `visualEffect.SendEvent("OnPlay", attribute)` and a reused `VFXEventAttribute`
  (`CreateVFXEventAttribute()` once). Use exactly the names in `eventAttributes`. Variadic attributes are per channel:
  send `scaleX`, `scaleY`, `scaleZ` with `SetFloat`. `SetVector3("scale", ...)` is silently ignored.
- A VisualEffect plays only one OnPlay burst per frame; later sends in the same frame are lost. Shotgun pellets,
  several turrets firing together or one impact per pellet need a few VisualEffect instances per effect, each used at
  most once per frame.
- `aliveParticleCount` lags a few frames: in tests poll it for up to ~30 frames, never assert on the spawn frame.
- Burst positions are world positions when the VisualEffect sits at the origin with identity transform; keep the
  bounds manual and large so culling never hides a burst.

## Seeing it

A muzzle flash lasts a tenth of a second, so capture it in slow motion, triggered by the project's own command:
```
powershell -NoProfile -ExecutionPolicy Bypass -File {{cliext-tools}}/cliext-play-capture.ps1 -Project <project> `
  -Scene <scene> -TimeScale 0.005 -Run "<command that plays the effect>" -Sheet Temp/shots/fx.png a@0 b@1.5 c@3
```
Make the effect fire side-on to the camera when judging
streaks: anything flying straight away from the camera foreshortens to a dot.

## Ownership

A graph built or edited this way is a normal asset people can open in the VFX Graph window. If the project also
generates graphs from code into a generated folder, keep hand-owned graphs out of it and out of the generator's specs,
or the next generation overwrites them.

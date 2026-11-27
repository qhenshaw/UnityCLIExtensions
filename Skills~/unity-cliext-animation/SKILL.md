---
name: unity-cliext-animation
description: Build and check Animator controllers, animation clip import and Timeline cutscenes from the command line with Unity CLI Extensions' cliext.animator, cliext.anim and cliext.timeline commands (on top of Pipeline's animation.animator and animation.timeline). Use before creating or changing a .controller (states, layers, avatar masks, blend trees, transitions), a model's clips (takes, loop, root motion, animation events), an .anim, or a .playable timeline (tracks, clips, audio, markers, bindings, PlayableDirector), and when scrubbing a timeline or confirming which clip a character is posed in. Load the unity-cliext skill too for the general Editor loop.
---

# Animation with cliext.animator, cliext.anim and cliext.timeline

Pipeline **adds** things: `animation.animator create_controller / add_parameter / add_layer / add_state /
add_transition` and `animation.timeline create / add_track`. cliext **changes and checks** them, in batched ops files:
`cliext.animator edit`, `cliext.anim edit_clips` (a model's import) and `edit_clip` (.anim), `cliext.timeline edit`.
Nothing in cliext creates a state, transition, parameter, layer, track or timeline, so the Pipeline call comes first.

## Before anything

1. **Folders.** Let a command make them: `cliext.anim mask` and Pipeline's create commands (`create_controller`,
   `animation.timeline create`, `asset create_folder`) all do. Don't make asset folders on disk (`New-Item`,
   `mkdir`): Pipeline's create commands don't see a folder Unity hasn't imported yet and make `<Folder> 1` beside
   it. If you did, run `cliext.wait idle --refresh true` before the create, and check for a `<Folder> 1` after.
2. **Clip names.** `cliext.anim takes --path <model>` for every model you take clips from: its takes (frame ranges)
   and the clips it imports (`name`, `first`, `last`). Reference a clip by the clip `name`, never a guessed take name.
   A model can import two clips with the same name (one may be a 1-frame pose): cliext commands then fail (exit 2)
   listing both with their lengths, and so does a bare model path when the model has several clips. Pick by name AND
   length. For a name two clips share, cut a uniquely named clip from the right range with `edit_clips`
   (`{"op":"add_clip","name":"<unique>","take":"<take>","first":0,"last":<frame>}`), read its length back, use it.
   The new clip starts with every setting off (`loopTime=false`, no root locks), whatever the take's other clips
   use: give it `"set":"loopTime=true"` in the same op if it must loop. `cliext.anim clip` also returns every curve
   (long): read `length` from it, not the whole output.
3. **Motion references** are `<model file>@<clip name>` (or an `.anim` path). Pipeline's `add_state --motion` given a
   model file fails "resolved to a GameObject, not an AnimationClip", and cliext's `set_state` accepts a bare model
   path only when the model has exactly one clip. Always write the `@<clip>`.

## Animator controller loop

1. Pipeline, one call each: `create_controller --path`, `add_parameter --controller <c> --name <n> --type
   Float|Int|Bool|Trigger`, `add_layer --controller <c> --name <layer>`, `add_state --controller <c> --layer <layer>
   --name <state> [--is-default true]` (no `--motion`), then `add_transition --controller <c> --layer <layer>
   --from-state <a> --to-state <b>` for **every** transition you will set (`AnyState`/`Entry` as sources, `Exit` as
   destination). Leave out `--conditions` (it takes JSON, which PowerShell 5.1 mangles): set them in step 3.
2. A mask for the layer: `cliext.anim mask --path <folder>/<Name>.mask --humanoid "Root=false;Body=false;..."`.
   A new mask starts with every body part on (Root, Body, Head, LeftLeg, RightLeg, LeftArm, RightArm, LeftFingers,
   RightFingers, LeftFootIK, RightFootIK, LeftHandIK, RightHandIK): turn off what the layer must not drive, then read
   the `humanoid` "X off" list it returns.
3. `cliext.animator edit --path <c> --ops-file ops.json` from `ops/layer-with-blendtree.json`: motions with
   `set_state`, the mask and weight with `set_layer`, `add_blendtree`, and `set_transition` (conditions, exit time,
   duration) on the transitions step 1 added. `set_transition` before its transition exists fails: "No transition from
   A to B ... Pipeline's animation.animator add_transition adds one".
4. `cliext.animator validate --path <c>` (exit 6 on errors). Its "No motion." warning on an intentionally empty state
   (the one an overlay layer rests in) is expected; fix every error. Then `cliext.anim check --model <the character's
   own model file or prefab> --clips <c>` (not the scene; exit 6 on a mismatch).
5. Assign it: `component set_field --target <character> --component Animator --field m_Controller --value <c>`. A
   bare asset path works as the reference value (so does `{"path":"<c>"}`, which PowerShell 5.1 mangles). It changes
   the scene: save the scene to keep it.

## Clips and events

- Clips inside a model are import settings: `cliext.anim edit_clips --path <model>` with `set_clip` (loop, root
  motion: `set="loopTime=true;lockRootRotation=true"`), `set_events`, `set_mask`, `add_clip`, `remove_clip`; it
  reimports once. `set_events` **replaces** the clip's events: read them from `takes` first and include the ones to
  keep. Model clip event times are normalized 0-1 (`"0.6:OnRelease"`); `.anim` events (`edit_clip add_event`) are in
  seconds. `edit_clip` refuses a model's clip and names `edit_clips`.
- Read back: `cliext.anim takes` (events per clip) or `cliext.anim clip --path <model>@<clip>` (time in seconds).

## Timeline loop

1. `animation.timeline create --path <t>.playable`, then `animation.timeline add_track --timeline <t> --track-type
   Animation --name <track>` (and `Audio`, `Signal`, ...).
2. `cliext.timeline edit --path <t> --ops-file ops.json` from `ops/cutscene.json`: `add_clip` with `time` and
   `set="duration=..."`. Its result echoes each clip's `start`, `duration` and the timeline `duration`.
3. Fixed length: the asset's `m_DurationMode` (`--value 1`, FixedLength) and `m_FixedDuration` (`--value <seconds>`),
   set with Pipeline's `component set_field --target <t>.playable --field <f> --value <v>`. `get_fields` doesn't list
   `m_FixedDuration` (it's hidden); read it with `--field m_FixedDuration`. `set_field` on an asset changes it in
   memory only and leaves it unsaved: `cliext.timeline inspect --path <t>` shows the new `duration`, but the file
   keeps the old one until you save it (`script eval --timeout 30 --file` a snippet with
   `AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath("<t>")); return "saved";`). A later
   `cliext.timeline edit` saves it too.
4. The director object needs a PlayableDirector **before** `bind`: `gameobject create --name <d>`, `component add
   --target <d> --type PlayableDirector`, then `component set_field --target <d> --component PlayableDirector --field
   m_PlayableAsset --value <t>.playable` (a bare asset path works as the reference). Then `cliext.timeline bind
   --director <d> --track <track> --object <character>` (binds its Animator; an Audio track needs an AudioSource on
   the object). Check with `cliext.timeline inspect --director <d>` (`boundTo` per track).
5. Save the scene (`cliext.scene save_open --scene <path>`) before scrubbing, so a check after it compares with the
   saved state.

## Scrubbing and "which clip is playing"

`cliext.timeline director --director <d> --time 5` (Edit mode too) returns `time`, `duration`, `state` and `tracks`:
each track's active clip (two with weights while they blend), its local time, the AnimationClip it plays and that
clip's length. That is the answer to "which clip is playing at 5 s"; quote it. Edit-mode screenshots often don't
show the pose.

In Edit mode the scrub is sampled the way the Timeline window previews it: the bound objects are put back
afterwards, so the scene keeps its saved pose (the reply's `scene` says so) and saving doesn't save the scrubbed
frame. `--read "<objects>"` returns those objects' position and rotation at that time. An object is a name, a path
from the scene root (a leading `/` is optional) or a path relative to a bound object (`Root/Hips` under the bound
character): take paths from `scene get_hierarchy`, not from the model's name; a wrong path's error names where the
object is. Leave out `--keep-pose true` (it leaves the frame in the scene, unsaved) unless the user wants the posed
scene.

To prove the pose itself matches a clip, read it with `--read` and compare it; nothing in the scene changes, so there
is nothing to restore (putting a pose back by hand leaves float noise in the saved scene):
1. `cliext.eval run --file <copy of ops/pose.cs> --vars "mode=bones;object=<character>;clip=-;time=0;read=-"`: every
   bone's path, joined with `;`.
2. `cliext.timeline director --director <d> --time <t> --read "<that list>" --format json`, saved to a file.
3. `pose.cs` with `mode=compare;object=<character>;clip=<model>@<animationClip from tracks>;time=<localTime>;read=<that file>`
   for the active clip and a neighbour: the active one has a mean under about 1 degree (its max can still be
   several), the other is clearly away (mean 5+). `pose.cs` wraps the time of a looping clip.

Never scrub with `--keep-pose true` to prove a pose, and never save a scrubbed pose.

## Failure and rollback

- `cliext.animator edit`, `cliext.anim edit_clips` / `edit_clip` and `cliext.timeline edit` are all or nothing: an op
  that fails saves nothing (`saved: false`, `failedAt`, `error`). Fix that op and rerun the whole file.
- `cliext.timeline edit` also fails an op that would change nothing (a track already muted, a rename to the same
  name), and `set_default` fails on the state that is already default: leave such ops out.
- Pipeline adds are one call each and not undone by a later failure. After a failed run, `cliext.animator inspect`
  or `cliext.timeline inspect` before re-adding, or you get duplicates (a duplicate parameter fails).

## Reference

| Ops | Fields |
|---|---|
| animator `set_state` | `state` (name or `Sub/State`), `motion`, `set="speed=1.2;tag=Move;writeDefaultValues=false"` |
| animator `set_transition` | `from` (state or `Any`), `to` (state or `Exit`), `index`, `conditions="P If;Speed Greater 0.1"` (`""` clears), `set="hasExitTime=true;exitTime=0.9;duration=0.1"` |
| animator `add_blendtree` | `state` (new or existing), `type` Simple1D (default), SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, Direct; `parameter`, `parameterY` (Float); `children="<model>@<clip>@0;..."` (1D threshold or `@x,y`) |
| animator others | `set_layer` (`set="defaultWeight=1;blendingMode=Override;avatarMask=<path>"`), `set_default`, `set_parameter` (`value`), `rename_state`, `remove_*`, `add_substatemachine`; every op takes `layer` (name or index, default 0) |
| anim `edit_clips` | `add_clip` (`name`, `take`, `first`, `last` frames, `set`), `set_clip`, `remove_clip`, `set_events` (`events="0.5:Fn;0.9:Fn:Param"`), `set_mask` (`mask` path or `none`) |
| anim `edit_clip` (.anim) | `set_key` / `remove_key` (`path`, `type`, `property`, `time`, `value`, `tangent`), `add_event`, `remove_event`, `set_settings` |
| timeline `edit` | `add_clip` (`track`, `clip` path or `<model>@<clip>`, `time`, `set`, `name`), `set_clip` (`set="start=;duration=;clipIn=;timeScale=;easeIn=;easeOut="`), `move_clip` (`to`), `remove_clip`, `remove_track`, `rename_track`, `mute_track`, `lock_track`, `add_marker` (`time`, no `track` for the timeline's markers, `signal`), `remove_marker` |

Play mode: `cliext.animator live --object <go>` (state, normalized time, clip weights, parameters) and `drive
--object <go> --parameter <p> --value <v>` (or `--state "Layer.State"`); `director --action play|pause|resume|stop`.

## Gotchas

- Git Bash rewrites arguments that start with `/` (`--target /Character` arrives as `C:/Program Files/Git/Character`):
  prefix the command with `MSYS_NO_PATHCONV=1`, or use PowerShell, for hierarchy paths.
- `director --time` past the timeline's `duration` exits 2: a clip-based timeline ends at its last clip.
- A model clip's mask and an Animator layer's mask are separate: `set_mask` changes the import, `set_layer` the layer.

## Reporting

Say what the project needs, not what it lacks: "the Animator's object needs a method `OnRelease` for this event"
rather than "nothing handles it", unless a search (Grep the scripts for the name) showed it. Name every import
setting you changed on a model (it affects every user of that model) and every scene object you restored.

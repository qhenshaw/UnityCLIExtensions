using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCLIExtensions.Animation
{
    /// <summary>
    /// <c>cliext.animator</c>: what Pipeline's animation.animator commands leave out: a full read with blend trees and
    /// transition settings, removing and changing states, transitions, parameters and layers, blend trees,
    /// sub-state machines, a validation pass, and the live state of an Animator in Play mode.
    /// </summary>
    internal static class AnimatorCommands
    {
        private const string Group = "cliext.animator";

        [CliGroupCommand(Group, "Describe an AnimatorController: parameters with defaults, layers (weight, blending, mask, sync), states by path with motion, blend tree, speed, tag, behaviours, " +
                                "and every transition with conditions, exit time, duration, offset and interruption, plus Any State transitions and sub-state machines.",
            Name = "inspect", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Inspect(CommandContext cmd, [CliArg("path", "The .controller asset", Required = true)] string path)
        {
            try { cmd.Result.Write(AnimatorEditing.Describe(AnimatorEditing.Load(path))); return ExitCode.Success; }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
        }

        [CliGroupCommand(Group, "Check an AnimatorController: states with no motion, unreachable states, transitions that can never fire, conditions on missing or mistyped parameters, " +
                                "blend trees on missing or non-Float parameters, and unused parameters. Exit 6 when there are errors.",
            Name = "validate", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Validate(CommandContext cmd, [CliArg("path", "The .controller asset", Required = true)] string path)
        {
            AnimatorController c;
            try { c = AnimatorEditing.Load(path); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            var issues = AnimatorEditing.Validate(c);
            foreach (var i in issues) cmd.Result.WriteRecord(i);
            int errors = issues.Count(i => (string)i.GetType().GetProperty("severity").GetValue(i) == "error");
            if (errors > 0) { cmd.Log.Error($"{errors} error(s)."); return ExitCode.Failed; }
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Edit an AnimatorController with a JSON list of ops, saved once: remove_state, rename_state, set_state (motion, set=\"speed=1.2;tag=Move;writeDefaultValues=false;speedParameterActive=true;speedParameter=Speed\"), " +
                                "set_default, add_substatemachine, remove_substatemachine, set_transition (from, to or Exit, from Any; conditions=\"Speed Greater 0.1;Grounded If\"; set=\"hasExitTime=false;duration=0.1\"), " +
                                "remove_transition, remove_parameter (also strips its conditions), set_parameter (default value), set_layer (set=\"defaultWeight=1;blendingMode=Additive;iKPass=true\"), remove_layer, " +
                                "add_blendtree (state, type Simple1D|SimpleDirectional2D|FreeformDirectional2D|FreeformCartesian2D|Direct, parameter, parameterY, children=\"Assets/Walk.anim@0;Assets/Run.anim@1\"). " +
                                "Pipeline's animation.animator commands add states, transitions, parameters and layers. An op that fails stops the run; the ones before it stay (Undo in the Editor reverts).",
            Name = "edit", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Edit(
            CommandContext cmd,
            [CliArg("path", "The .controller asset", Required = true)] string path,
            [CliArg("ops", "JSON array of ops")] string ops = "",
            [CliArg("ops-file", "A file holding the JSON array")] string opsFile = "")
        {
            string json = !string.IsNullOrWhiteSpace(opsFile) ? File.ReadAllText(opsFile) : ops;
            if (string.IsNullOrWhiteSpace(json)) { cmd.Log.Error("Pass --ops or --ops-file."); return ExitCode.InvalidUsage; }
            AnimatorOpList list;
            try { string t = json.Trim(); list = JsonUtility.FromJson<AnimatorOpList>(t.StartsWith("[") ? "{\"ops\":" + t + "}" : t); }
            catch (Exception e) { cmd.Log.Error($"The ops aren't valid JSON: {e.Message}"); return ExitCode.InvalidUsage; }
            if (list?.ops == null || list.ops.Length == 0) { cmd.Log.Error("No ops given."); return ExitCode.InvalidUsage; }

            AnimatorController c;
            try { c = AnimatorEditing.Load(path); }
            catch (ArgumentException e) { cmd.Log.Error(e.Message); return ExitCode.NotFound; }
            Undo.RegisterCompleteObjectUndo(c, "cliext.animator edit");
            var results = new List<object>();
            for (int i = 0; i < list.ops.Length; i++)
            {
                try { results.Add(AnimatorEditing.Apply(c, list.ops[i])); }
                catch (Exception e)
                {
                    string message = e is ArgumentException ? e.Message : e.ToString();
                    EditorUtility.SetDirty(c);
                    AssetDatabase.SaveAssets();
                    cmd.Result.Write(new { path, applied = results.Count, results, failedAt = i, error = message });
                    cmd.Log.Error($"Op {i} ({list.ops[i].op}) failed: {message}" + (results.Count > 0 ? $" The {results.Count} op(s) before it were saved." : ""));
                    return ExitCode.Failed;
                }
            }
            EditorUtility.SetDirty(c);
            AssetDatabase.SaveAssets();
            var issues = AnimatorEditing.Validate(c).Where(x => (string)x.GetType().GetProperty("severity").GetValue(x) == "error").ToArray();
            cmd.Result.Write(new { path, applied = results.Count, results, errors = issues });
            return ExitCode.Success;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Play mode

        [CliGroupCommand(Group, "The live state of an Animator in Play mode (--object: a GameObject name or path): each layer's current state (by name), normalized time, the transition in progress, " +
                                "and every parameter's current value.",
            Name = "live", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Live(CommandContext cmd, [CliArg("object", "GameObject name or hierarchy path with an Animator", Required = true)] string obj)
        {
            var animator = FindAnimator(obj, out string error);
            if (animator == null) { cmd.Log.Error(error); return ExitCode.NotFound; }
            var names = StateNames(animator);
            string Name(int hash) => names.TryGetValue(hash, out var n) ? n : hash.ToString();
            cmd.Result.Write(new
            {
                gameObject = animator.gameObject.name,
                controller = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : null,
                playing = Application.isPlaying,
                layers = Enumerable.Range(0, animator.layerCount).Select(i =>
                {
                    var st = animator.GetCurrentAnimatorStateInfo(i);
                    bool inTransition = animator.IsInTransition(i);
                    var next = animator.GetNextAnimatorStateInfo(i);
                    var clips = animator.GetCurrentAnimatorClipInfo(i);
                    return new
                    {
                        layer = animator.GetLayerName(i),
                        weight = animator.GetLayerWeight(i),
                        state = Name(st.fullPathHash),
                        normalizedTime = Math.Round(st.normalizedTime, 3),
                        length = st.length,
                        clips = clips.Select(ci => $"{ci.clip.name} ({Math.Round(ci.weight, 2)})").ToArray(),
                        transitionTo = inTransition ? Name(next.fullPathHash) : null
                    };
                }).ToArray(),
                parameters = animator.parameters.Select(p => new
                {
                    p.name,
                    type = p.type.ToString(),
                    value = p.type == AnimatorControllerParameterType.Float ? ValueText.Format(animator.GetFloat(p.nameHash))
                          : p.type == AnimatorControllerParameterType.Int ? animator.GetInteger(p.nameHash).ToString()
                          : ValueText.Format(animator.GetBool(p.nameHash))
                }).ToArray()
            });
            return ExitCode.Success;
        }

        [CliGroupCommand(Group, "Set an Animator parameter in Play mode (--value; for a Trigger, true sets it and false resets it), or play a state directly (--state Layer.State --layer n).",
            Name = "drive", Tags = new[] { "cliext", "animation" }, Effect = CommandEffect.Destructive)]
        internal static ExitCode Drive(
            CommandContext cmd,
            [CliArg("object", "GameObject name or hierarchy path with an Animator", Required = true)] string obj,
            [CliArg("parameter", "The parameter to set")] string parameter = "",
            [CliArg("value", "Its value")] string value = "",
            [CliArg("state", "A state to cross-fade to instead (Base Layer.Run)")] string state = "",
            [CliArg("layer", "The layer for --state")] int layer = -1,
            [CliArg("fade", "Cross-fade seconds for --state")] float fade = 0.1f)
        {
            var animator = FindAnimator(obj, out string error);
            if (animator == null) { cmd.Log.Error(error); return ExitCode.NotFound; }
            if (!string.IsNullOrWhiteSpace(state))
            {
                animator.CrossFadeInFixedTime(state, fade, layer);
                cmd.Result.Write(new { state, layer, fade });
                return ExitCode.Success;
            }
            var p = animator.parameters.FirstOrDefault(x => x.name == parameter);
            if (p == null) { cmd.Log.Error($"No parameter '{parameter}'. Parameters: {string.Join(", ", animator.parameters.Select(x => x.name))}"); return ExitCode.NotFound; }
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: animator.SetFloat(p.nameHash, (float)ValueText.Parse(value, typeof(float))); break;
                case AnimatorControllerParameterType.Int: animator.SetInteger(p.nameHash, (int)ValueText.Parse(value, typeof(int))); break;
                case AnimatorControllerParameterType.Bool: animator.SetBool(p.nameHash, (bool)ValueText.Parse(value, typeof(bool))); break;
                case AnimatorControllerParameterType.Trigger:
                    if (string.IsNullOrEmpty(value) || (bool)ValueText.Parse(value, typeof(bool))) animator.SetTrigger(p.nameHash); else animator.ResetTrigger(p.nameHash);
                    break;
            }
            cmd.Result.Write(new { parameter = p.name, type = p.type.ToString(), value });
            return ExitCode.Success;
        }

        private static Animator FindAnimator(string obj, out string error)
        {
            error = null;
            var go = GameObject.Find(obj) ?? UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Include)
                .Select(a => a.gameObject).FirstOrDefault(g => string.Equals(g.name, obj, StringComparison.OrdinalIgnoreCase));
            if (go == null) { error = $"No GameObject '{obj}' in the open scenes."; return null; }
            var animator = go.GetComponent<Animator>() ?? go.GetComponentInChildren<Animator>();
            if (animator == null) error = $"{go.name} has no Animator.";
            return animator;
        }

        /// <summary>fullPathHash -> "Layer.State" for every state of the Animator's controller.</summary>
        private static Dictionary<int, string> StateNames(Animator animator)
        {
            var map = new Dictionary<int, string>();
            var rac = animator.runtimeAnimatorController;
            var controller = rac as AnimatorController ?? (rac is AnimatorOverrideController o ? o.runtimeAnimatorController as AnimatorController : null);
            if (controller == null) return map;
            foreach (var layer in controller.layers)
                foreach (var (path, state, _) in AnimatorEditing.AllStates(layer.stateMachine))
                {
                    string full = layer.name + "." + path.Replace('/', '.');
                    map[Animator.StringToHash(full)] = full;
                }
            return map;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityCLIExtensions.InputSystem.Tests
{
    public class InputActionsEditingTests
    {
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _made.Clear();
        }

        private InputActionAsset InMemory()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            _made.Add(asset);
            var player = asset.AddActionMap("Player");
            var jump = player.AddAction("Jump", InputActionType.Button);
            jump.AddBinding("<Gamepad>/buttonSouth", groups: "Gamepad");
            jump.AddBinding("<Keyboard>/space", groups: "Keyboard");
            player.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
            player.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
            asset.AddControlScheme("Gamepad").WithRequiredDevice("<Gamepad>").Done();
            return asset;
        }

        private static object Apply(InputActionAsset a, string json) =>
            InputActionsEditing.Apply(a, JsonUtility.FromJson<InputOp>(json), new List<string>());

        private static string Error(InputActionAsset a, string json) =>
            Assert.Catch<ArgumentException>(() => Apply(a, json)).Message;

        [Test]
        public void UnknownNames_SayDidYouMean_OrPointAtInspect()
        {
            var a = InMemory();
            StringAssert.Contains("Did you mean: Player?", Error(a, "{\"op\":\"remove_action\",\"map\":\"Playr\",\"action\":\"Jump\"}"));
            StringAssert.Contains("Did you mean: Jump?", Error(a, "{\"op\":\"remove_action\",\"map\":\"Player\",\"action\":\"Jum\"}"));
            StringAssert.Contains("cliext.input inspect", Error(a, "{\"op\":\"remove_action\",\"map\":\"Player\",\"action\":\"Zzqx\"}"));
            StringAssert.Contains("Did you mean: remove_action?", Error(a, "{\"op\":\"remove_actions\"}"));
            StringAssert.Contains("Did you mean: Gamepad?", Error(a, "{\"op\":\"remove_scheme\",\"name\":\"Gamepd\"}"));
            StringAssert.Contains("bindings 0-1, not 5", Error(a, "{\"op\":\"remove_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"index\":5}"));
        }

        [Test]
        public void OpsThatWouldChangeNothing_Fail()
        {
            var a = InMemory();
            StringAssert.Contains("would change nothing", Error(a, "{\"op\":\"set_action\",\"map\":\"Player\",\"action\":\"Jump\",\"type\":\"Button\"}"));
            StringAssert.Contains("needs type, controlType", Error(a, "{\"op\":\"set_action\",\"map\":\"Player\",\"action\":\"Jump\"}"));
            StringAssert.Contains("needs path, groups", Error(a, "{\"op\":\"set_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"index\":0}"));
            StringAssert.Contains("would change nothing", Error(a, "{\"op\":\"set_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"index\":0,\"path\":\"<Gamepad>/buttonSouth\"}"));
            StringAssert.Contains("already has that name", Error(a, "{\"op\":\"rename_action\",\"map\":\"Player\",\"action\":\"Jump\",\"name\":\"Jump\"}"));
            StringAssert.Contains("already has an action 'Move'", Error(a, "{\"op\":\"rename_action\",\"map\":\"Player\",\"action\":\"Jump\",\"name\":\"Move\"}"));
            StringAssert.Contains("rename_action needs name", Error(a, "{\"op\":\"rename_action\",\"map\":\"Player\",\"action\":\"Jump\"}"));
            StringAssert.Contains("already exists", Error(a, "{\"op\":\"add_scheme\",\"name\":\"Gamepad\"}"));
            StringAssert.Contains("add_map needs a map", Error(a, "{\"op\":\"add_map\"}"));
        }

        [Test]
        public void BadEnumsInteractionsAndComposites_ListTheValidOnes()
        {
            var a = InMemory();
            StringAssert.Contains("Use one of: Value, Button, PassThrough", Error(a, "{\"op\":\"add_action\",\"map\":\"Player\",\"action\":\"Dash\",\"type\":\"Buton\"}"));
            StringAssert.Contains("No interaction 'noSuch'", Error(a, "{\"op\":\"set_action\",\"map\":\"Player\",\"action\":\"Jump\",\"interactions\":\"noSuch\"}"));
            StringAssert.Contains("Did you mean: Hold", Error(a, "{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"path\":\"<Keyboard>/j\",\"interactions\":\"Hld\"}"));
            StringAssert.Contains("No processor", Error(a, "{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"path\":\"<Keyboard>/j\",\"processors\":\"noSuchProcessor\"}"));
            StringAssert.Contains("Composites: 1DAxis, 2DVector", Error(a, "{\"op\":\"add_composite\",\"map\":\"Player\",\"action\":\"Move\",\"composite\":\"Zzqx\",\"parts\":\"Up=<Keyboard>/w\"}"));
            StringAssert.Contains("Did you mean: up", Error(a, "{\"op\":\"add_composite\",\"map\":\"Player\",\"action\":\"Move\",\"composite\":\"2DVector\",\"parts\":\"Upp=<Keyboard>/w\"}"));
            StringAssert.Contains("needs parts", Error(a, "{\"op\":\"add_composite\",\"map\":\"Player\",\"action\":\"Move\",\"composite\":\"2DVector\"}"));
            Assert.AreEqual(2, a.FindAction("Player/Jump").bindings.Count, "a failed op added bindings");
            Assert.AreEqual(0, a.FindAction("Player/Move").bindings.Count, "a failed add_composite added a binding");
        }

        [Test]
        public void SetAction_KeepsTheActionsIdAndPlace()
        {
            var a = InMemory();
            var jump = a.FindAction("Player/Jump");
            var id = jump.id;
            Apply(a, "{\"op\":\"set_action\",\"map\":\"Player\",\"action\":\"Jump\",\"type\":\"PassThrough\",\"interactions\":\"Hold\"}");
            var after = a.FindAction("Player/Jump");
            Assert.AreEqual(id, after.id);
            Assert.AreEqual(InputActionType.PassThrough, after.type);
            Assert.AreEqual("Hold", after.interactions);
            Assert.AreEqual("Jump", a.FindActionMap("Player").actions[0].name);
            Assert.AreEqual(2, after.bindings.Count);
            var copy = InputActionAsset.FromJson(a.ToJson());
            _made.Add(copy);
            Assert.AreEqual(InputActionType.PassThrough, copy.FindAction("Player/Jump").type, "the change isn't in the saved JSON");
        }

        [Test]
        public void PathProblem_NamesUnknownControlsButAcceptsDerivedOnes()
        {
            StringAssert.Contains("Did you mean: buttonSouth", InputActionsEditing.PathProblem("<Gamepad>/buttonSouht"));
            StringAssert.Contains("layout the Input System doesn't know", InputActionsEditing.PathProblem("<NoSuchDevice>/x"));
            Assert.IsNull(InputActionsEditing.PathProblem("<Gamepad>/buttonSouth"));
            Assert.IsNull(InputActionsEditing.PathProblem("<Gamepad>/leftStick/up"));
            Assert.IsNull(InputActionsEditing.PathProblem("<Keyboard>/space"));
            Assert.IsNull(InputActionsEditing.PathProblem("<Mouse>/leftButton"));
            Assert.IsNull(InputActionsEditing.PathProblem("<Touchscreen>/touch*/press"));
            Assert.IsNull(InputActionsEditing.PathProblem("*/{Submit}"));
        }

        [Test]
        public void Load_TellsMissingFromWrongPaths()
        {
            // The exception types (internal to the Editor assembly) decide the exit code: not found 13, invalid usage 2.
            StringAssert.Contains("Create > Input Actions", Kind("Assets/CliExtNoSuch/None.inputactions", "AnimNotFoundException"));
            StringAssert.Contains("isn't under Assets/ or Packages/", Kind("ProjectSettings/ProjectVersion.txt", "AnimUsageException"));
            StringAssert.Contains("not an .inputactions asset", Kind("Packages/manifest.json", "AnimUsageException"));
            Kind("", "AnimUsageException");
        }

        private static string Kind(string path, string type)
        {
            var e = Assert.Catch<ArgumentException>(() => InputActionsEditing.Load(path, out _));
            Assert.AreEqual(type, e.GetType().Name, e.Message);
            return e.Message;
        }

        [Test]
        public void FindControls_DoesNotThrowOnFullPaths()
        {
            // InputControlList.CopyTo is not implemented, so a LINQ ToList over it threw for every full control path.
            Assert.DoesNotThrow(() => InputCommands.InputSystemFind("<Keyboard>/space"));
            Assert.DoesNotThrow(() => InputCommands.InputSystemFind("<Gamepad>/buttonSouth"));
        }

        [Test]
        public void AddBinding_ToAControlAnotherActionUses_WarnsNamingIt()
        {
            var a = InMemory();
            var warnings = new List<string>();
            InputActionsEditing.Apply(a, JsonUtility.FromJson<InputOp>("{\"op\":\"add_action\",\"map\":\"Player\",\"action\":\"Dash\",\"type\":\"Button\"}"), warnings);
            InputActionsEditing.Apply(a, JsonUtility.FromJson<InputOp>("{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Dash\",\"path\":\"<Keyboard>/space\"}"), warnings);
            Assert.AreEqual(1, warnings.Count, string.Join(" | ", warnings));
            StringAssert.Contains("<Keyboard>/space is also bound to Player/Jump", warnings[0]);
            warnings.Clear();
            InputActionsEditing.Apply(a, JsonUtility.FromJson<InputOp>("{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Dash\",\"path\":\"<Keyboard>/leftShift\"}"), warnings);
            Assert.IsEmpty(warnings, "A control no other action uses gives no warning.");
            InputActionsEditing.Apply(a, JsonUtility.FromJson<InputOp>("{\"op\":\"add_composite\",\"map\":\"Player\",\"action\":\"Move\",\"composite\":\"2DVector\",\"parts\":\"Up=<Keyboard>/space;Down=<Keyboard>/s\"}"), warnings);
            StringAssert.Contains("also bound to Player/Jump, Player/Dash", string.Join(" | ", warnings), "A composite's part is checked too.");
        }

        [Test]
        public void Groups_ThatMatchNoControlScheme_Warn_UnlessTheBatchAddsTheScheme()
        {
            var a = InMemory();
            var ops = new[]
            {
                JsonUtility.FromJson<InputOp>("{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"path\":\"<Gamepad>/buttonNorth\",\"groups\":\"Gamepads\"}"),
                JsonUtility.FromJson<InputOp>("{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"path\":\"<Keyboard>/enter\",\"groups\":\"Keyboard&Mouse\"}"),
                JsonUtility.FromJson<InputOp>("{\"op\":\"add_binding\",\"map\":\"Player\",\"action\":\"Jump\",\"path\":\"<Gamepad>/buttonEast\",\"groups\":\"Gamepad\"}")
            };
            foreach (var op in ops) InputActionsEditing.Apply(a, op, new List<string>());
            var warnings = InputActionsEditing.GroupWarnings(a, ops).ToList();
            Assert.AreEqual(2, warnings.Count, string.Join(" | ", warnings));
            StringAssert.Contains("groups 'Gamepads'", warnings[0]);
            StringAssert.Contains("Did you mean: Gamepad?", warnings[0]);
            StringAssert.Contains("groups 'Keyboard&Mouse'", warnings[1]);

            a.AddControlScheme("Keyboard&Mouse").WithRequiredDevice("<Keyboard>").Done();
            Assert.AreEqual(1, InputActionsEditing.GroupWarnings(a, ops).Count(), "A scheme the batch adds (before the check) counts.");
        }
    }
}

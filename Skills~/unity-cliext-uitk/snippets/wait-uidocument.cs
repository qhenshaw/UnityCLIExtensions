// cliext.playmode wait_until --file <this file> --vars "document=<GameObject>;element=<element name>" --timeout 20
// Returns "ready" once Play mode runs, the UIDocument has a live panel and the element exists.
if (!UnityEditor.EditorApplication.isPlaying) return "not in Play mode";
var go = UnityEngine.GameObject.Find("{{document}}");
if (go == null) return "no active GameObject {{document}}";
var doc = go.GetComponent<UnityEngine.UIElements.UIDocument>();
if (doc == null) return "{{document}} has no UIDocument";
var root = doc.rootVisualElement;
if (root == null || root.panel == null) return "no live panel yet";
return UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.VisualElement>(root, "{{element}}") != null ? "ready" : "no #{{element}} yet";

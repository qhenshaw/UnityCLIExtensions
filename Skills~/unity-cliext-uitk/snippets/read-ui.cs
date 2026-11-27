// cliext.eval run --file <this file> --vars "document=<GameObject>;label=<Label name>"
// The live-tree pattern for snippets: call UQueryExtensions as static methods (root.Q(...) is CS1061 in snippets).
// Replace the volume read with the game state the UI drives.
var go = UnityEngine.GameObject.Find("{{document}}");
var doc = go == null ? null : go.GetComponent<UnityEngine.UIElements.UIDocument>();
if (doc == null || doc.rootVisualElement == null) return "no active GameObject {{document}} with a live UIDocument";
var root = doc.rootVisualElement;
var label = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Label>(root, "{{label}}");
return (label == null ? "no Label {{label}}" : "label=" + label.text)
    + " volume=" + UnityEngine.AudioListener.volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

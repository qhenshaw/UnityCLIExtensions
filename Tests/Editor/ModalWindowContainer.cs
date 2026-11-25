using UnityEditor;

namespace UnityCLIExtensions.Tests
{
    /// <summary>
    /// Stands in for the Package Manager's internal <c>ModalWindowContainer</c> in <see cref="MissingSignatureAnswerTests"/>:
    /// <see cref="MissingSignatureAnswer"/> recognizes the dialog by this type name and its title.
    /// </summary>
    internal class ModalWindowContainer : EditorWindow
    {
    }
}

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityCLIExtensions.Tests")]
// The Timeline commands share the Animation commands' error helpers (exit codes, "Did you mean", asset type checks).
[assembly: InternalsVisibleTo("UnityCLIExtensions.Timeline")]
// So do the Input System commands.
[assembly: InternalsVisibleTo("UnityCLIExtensions.InputSystem")]
// So do the Cinemachine and Splines commands.
[assembly: InternalsVisibleTo("UnityCLIExtensions.Cinemachine")]
[assembly: InternalsVisibleTo("UnityCLIExtensions.Splines")]
// So do the AI Navigation commands.
[assembly: InternalsVisibleTo("UnityCLIExtensions.AINavigation")]
// So do the Addressables commands.
[assembly: InternalsVisibleTo("UnityCLIExtensions.Addressables")]
// The Timeline tests check the shared clip-reference errors.
[assembly: InternalsVisibleTo("UnityCLIExtensions.Timeline.Tests")]

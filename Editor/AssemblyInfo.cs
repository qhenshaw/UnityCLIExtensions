using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityCLIExtensions.Tests")]
// The Timeline commands share the Animation commands' error helpers (exit codes, "Did you mean", asset type checks).
[assembly: InternalsVisibleTo("UnityCLIExtensions.Timeline")]
// So do the Input System commands.
[assembly: InternalsVisibleTo("UnityCLIExtensions.InputSystem")]

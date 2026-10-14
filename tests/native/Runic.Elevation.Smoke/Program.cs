// The elevated helper of the RealElevation cases in Runic.Platform.Windows.Elevation.Tests, which start it with
// ShellExecuteExW and runas from its install directory under Program Files. SmokeActions.cs holds the actions; the
// test project's stand-in runs the same code without elevation.
using Runic.Elevation.Smoke;
using Runic.Platform.Windows.Elevation;

if (ElevatedHelper.Match(PrivilegedActions.All, args, out var arguments) is not { } action) return 2;
return SmokeActions.Run(action, arguments);

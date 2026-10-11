using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Options of the explicit native modes, passed as --test-parameter <name>=<value>.
internal static class TestOptions
{
    internal static bool Flag(string name) => Value(name) is { } value && bool.TryParse(value, out bool enabled) && enabled;

    internal static string? Value(string name) => TestContext.Parameters.TryGetValue(name, out var values) ? values.LastOrDefault() : null;
}

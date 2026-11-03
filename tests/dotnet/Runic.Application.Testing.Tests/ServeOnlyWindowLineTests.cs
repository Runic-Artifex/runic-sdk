using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// RunicServeOnlyApplication reads the Window type of a RUNIC_APPLICATION_WINDOW=<url> <type> line from its second
// space-separated field, so a host writes a closed generic Window type without spaces.
internal sealed class ServeOnlyWindowLineTests
{
    [Test]
    public void ParsesAGenericWindowTypeAndIgnoresLaterFields()
    {
        Require(RunicServeOnlyApplication.TryParseWindowLine(
                "RUNIC_APPLICATION_WINDOW=http://127.0.0.1:5000/ MyApp.DocumentWindow`1[Dictionary`2[String,Report]] future-field",
                out var url, out var type)
            && url == new Uri("http://127.0.0.1:5000/") && type == "MyApp.DocumentWindow`1[Dictionary`2[String,Report]]",
            $"The generic Window line was read as {url} {type}.");
        Require(RunicServeOnlyApplication.IsType("MyApp.DocumentWindow`1[Report]", "DocumentWindow`1[Report]")
            && RunicServeOnlyApplication.IsType("MyApp.DocumentWindow`1[Report]", "MyApp.DocumentWindow`1[Report]")
            && !RunicServeOnlyApplication.IsType("MyApp.DocumentWindow`1[Report]", "Window`1[Report]"),
            "A generic Window type did not match by its full name and its name only.");
        Require(!RunicServeOnlyApplication.TryParseWindowLine("RUNIC_APPLICATION_WINDOW=http://127.0.0.1:5000/", out _, out _)
            && !RunicServeOnlyApplication.TryParseWindowLine("RUNIC_APPLICATION_WINDOW=not-a-url MyApp.Window", out _, out _)
            && !RunicServeOnlyApplication.TryParseWindowLine("RUNIC_APPLICATION_URL=http://127.0.0.1:5000/", out _, out _),
            "An incomplete or foreign line was read as a Window line.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

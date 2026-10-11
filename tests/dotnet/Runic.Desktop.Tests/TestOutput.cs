using TUnit.Core;

namespace Runic.Desktop.Tests;

// Stands in for xUnit's ITestOutputHelper: writes a line to the output of the case that created it, also from
// threads (such as trace listeners) that do not carry the case's context.
internal sealed class TestOutput
{
    private readonly TestContext? _context = TestContext.Current;

    public void WriteLine(string message) => (TestContext.Current ?? _context)?.Output.WriteLine(message);
}

namespace Fixture.Tests;

public sealed class GreetingTests
{
    [Test]
    public async Task Greets_by_name()
    {
        await Assert.That(Greeting.For("Nix")).IsEqualTo("Hello, Nix");
        // nix/tests/test-runner.nix checks that the test application ran this test.
        if (Environment.GetEnvironmentVariable("FIXTURE_TEST_MARKER") is { Length: > 0 } marker)
            await File.WriteAllTextAsync(marker, nameof(Greets_by_name));
    }
}

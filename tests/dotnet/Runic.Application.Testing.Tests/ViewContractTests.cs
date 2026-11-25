using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// view-contract/1 (specs/application/README.md, "Presentations"; sdk#1324): on a connection that uses the feature, an
// open reply names the route's generated contract, "{ViewModel}:{fingerprint}", which a page's development build
// compares with its own. A connection without it, such as a page before 0.24 or a production build, receives the reply
// as before.
// Runs alone: GeneratedInteractionViewModel counts ChildView's static lifetimes (AssemblyInfo.cs).
[NotInParallel]
internal sealed class ViewContractTests
{
    [Test]
    public void AnOpenReplyNamesTheContractOnlyToAConnectionThatUsesViewContract()
    {
        using var model = new GeneratedInteractionViewModel();
        using var host = new RunicWindowTestHost<GeneratedInteractionViewModel>(model,
            (transport, content, vm) => new GeneratedInteractionBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "generatedInteraction", ViewLocator = new TestViewLocator() });
        var contract = $"{typeof(GeneratedInteractionViewModel).FullName}:{BridgeContractShape.Compute(typeof(GeneratedInteractionViewModel))}";

        using (var hello = JsonDocument.Parse(host.Transport.Request(
            """{"kind":"hello","protocol":4,"session":"s-contract","client":{"name":"t","version":"1"},"features":["view-contract/1"]}""",
            "development", "development-client")))
            Require(hello.RootElement.GetProperty("features").EnumerateArray().Any(feature => feature.GetString() == "view-contract/1"),
                $"The hello reply does not list view-contract/1: {hello.RootElement}");
        using (var opened = JsonDocument.Parse(host.Transport.Open("generatedInteraction", "p-development", "development", "development-client")))
            Require(opened.RootElement.GetProperty("presentation").GetString() == "ok"
                && opened.RootElement.TryGetProperty("contract", out var named) && named.GetString() == contract,
                $"The open reply does not name {contract}: {opened.RootElement}");

        Require(host.Transport.Request(
            """{"kind":"hello","protocol":4,"session":"s-plain","client":{"name":"t","version":"1"},"features":[]}""",
            "production", "production-client").StartsWith("{\"ok\":true", StringComparison.Ordinal), "The hello was refused.");
        using (var opened = JsonDocument.Parse(host.Transport.Open("generatedInteraction", "p-production", "production", "production-client")))
            Require(opened.RootElement.GetProperty("presentation").GetString() == "ok" && !opened.RootElement.TryGetProperty("contract", out _),
                $"A connection without view-contract/1 received a contract: {opened.RootElement}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

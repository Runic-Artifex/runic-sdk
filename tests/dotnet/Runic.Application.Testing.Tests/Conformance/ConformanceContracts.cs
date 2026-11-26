using System.Text.Json.Nodes;

namespace Runic.Application.Testing.Tests.Conformance;

// The contracts of the test Views, derived from their contract IR (specs/application/conformance/views.ir.json, embedded
// as ConformanceViews.ir.json) as specs/application/contract-ir.md, "Derived contracts", says: a View's contract is
// {id}:{fingerprint}, an interaction's {fingerprint}:interaction:{Name} and a view request's
// {fingerprint}:request:{Name}. The IR describes the hand-written Bridges below; ConformanceContractTests checks that it
// still does, and eng/views-contract-ir.test.mjs that its fingerprints are runic.views.fingerprint/1 of it.
internal static class ConformanceContracts
{
    internal const string ResourceName = "ConformanceViews.ir.json";

    private static readonly Lazy<JsonObject> Document = new(() =>
    {
        using var stream = typeof(ConformanceContracts).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The test assembly does not embed {ResourceName}.");
        return JsonNode.Parse(stream)!.AsObject();
    });

    // The test Views' contract IR.
    internal static JsonObject Ir => Document.Value;

    // The IR View of a test View's model type, whose id is the type's full name.
    internal static JsonObject View(Type model) =>
        Ir["views"]!.AsArray().Select(view => view!.AsObject()).SingleOrDefault(view => view["id"]!.GetValue<string>() == model.FullName)
            ?? throw new InvalidOperationException($"specs/application/conformance/views.ir.json has no View {model.FullName}.");

    // The View's fingerprint, which its Bridge passes as its contract fingerprint.
    internal static string Fingerprint(Type model) => View(model)["fingerprint"]!.GetValue<string>();

    // The View's contract, {id}:{fingerprint}: the operation contract of its asynchronous commands.
    internal static string Contract(Type model) => $"{model.FullName}:{Fingerprint(model)}";

    internal static string Interaction(Type model, string name) => $"{Fingerprint(model)}:interaction:{name}";

    internal static string Request(Type model, string name) => $"{Fingerprint(model)}:request:{name}";
}

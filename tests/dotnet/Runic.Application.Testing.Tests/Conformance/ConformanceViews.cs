using System.Text.Json.Nodes;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests.Conformance;

// The test Views of the conformance corpus (specs/application/conformance/README.md, "Test Views" and "Mutations"),
// implemented by the producer under test. The scenario runner owns the transport, the virtual clock and the wire; this
// hook owns the window and its model:
//
// - CreateWindow creates the window of one scenario: a WindowContentSession on settings.Transport with
//   settings.TimeProvider and settings.MaximumInputBytes, failure detail off (IncludeFailureDetail = false), whose root
//   View is `view` ("list", "log" or "notes"), serving the route of that name, with the values of arrange.model
//   (generators expanded; members it does not list keep their defaults). The notes View also serves the detail View in
//   its detail slot with the content id 0123456789abcdef0123456789abcdef.
// - IConformanceWindow.MutateAsync applies the mutations of one mutate step to the root View's model, as one
//   publication unit, or each mutation and each appended row as its own unit when eachUnit is true.
// - IConformanceWindow.ReadMember returns a member of the root View's model as its wire JSON value, for the runner to
//   compare with expect.model.
// - IConformanceWindow.Provide returns the value of a provided id (README "Identifiers") that the runner writes before
//   any message carried it, or null when the window does not know it. The corpus names the operation contract of the
//   notes View's slowImport "contract" and the contract of its confirm interaction "confirmContract".
//
// P3 serves no View route (P3ConformanceViews); P4 implements list, log, notes and detail and replaces Producer.
internal interface IConformanceViews
{
    IConformanceWindow CreateWindow(string view, JsonObject model, ConformanceWindowSettings settings);
}

internal interface IConformanceWindow : IDisposable
{
    // Throws NotSupportedException when the implementation has no test Views yet.
    Task MutateAsync(JsonArray mutations, bool eachUnit);

    // Throws NotSupportedException when the implementation has no test Views yet.
    JsonNode? ReadMember(string member);

    JsonNode? Provide(string id);
}

// What the runner gives the window: its transport (the ScenarioTransport), its virtual clock and its input limit
// (the scenario's options.inputBytes, or null for the default).
internal sealed record ConformanceWindowSettings(IBridgeTransport Transport, TimeProvider TimeProvider, int? MaximumInputBytes);

internal static class ConformanceViews
{
    // The implementation the producer scenarios run against.
    internal static IConformanceViews Producer { get; } = new P3ConformanceViews();
}

// P3: a window with no root View. The dispatcher answers released for every View route, and mutate and
// expect(model) steps are unsupported.
internal sealed class P3ConformanceViews : IConformanceViews
{
    public IConformanceWindow CreateWindow(string view, JsonObject model, ConformanceWindowSettings settings) =>
        new Window(new WindowContentSession(settings.Transport, new WindowContentSessionOptions
        {
            TimeProvider = settings.TimeProvider,
            MaximumInputBytes = settings.MaximumInputBytes,
            IncludeFailureDetail = false,
        }));

    private sealed class Window(WindowContentSession session) : IConformanceWindow
    {
        public Task MutateAsync(JsonArray mutations, bool eachUnit) =>
            throw new NotSupportedException("P3 serves no test View: mutate is unsupported.");

        public JsonNode? ReadMember(string member) =>
            throw new NotSupportedException("P3 serves no test View: expect(model) is unsupported.");

        public JsonNode? Provide(string id) => null;

        public void Dispose() => session.Dispose();
    }
}

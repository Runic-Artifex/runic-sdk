using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The generated Bridge of ViewRequest<T> and ViewQuestion<TInput, TOutput> members (specs/application, "View
// requests") in both MVVM flavors. ViewRequestCodegenFixture.cs holds the ViewModels; the project build generates and
// compiles their Bridges. Set RUNIC_UPDATE_GOLDEN=1 to rewrite the goldens after an intended change.
internal sealed class ViewRequestCodegenTests
{
    private static readonly (Type Model, string Module)[] Models =
    [
        (typeof(ReactiveViewRequestViewModel), "reactiveViewRequest"),
        (typeof(ToolkitViewRequestViewModel), "toolkitViewRequest"),
        (typeof(RequestOnlyViewModel), "requestOnly"),
    ];

    [Test]
    public void ViewRequestOutputMatchesTheGoldenFiles()
    {
        var project = ProjectDirectory();
        var bridges = BridgeDirectory();
        var outputs = Models.SelectMany(entry => new (string Golden, string Actual)[]
        {
            (entry.Module + ".ts", Path.Combine(GeneratedDirectory(), entry.Module + ".ts")),
            (entry.Module + ".mock.ts", Path.Combine(GeneratedDirectory(), entry.Module + ".mock.ts")),
            (entry.Model.Name + ".Bridge.g.cs", Path.Combine(bridges, entry.Model.FullName + ".Bridge.g.cs")),
        }).ToList();
        var shape = Path.Combine(project, "obj", "view-requests-contract-shape.txt");
        File.WriteAllText(shape, string.Join('\n', MvvmConventionGoldenTests.ModelSections(BridgeContractShape.Parts(Models[0].Model),
            Models.Select(entry => entry.Model.FullName!).ToHashSet(StringComparer.Ordinal))) + "\n");
        outputs.Add(("contract-shape.txt", shape));
        DomainFailureCodegenTests.CompareGolden(project, "view-requests", [.. outputs]);
    }

    // The generator's contract ids use BridgeContractShape's fingerprint, which classifies a ViewRequest as a request
    // and a ViewQuestion as an interaction, never as state.
    [Test]
    public void ContractShapeClassifiesViewRequestsAndQuestions()
    {
        foreach (var (model, module) in Models)
        {
            var fingerprint = BridgeContractShape.Compute(model);
            var client = File.ReadAllText(Path.Combine(GeneratedDirectory(), module + ".ts"));
            var bridge = File.ReadAllText(Path.Combine(BridgeDirectory(), model.FullName + ".Bridge.g.cs"));
            Require(client.Contains($"const bridgeContract = \"{model.FullName}:{fingerprint}\";", StringComparison.Ordinal)
                && bridge.Contains($"\"{fingerprint}\", content,", StringComparison.Ordinal),
                $"{model.Name}: the generated fingerprint differs from BridgeContractShape.Compute ({fingerprint}).");
            Require(client.Contains($"focus: {{ contract: \"{fingerprint}:request:Focus\", decodeInput: (value: unknown) => bridgeWire.object<ViewRequestFocusTarget>(", StringComparison.Ordinal)
                && bridge.Contains($"global::Runic.Application.Views.Generated.ViewRequestDescriptor.Create<global::Runic.Application.Testing.Tests.ViewRequestFocusTarget>(\"focus\", \"{fingerprint}:request:Focus\", static vm => ((global::{model.FullName})vm).Focus, input => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => FocusRequestInputCodec.Write(writer, input))),", StringComparison.Ordinal),
                $"{model.Name}: the Focus view request lost its contract or descriptor.\n{client}\n{bridge}");

            var parts = BridgeContractShape.Parts(model);
            var name = model.FullName;
            Require(parts.Contains($"member:{name}:Focus:wire:focus:request:access:read")
                && parts.Contains($"wire:{model.Name}.Focus.input:type:Runic.Application.Testing.Tests.ViewRequestFocusTarget:nullable:False"),
                $"{model.Name}.Focus is not a request with its input type:\n{string.Join('\n', parts)}");
            if (model == typeof(RequestOnlyViewModel)) continue;
            Require(parts.Contains($"member:{name}:ConfirmDiscard:wire:confirmDiscard:interaction:access:read")
                && parts.Contains($"wire:{model.Name}.ConfirmDiscard.output:type:System.Boolean:nullable:False"),
                $"{model.Name}.ConfirmDiscard is not an interaction:\n{string.Join('\n', parts)}");
            Require(client.Contains($"confirmDiscard: {{ contract: \"{fingerprint}:interaction:ConfirmDiscard\"", StringComparison.Ordinal)
                && bridge.Contains($"global::Runic.Application.Views.Generated.ViewQuestionDescriptor.Create<global::Runic.Application.Testing.Tests.ViewRequestDiscard, global::System.Boolean>(\"confirmDiscard\", \"{fingerprint}:interaction:ConfirmDiscard\", static vm => ((global::{model.FullName})vm).ConfirmDiscard,", StringComparison.Ordinal),
                $"{model.Name}: the ConfirmDiscard view question is not a generated interaction.\n{client}\n{bridge}");
            Require(!client.Contains("readonly focus: string", StringComparison.Ordinal) && !client.Contains("confirmDiscard: unknown", StringComparison.Ordinal)
                && !bridge.Contains("PropertyDescriptor(\"Focus\"", StringComparison.Ordinal),
                $"{model.Name}: a view request or question was published as state.");
        }
        // A nullable input keeps its annotation on both sides.
        Require(BridgeContractShape.Parts(typeof(ReactiveViewRequestViewModel))
                .Contains("wire:ReactiveViewRequestViewModel.Notify.input:type:System.String:nullable:True"),
            "The fingerprint lost the nullable input of a ViewRequest<string?>.");
    }

    // Only a View with view requests imports and passes their runtime (the bundle-size pattern of interactions).
    [Test]
    public void ViewsWithoutRequestsDoNotReferenceTheirRuntime()
    {
        foreach (var path in Directory.GetFiles(GeneratedDirectory(), "*.ts"))
        {
            var module = Path.GetFileName(path);
            if (module.EndsWith(".mock.ts", StringComparison.Ordinal) || module == "types.ts") continue;
            var text = File.ReadAllText(path);
            var hasRequests = Models.Any(entry => entry.Module + ".ts" == module);
            foreach (var marker in new[] { "defineRequests", "requestDefinitions", "BridgeViewRequestContext", "requests: view.requests" })
                Require(text.Contains(marker, StringComparison.Ordinal) == hasRequests,
                    $"{module} {(hasRequests ? "lacks" : "references")} '{marker}'.");
        }
    }

    // A CommunityToolkit ViewModel's ViewQuestion asks the page through the generated Bridge, with no ReactiveUI.
    [Test]
    public async Task ToolkitViewQuestionAsksThePageAsync()
    {
        var model = new ToolkitViewRequestViewModel();
        using var host = new RunicWindowTestHost<ToolkitViewRequestViewModel>(model,
            (transport, content, vm) => new ToolkitViewRequestBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "toolkitViewRequest", ViewLocator = new TestViewLocator() });
        const string route = "toolkitViewRequest";
        const string presentation = "toolkit-question";
        const string client = "toolkit-client";
        const string connection = "toolkit-connection";
        var contract = $"{BridgeContractShape.Compute(typeof(ToolkitViewRequestViewModel))}:interaction:ConfirmDiscard";
        using (var opened = JsonDocument.Parse(host.Transport.Open(route, presentation, connection, client,
            $$"""[{"name":"confirmDiscard","contract":{{JsonSerializer.Serialize(contract)}}}]""")))
            Require(opened.RootElement.GetProperty("presentation").GetString() == "ok"
                && opened.RootElement.GetProperty("registration").GetString() == "ok",
                $"The view question's handler was not registered: {opened.RootElement}");

        var command = host.Transport.CommandAsync(route, "discard", connectionKey: connection, clientKey: client).AsTask();
        using (var request = await NextInteractionAsync(host.Transport, connection))
        {
            var root = request.RootElement;
            Require(root.GetProperty("input").GetProperty("title").GetString() == "Draft",
                $"The generated bridge did not encode the view question's input: {root}");
            var reply = BridgeRequests.Json(writer =>
            {
                writer.WriteString("kind", "interaction-reply");
                writer.WriteString("route", route);
                writer.WriteString("presentation", presentation);
                writer.WriteString("requestId", root.GetProperty("requestId").GetString());
                writer.WriteNumber("ownerEpoch", root.GetProperty("ownerEpoch").GetInt64());
                writer.WriteString("name", "confirmDiscard");
                writer.WriteString("contract", contract);
                writer.WriteString("outcome", "answered");
                writer.WriteBoolean("output", true);
            });
            using var replied = JsonDocument.Parse(host.Transport.Send(reply, connection, client));
            Require(replied.RootElement.GetProperty("result").GetString() == "ok",
                $"The view question's answer was rejected: {replied.RootElement}");
        }
        using var completed = JsonDocument.Parse(await command);
        Require(completed.RootElement.GetProperty("ok").GetBoolean() && model.Title.Length == 0,
            $"The command did not receive the page's answer: {completed.RootElement}");
    }

    private static async Task<JsonDocument> NextInteractionAsync(InMemoryBridgeTransport transport, string connection)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var message in transport.TakeMessages().Where(message => message.ConnectionKey == connection))
            {
                var document = JsonDocument.Parse(message.Json);
                if (document.RootElement.GetProperty("kind").GetString() == "interaction") return document;
                document.Dispose();
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("No interaction reached the connection.");
    }

    private static string ProjectDirectory() =>
        Path.Combine(DomainFailureCodegenTests.FindWorkspaceRoot(), "tests", "dotnet", "Runic.Application.Testing.Tests");

    private static string GeneratedDirectory() => Path.Combine(ProjectDirectory(), "obj", "bridge-frontend", "generated");

    private static string BridgeDirectory()
    {
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(ViewRequestCodegenTests).Assembly)?.Configuration ?? "Debug";
        return Path.Combine(ProjectDirectory(), "obj", configuration, "net10.0", "runic-bridge");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

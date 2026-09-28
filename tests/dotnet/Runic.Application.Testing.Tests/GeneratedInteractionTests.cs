using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class GeneratedInteractionTests
{
    public static async Task RunAsync()
    {
        using var model = new GeneratedInteractionViewModel();
        using var host = new RunicWindowTestHost<GeneratedInteractionViewModel>(model, "generatedInteraction",
            (transport, content, vm) => new GeneratedInteractionBridge(transport, vm, content: content),
            new TestViewLocator());

        const string route = "generatedInteraction";
        const string presentation = "generated:interaction";
        const string client = "generated-client";
        const string connection = "generated-connection";
        Require(host.Transport.Call(route + "Mount", new(StringValue: presentation, ClientKey: client,
            ConnectionKey: connection)) == "ok", "The generated interaction root did not mount.");

        await AnswerAsync(false);
        Require(model.Attempt == 1 && model.AcceptedCount == 0,
            "A generated interaction did not preserve a false browser answer.");
        await AnswerAsync(true);
        Require(model.Attempt == 2 && model.AcceptedCount == 1,
            "A generated interaction did not deliver a typed browser answer to its command.");

        async Task AnswerAsync(bool answer)
        {
            var wait = host.Transport.CallAsync(BridgeInteractionRouter.WaitRoute,
                new(StringValue: JsonSerializer.Serialize(new
                {
                    route,
                    presentationId = presentation,
                    handlers = new[] { new { name = "confirm", contract = InteractionContract() } },
                }), ClientKey: client, ConnectionKey: connection)).AsTask();
            await Task.Yield();

            var command = host.Transport.CallAsync(route + "Ask", new(ClientKey: client,
                ConnectionKey: connection)).AsTask();
            var wireRequest = await wait.WaitAsync(TimeSpan.FromSeconds(2));
            using var request = JsonDocument.Parse(wireRequest);
            var root = request.RootElement;
            Require(root.GetProperty("kind").GetString() == "request"
                && root.GetProperty("input").GetProperty("title").GetString() == "Generated request",
                $"The generated bridge did not encode the typed interaction input: {wireRequest}");
            var reply = JsonSerializer.Serialize(new
            {
                kind = "answered",
                requestId = root.GetProperty("requestId").GetString(),
                route,
                presentationId = presentation,
                ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
                name = "confirm",
                contract = InteractionContract(),
                output = answer,
            });
            Require(Kind(host.Transport.Call(BridgeInteractionRouter.ReplyRoute,
                new(StringValue: reply, ClientKey: client, ConnectionKey: connection))) == "ok",
                "The generated interaction reply was rejected.");
            using var completed = JsonDocument.Parse(await command);
            Require(completed.RootElement.GetProperty("ok").GetBoolean(),
                "The command awaiting a generated interaction did not complete.");
        }

        static string InteractionContract() =>
            $"{BridgeContractShape.Compute(typeof(GeneratedInteractionViewModel))}:interaction:Confirm";
    }

    private static string Kind(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("kind").GetString()!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

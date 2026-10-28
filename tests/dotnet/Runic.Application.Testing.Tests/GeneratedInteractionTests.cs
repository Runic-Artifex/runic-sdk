using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class GeneratedInteractionTests
{
    [Test]
    public void InteractionMountsAreBoundedPerOwner()
    {
        using var model = new GeneratedInteractionViewModel();
        using var host = new RunicWindowTestHost<GeneratedInteractionViewModel>(model,
            (transport, content, vm) => new GeneratedInteractionBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "generatedInteraction", ViewLocator = new TestViewLocator() });
        // A connection holds at most 64 presentations per route; another open is rejected with capacity.
        string Open(string presentation, string connection = "connection")
        {
            using var reply = JsonDocument.Parse(host.Transport.Open("generatedInteraction", presentation, connection, "client"));
            var root = reply.RootElement;
            return root.GetProperty("ok").GetBoolean() ? root.GetProperty("presentation").GetString()!
                : root.GetProperty("error").GetProperty("reason").GetString()!;
        }
        for (var index = 0; index < 64; index++)
            Require(Open($"page-{index}") == "ok", $"Presentation {index} did not open.");
        Require(Open("page-64") == "capacity", "A 65th interaction presentation of one owner was opened.");
        Require(Open("page-0") == "ok", "An exact repeat of an open presentation was refused.");
        Require(Open("page-64", "other") == "ok", "Another connection's first interaction presentation was limited.");
    }

    [Test]
    [Skip("P5: needs the interaction pushes and interaction-reply served")]
    public async Task GeneratedInteractionDeliversTypedBrowserAnswersAsync()
    {
        using var model = new GeneratedInteractionViewModel();
        using var host = new RunicWindowTestHost<GeneratedInteractionViewModel>(model,
            (transport, content, vm) => new GeneratedInteractionBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "generatedInteraction", ViewLocator = new TestViewLocator() });

        const string route = "generatedInteraction";
        const string presentation = "generated-interaction";
        const string client = "generated-client";
        const string connection = "generated-connection";
        using (var opened = JsonDocument.Parse(host.Transport.Open(route, presentation, connection, client,
            $$"""[{"name":"confirm","contract":{{JsonSerializer.Serialize(InteractionContract())}}}]""")))
            Require(opened.RootElement.GetProperty("presentation").GetString() == "ok"
                && opened.RootElement.GetProperty("registration").GetString() == "ok",
                $"The generated interaction root did not open with its handler: {opened.RootElement}");

        await AnswerAsync(false);
        Require(model.Attempt == 1 && model.AcceptedCount == 0,
            "A generated interaction did not preserve a false browser answer.");
        await AnswerAsync(true);
        Require(model.Attempt == 2 && model.AcceptedCount == 1,
            "A generated interaction did not deliver a typed browser answer to its command.");

        async Task AnswerAsync(bool answer)
        {
            var command = host.Transport.CommandAsync(route, "ask", connectionKey: connection, clientKey: client).AsTask();
            using var request = await NextInteractionAsync(host.Transport, connection);
            var root = request.RootElement;
            Require(root.GetProperty("input").GetProperty("title").GetString() == "Generated request",
                $"The generated bridge did not encode the typed interaction input: {root}");
            var reply = BridgeRequests.Json(writer =>
            {
                writer.WriteString("kind", "interaction-reply");
                writer.WriteString("route", route);
                writer.WriteString("presentation", presentation);
                writer.WriteString("requestId", root.GetProperty("requestId").GetString());
                writer.WriteNumber("ownerEpoch", root.GetProperty("ownerEpoch").GetInt64());
                writer.WriteString("name", "confirm");
                writer.WriteString("contract", InteractionContract());
                writer.WriteString("outcome", "answered");
                writer.WriteBoolean("output", answer);
            });
            using (var replied = JsonDocument.Parse(host.Transport.Send(reply, connection, client)))
                Require(replied.RootElement.GetProperty("result").GetString() == "ok",
                    $"The generated interaction reply was rejected: {replied.RootElement}");
            using var completed = JsonDocument.Parse(await command);
            Require(completed.RootElement.GetProperty("ok").GetBoolean(),
                "The command awaiting a generated interaction did not complete.");
        }

        static string InteractionContract() =>
            $"{BridgeContractShape.Compute(typeof(GeneratedInteractionViewModel))}:interaction:Confirm";
    }

    // The next interaction push to the connection; the messages before it are taken.
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

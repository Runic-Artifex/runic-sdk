using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Runs alone: it counts ChildView's static lifetimes (AssemblyInfo.cs).
[NotInParallel]
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
            $"{BridgeRequests.GeneratedFingerprint(typeof(GeneratedInteractionViewModel))}:interaction:Confirm";
    }

    // sdk#1146: [RunicInteractionTimeout] reaches the generated descriptor. Without a deadline the page receives no
    // expiresAt (its connection lists open-ended-interactions/1) and the command waits however long the answer takes;
    // fifteen minutes outlast the default two and then end the command's question.
    [Test]
    public async Task GeneratedInteractionsKeepTheirDeclaredLifetimesAsync()
    {
        using var model = new GeneratedInteractionViewModel();
        using var host = new RunicWindowTestHost<GeneratedInteractionViewModel>(model,
            (transport, content, vm) => new GeneratedInteractionBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "generatedInteraction", ViewLocator = new TestViewLocator() });
        var clock = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)host.Time;
        const string route = "generatedInteraction";
        const string presentation = "generated-interaction";
        const string client = "generated-client";
        const string connection = "generated-connection";
        var fingerprint = BridgeRequests.GeneratedFingerprint(typeof(GeneratedInteractionViewModel));
        // A current page's hello: BridgeRequests' own lists no features.
        Require(host.Transport.Request($$"""{"kind":"hello","protocol":4,"session":"s-lifetimes","client":{"name":"t","version":"1"},"features":["open-ended-interactions/1"]}""",
            connection, client).StartsWith("{\"ok\":true", StringComparison.Ordinal), "The hello was refused.");
        using (var opened = JsonDocument.Parse(host.Transport.Open(route, presentation, connection, client,
            $$"""[{"name":"confirmWithoutDeadline","contract":"{{fingerprint}}:interaction:ConfirmWithoutDeadline"},{"name":"confirmSlowly","contract":"{{fingerprint}}:interaction:ConfirmSlowly"}]""")))
            Require(opened.RootElement.GetProperty("registration").GetString() == "ok", $"The handlers were not registered: {opened.RootElement}");

        var unbounded = host.Transport.CommandAsync(route, "askWithoutDeadline", connectionKey: connection, clientKey: client).AsTask();
        using (var request = await NextInteractionAsync(host.Transport, connection))
        {
            var root = request.RootElement;
            Require(!root.TryGetProperty("expiresAt", out _), $"The interaction without a deadline named one: {root}");
            clock.Advance(TimeSpan.FromDays(3));
            Require(!unbounded.IsCompleted, "The command's question without a deadline ended.");
            var reply = BridgeRequests.Json(writer =>
            {
                writer.WriteString("kind", "interaction-reply");
                writer.WriteString("route", route);
                writer.WriteString("presentation", presentation);
                writer.WriteString("requestId", root.GetProperty("requestId").GetString());
                writer.WriteNumber("ownerEpoch", root.GetProperty("ownerEpoch").GetInt64());
                writer.WriteString("name", "confirmWithoutDeadline");
                writer.WriteString("contract", $"{fingerprint}:interaction:ConfirmWithoutDeadline");
                writer.WriteString("outcome", "answered");
                writer.WriteBoolean("output", true);
            });
            using (var replied = JsonDocument.Parse(host.Transport.Send(reply, connection, client)))
                Require(replied.RootElement.GetProperty("result").GetString() == "ok", $"The late answer was rejected: {replied.RootElement}");
        }
        using (var completed = JsonDocument.Parse(await unbounded.WaitScaledAsync(TimeSpan.FromSeconds(5))))
            Require(completed.RootElement.GetProperty("ok").GetBoolean() && model.AcceptedCount == 1,
                $"The command did not receive the late answer: {completed.RootElement}");

        var slow = host.Transport.CommandAsync(route, "askSlowly", connectionKey: connection, clientKey: client).AsTask();
        using (var request = await NextInteractionAsync(host.Transport, connection))
        {
            var sent = clock.GetUtcNow();
            Require(DateTimeOffset.Parse(request.RootElement.GetProperty("expiresAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                == sent.AddMinutes(15), $"The deadline is not fifteen minutes away: {request.RootElement}");
        }
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Require(!slow.IsCompleted, "The fifteen-minute question ended after the default two minutes.");
        clock.Advance(TimeSpan.FromMinutes(13));
        using (var failed = JsonDocument.Parse(await slow.WaitScaledAsync(TimeSpan.FromSeconds(5))))
            Require(!failed.RootElement.GetProperty("ok").GetBoolean(), $"The command did not fail when its question timed out: {failed.RootElement}");
        Require(model.AcceptedCount == 1, "The timed-out question was counted as answered.");
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

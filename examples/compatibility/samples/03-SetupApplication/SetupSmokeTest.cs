using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Runic.Application.Bridge;
using Runic.Examples.Setup.Contract;

namespace Runic.Examples.Setup;

internal static class SetupSmokeTest
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            await VerifyCompletionAsync().ConfigureAwait(false);
            await VerifyCancellationAsync().ConfigureAwait(false);
            await VerifyFailureAsync().ConfigureAwait(false);
            Console.WriteLine("Setup Application Bridge completion, cancellation, failure, and recovery passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task VerifyCompletionAsync()
    {
        await using var session = new ApplicationBridgeSession(
            new SetupBridgeDispatcher(new SetupBridgeHandler()));
        DestinationSelected destination = await PrepareAsync(session).ConfigureAwait(false);
        var terminal = new TaskCompletionSource<BridgeHostEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<BridgeHostEnvelope>();
        session.EventProduced += (_, message) =>
        {
            events.Add(message);
            if (message.Payload.GetProperty("_tag").GetString() == "OperationCompleted")
                terminal.TrySetResult(message);
        };
        BridgeHostEnvelope started = await session.DispatchAsync(Envelope(
            "dispatch",
            session.Id.Value,
            session.Revision,
            $$"""{"_tag":"StartInstallation","destinationSelectionId":"{{destination.Destination.SelectionId}}","selectedFeatures":["core","examples"]}""")).ConfigureAwait(false);
        Guid operationId = started.Payload.GetProperty("operationId").GetGuid();
        BridgeHostEnvelope completed = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal(operationId, completed.OperationId);
        True(events.Count(message => message.Payload.GetProperty("_tag").GetString() == "OperationProgress") == 5);

        BridgeHostEnvelope recovered = await session.DispatchAsync(Envelope(
            "initialize", null, null, """{"_tag":"InitializeApplication"}""")).ConfigureAwait(false);
        Equal("Complete", recovered.Payload.GetProperty("viewId").GetString());
        True(!recovered.Payload.TryGetProperty("activeOperationId", out _));
    }

    private static async Task VerifyCancellationAsync()
    {
        await using var session = new ApplicationBridgeSession(
            new SetupBridgeDispatcher(new SetupBridgeHandler()));
        DestinationSelected destination = await PrepareAsync(session).ConfigureAwait(false);
        var cancelled = new TaskCompletionSource<BridgeHostEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) =>
        {
            if (message.Payload.GetProperty("_tag").GetString() == "OperationCancelled")
                cancelled.TrySetResult(message);
        };
        BridgeHostEnvelope started = await session.DispatchAsync(Envelope(
            "dispatch",
            session.Id.Value,
            session.Revision,
            $$"""{"_tag":"StartInstallation","destinationSelectionId":"{{destination.Destination.SelectionId}}","selectedFeatures":["core"]}""")).ConfigureAwait(false);
        Guid operationId = started.Payload.GetProperty("operationId").GetGuid();
        BridgeHostEnvelope accepted = await session.DispatchAsync(Envelope(
            "cancelOperation",
            session.Id.Value,
            session.Revision,
            $$"""{"operationId":"{{operationId}}"}""")).ConfigureAwait(false);
        True(accepted.Payload.GetProperty("accepted").GetBoolean());
        BridgeHostEnvelope terminal = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal(operationId, terminal.OperationId);
    }

    private static async Task VerifyFailureAsync()
    {
        await using var session = new ApplicationBridgeSession(
            new SetupBridgeDispatcher(new SetupBridgeHandler(failInstallation: true)));
        DestinationSelected destination = await PrepareAsync(session).ConfigureAwait(false);
        var failed = new TaskCompletionSource<BridgeHostEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) =>
        {
            if (message.Payload.GetProperty("_tag").GetString() == "OperationFailed")
                failed.TrySetResult(message);
        };
        _ = await session.DispatchAsync(Envelope(
            "dispatch",
            session.Id.Value,
            session.Revision,
            $$"""{"_tag":"StartInstallation","destinationSelectionId":"{{destination.Destination.SelectionId}}","selectedFeatures":["core"]}""")).ConfigureAwait(false);
        BridgeHostEnvelope terminal = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal("The simulated package verification failed.", terminal.Payload.GetProperty("error").GetString());
    }

    private static async Task<DestinationSelected> PrepareAsync(ApplicationBridgeSession session)
    {
        BridgeHostEnvelope initialized = await session.DispatchAsync(Envelope(
            "initialize", null, null, """{"_tag":"InitializeApplication"}""")).ConfigureAwait(false);
        Equal("Welcome", initialized.Payload.GetProperty("viewId").GetString());
        _ = await session.DispatchAsync(Envelope(
            "dispatch", session.Id.Value, 0, """{"_tag":"Navigate","target":"Destination","expectedRevision":0}""")).ConfigureAwait(false);
        BridgeHostEnvelope selected = await session.DispatchAsync(Envelope(
            "dispatch", session.Id.Value, 1, """{"_tag":"SelectDestination"}""")).ConfigureAwait(false);
        _ = await session.DispatchAsync(Envelope(
            "dispatch", session.Id.Value, 2, """{"_tag":"Navigate","target":"Features","expectedRevision":2}""")).ConfigureAwait(false);
        return new DestinationSelected
        {
            Tag = "DestinationSelected",
            Destination = new DestinationSelectedDestination
            {
                SelectionId = selected.Payload.GetProperty("destination").GetProperty("selectionId").GetGuid(),
                DisplayName = selected.Payload.GetProperty("destination").GetProperty("displayName").GetString()!,
                AvailableBytes = selected.Payload.GetProperty("destination").GetProperty("availableBytes").GetInt64(),
            },
            Revision = selected.Revision,
        };
    }

    private static BridgeClientEnvelope Envelope(
        string kind,
        Guid? sessionId,
        long? expectedRevision,
        string payload) => new()
        {
            Protocol = SetupBridgeContract.ProtocolIdentity,
            Version = SetupBridgeContract.ProtocolVersion,
            ContractFingerprint = SetupBridgeContract.Fingerprint,
            ConnectionEpoch = 0,
            Kind = kind,
            CommandId = Guid.NewGuid(),
            SessionId = sessionId,
            ExpectedRevision = expectedRevision,
            Payload = JsonDocument.Parse(payload).RootElement.Clone(),
        };

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("The Setup acceptance condition was false.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}

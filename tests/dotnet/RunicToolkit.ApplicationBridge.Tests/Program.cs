using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Setup.Contract;

namespace RunicToolkit.ApplicationBridge.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("generated named commands initialize and mutate an authoritative session", SessionRoundTrip),
            ("duplicates, stale revisions, and stale sessions are rejected", Rejections),
            ("the duplicate-command ledger remains bounded at capacity", CommandLedgerCapacity),
            ("operations publish progress and support explicit cancellation", OperationLifecycle),
            ("the strict codec rejects unknown and oversized input", CodecLimits),
            ("generated contract readers reject unknown and duplicate fields", GeneratedStrictness),
            ("generated contract identity and fingerprints are embedded", GeneratedMetadata),
        ];
        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"ok - {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"not ok - {name}");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
        return 0;
    }

    private static async Task SessionRoundTrip()
    {
        var handler = new SetupHandler();
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(handler));
        byte[] fixture = await File.ReadAllBytesAsync(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "initialize.client.json"));
        True(ApplicationBridgeCodec.TryDecodeClient(fixture, out BridgeClientEnvelope? request));
        BridgeHostEnvelope initialized = await session.DispatchAsync(request!).ConfigureAwait(false);
        Equal("snapshot", initialized.Kind);
        Equal("Welcome", initialized.Payload.GetProperty("viewId").GetString());
        Equal(session.Id.Value, initialized.SessionId);
        Equal(0L, initialized.Revision);

        BridgeHostEnvelope selected = await session.DispatchAsync(Envelope(
            "dispatch",
            Guid.Parse("00000000-0000-4000-8000-000000000002"),
            session.Id.Value,
            0,
            """{"_tag":"SelectDestination"}""")).ConfigureAwait(false);
        Equal("receipt", selected.Kind);
        Equal("DestinationSelected", selected.Payload.GetProperty("_tag").GetString());
        Equal(1L, selected.Revision);
        Equal(2L, selected.Sequence);
    }

    private static async Task Rejections()
    {
        var handler = new SetupHandler();
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(handler));
        Guid command = Guid.Parse("00000000-0000-4000-8000-000000000011");
        _ = await session.DispatchAsync(Envelope("initialize", command, null, null, """{"_tag":"InitializeApplication"}"""));
        BridgeHostEnvelope duplicate = await session.DispatchAsync(Envelope("initialize", command, null, null, """{"_tag":"InitializeApplication"}"""));
        Equal("CommandRejected", duplicate.Payload.GetProperty("_tag").GetString());

        BridgeHostEnvelope stale = await session.DispatchAsync(Envelope(
            "dispatch",
            Guid.Parse("00000000-0000-4000-8000-000000000012"),
            session.Id.Value,
            99,
            """{"_tag":"Navigate","target":"Destination","expectedRevision":99}"""));
        Equal("StaleRevision", stale.Payload.GetProperty("_tag").GetString());

        BridgeHostEnvelope wrongSession = await session.DispatchAsync(Envelope(
            "dispatch",
            Guid.Parse("00000000-0000-4000-8000-000000000013"),
            Guid.NewGuid(),
            0,
            """{"_tag":"Navigate","target":"Destination","expectedRevision":0}"""));
        Equal("CommandRejected", wrongSession.Payload.GetProperty("_tag").GetString());
    }

    private static async Task OperationLifecycle()
    {
        var handler = new SetupHandler();
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(handler));
        _ = await session.DispatchAsync(Envelope(
            "initialize",
            Guid.Parse("00000000-0000-4000-8000-000000000021"),
            null,
            null,
            """{"_tag":"InitializeApplication"}"""));
        var events = new List<BridgeHostEnvelope>();
        session.EventProduced += (_, message) => events.Add(message);
        Guid destination = Guid.Parse("11111111-1111-4111-8111-111111111111");
        BridgeHostEnvelope started = await session.DispatchAsync(Envelope(
            "dispatch",
            Guid.Parse("00000000-0000-4000-8000-000000000022"),
            session.Id.Value,
            0,
            $$"""{"_tag":"StartInstallation","destinationSelectionId":"{{destination}}","selectedFeatures":["core"]}"""));
        Guid operationId = started.Payload.GetProperty("operationId").GetGuid();
        Equal(operationId, started.OperationId);
        await handler.ProgressPublished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        True(events.Any(message => message.Payload.GetProperty("_tag").GetString() == "OperationProgress"));

        BridgeHostEnvelope cancelled = await session.DispatchAsync(Envelope(
            "cancelOperation",
            Guid.Parse("00000000-0000-4000-8000-000000000023"),
            session.Id.Value,
            session.Revision,
            $$"""{"operationId":"{{operationId}}"}"""));
        True(cancelled.Payload.GetProperty("accepted").GetBoolean());
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task CommandLedgerCapacity()
    {
        var limits = new BridgeLimits { MaxPendingCommands = 1, MaxCommandLedgerEntries = 2 };
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()), limits);
        Guid first = Guid.Parse("00000000-0000-4000-8000-000000000061");
        _ = await session.DispatchAsync(Envelope(
            "initialize", first, null, null, """{"_tag":"InitializeApplication"}"""));
        _ = await session.DispatchAsync(Envelope(
            "uiReady",
            Guid.Parse("00000000-0000-4000-8000-000000000062"),
            session.Id.Value,
            null,
            """{}"""));

        BridgeHostEnvelope full = await session.DispatchAsync(Envelope(
            "uiRendered",
            Guid.Parse("00000000-0000-4000-8000-000000000063"),
            session.Id.Value,
            null,
            """{}"""));
        Equal("CommandRejected", full.Payload.GetProperty("_tag").GetString());
        True(full.Payload.GetProperty("message").GetString()!.Contains("ledger is full", StringComparison.Ordinal));

        BridgeHostEnvelope duplicate = await session.DispatchAsync(Envelope(
            "initialize", first, null, null, """{"_tag":"InitializeApplication"}"""));
        Equal("CommandRejected", duplicate.Payload.GetProperty("_tag").GetString());
        True(duplicate.Payload.GetProperty("message").GetString()!.Contains("already been processed", StringComparison.Ordinal));
    }

    private static Task CodecLimits()
    {
        byte[] valid = Encoding.UTF8.GetBytes("""{"protocol":"runic.artifex.setup","version":1,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{"_tag":"InitializeApplication"}}""");
        True(ApplicationBridgeCodec.TryDecodeClient(valid, out BridgeClientEnvelope? decoded));
        Equal("initialize", decoded!.Kind);
        byte[] unknown = Encoding.UTF8.GetBytes("""{"protocol":"runic.artifex.setup","version":1,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{},"rawFrame":"secret"}""");
        False(ApplicationBridgeCodec.TryDecodeClient(unknown, out _));
        byte[] duplicate = Encoding.UTF8.GetBytes("""{"protocol":"runic.artifex.setup","protocol":"attacker","version":1,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{}}""");
        False(ApplicationBridgeCodec.TryDecodeClient(duplicate, out _));
        byte[] longString = Encoding.UTF8.GetBytes(
            """{"protocol":"runic.artifex.setup","version":1,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{"value":"VALUE"}}"""
                .Replace("VALUE", new string('x', 65), StringComparison.Ordinal));
        False(ApplicationBridgeCodec.TryDecodeClient(longString, out _, new BridgeLimits { MaxStringBytes = 64 }));
        False(ApplicationBridgeCodec.TryDecodeClient(new byte[2048], out _, new BridgeLimits { MaxFrameBytes = 1024 }));

        byte[] hostFrame = ApplicationBridgeCodec.EncodeHost(new BridgeHostEnvelope
        {
            Protocol = "runic.artifex.setup",
            Version = 1,
            Kind = "event",
            SessionId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Sequence = 1,
            Revision = 0,
            Payload = JsonDocument.Parse("""{"_tag":"NavigationChanged","revision":0,"view":"Welcome"}""").RootElement.Clone(),
        });
        string encodedHostFrame = Encoding.UTF8.GetString(hostFrame);
        False(encodedHostFrame.Contains("\"commandId\"", StringComparison.Ordinal));
        False(encodedHostFrame.Contains("\"operationId\"", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task GeneratedStrictness()
    {
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()));
        BridgeHostEnvelope unknown = await session.DispatchAsync(Envelope(
            "initialize",
            Guid.Parse("00000000-0000-4000-8000-000000000041"),
            null,
            null,
            """{"_tag":"InitializeApplication","hidden":"value"}"""));
        Equal("CommandRejected", unknown.Payload.GetProperty("_tag").GetString());
        BridgeHostEnvelope duplicate = await session.DispatchAsync(Envelope(
            "initialize",
            Guid.Parse("00000000-0000-4000-8000-000000000042"),
            null,
            null,
            """{"_tag":"InitializeApplication","_tag":"InitializeApplication"}"""));
        Equal("CommandRejected", duplicate.Payload.GetProperty("_tag").GetString());
    }

    private static Task GeneratedMetadata()
    {
        var dispatcher = new SetupBridgeDispatcher(new SetupHandler());
        Equal("runic.artifex.setup", dispatcher.ProtocolIdentity);
        Equal(1, dispatcher.ProtocolVersion);
        Equal(64, dispatcher.ManifestFingerprint.Length);
        return Task.CompletedTask;
    }

    private static BridgeClientEnvelope Envelope(
        string kind,
        Guid commandId,
        Guid? sessionId,
        long? revision,
        string payload) => new()
        {
            Protocol = "runic.artifex.setup",
            Version = 1,
            Kind = kind,
            CommandId = commandId,
            SessionId = sessionId,
            ExpectedRevision = revision,
            Payload = JsonDocument.Parse(payload).RootElement.Clone(),
        };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
}

internal sealed class SetupHandler : ISetupBridgeHandler
{
    internal TaskCompletionSource ProgressPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ApplicationInitialized> InitializeApplicationAsync(
        InitializeApplication command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ApplicationInitialized
        {
            Tag = "ApplicationInitialized",
            Snapshot = Snapshot<ApplicationInitializedSnapshot>(0),
        });

    public ValueTask<DestinationSelected> SelectDestinationAsync(
        SelectDestination command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DestinationSelected
        {
            Tag = "DestinationSelected",
            Destination = new DestinationSelectedDestination
            {
                SelectionId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
                DisplayName = "Recommended destination",
                AvailableBytes = 1_000_000,
            },
            Revision = context.CurrentRevision + 1,
        });

    public ValueTask<NavigationAccepted> NavigateAsync(
        Navigate command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new NavigationAccepted
        {
            Tag = "NavigationAccepted",
            Snapshot = Snapshot<NavigationAcceptedSnapshot>(context.CurrentRevision + 1),
        });

    public ValueTask<InstallationStarted> StartInstallationAsync(
        StartInstallation command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        BridgeOperationId operation = context.Operations.Start(async (id, token) =>
        {
            await context.Events.PublishOperationProgressAsync(new OperationProgress
            {
                Tag = "OperationProgress",
                OperationId = id.Value,
                Completed = 1,
                Total = 2,
            }, operationId: id, cancellationToken: token).ConfigureAwait(false);
            ProgressPublished.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await context.Events.PublishOperationCancelledAsync(new OperationCancelledEvent
                {
                    Tag = "OperationCancelled",
                    OperationId = id.Value,
                    Revision = context.CurrentRevision + 1,
                }, operationId: id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                Cancelled.TrySetResult();
                throw;
            }
        }, cancellationToken);
        return ValueTask.FromResult(new InstallationStarted
        {
            Tag = "InstallationStarted",
            CommandId = context.CommandId.Value,
            OperationId = operation.Value,
            Revision = context.CurrentRevision + 1,
        });
    }

    public ValueTask<OperationCancellationAccepted> CancelOperationAsync(
        CancelOperation command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OperationCancellationAccepted
        {
            Tag = "OperationCancellationAccepted",
            OperationId = command.OperationId,
            Accepted = false,
            Revision = context.CurrentRevision,
        });

    private static T Snapshot<T>(long revision) where T : class
    {
        object value = typeof(T) == typeof(ApplicationInitializedSnapshot)
            ? new ApplicationInitializedSnapshot { ViewId = "Welcome", Revision = revision, SelectedFeatures = [], CanNavigateBack = false, CanNavigateNext = true }
            : new NavigationAcceptedSnapshot { ViewId = "Destination", Revision = revision, SelectedFeatures = [], CanNavigateBack = true, CanNavigateNext = true };
        return (T)value;
    }
}

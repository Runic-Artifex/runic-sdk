using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Bridge;
using Runic.Application.Setup.Contract;

namespace Runic.Application.Bridge.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("generated named commands initialize and mutate an authoritative session", SessionRoundTrip),
            ("duplicates, stale revisions, and stale sessions are rejected", Rejections),
            ("revision comparison and advancement are atomic across admitted commands", AtomicRevisionAdvance),
            ("reconnect admission follows an old mutating command and external publish", ReconnectInterleaving),
            ("pending-capacity rejection does not consume a command identifier", PendingCapacityRetry),
            ("a full event budget rejects mutation before its handler runs", FullEventBudgetRejectsMutation),
            ("staged event payloads survive their source document disposal", StagedPayloadOwnership),
            ("initialize publication follows its snapshot in the same epoch", InitializePublication),
            ("event subscribers can reenter dispatch without a revision deadlock", ReentrantSubscriber),
            ("a staged command event permits synchronous reentrant dispatch", ReentrantCommandEvent),
            ("synchronous subscribers can publish into the bounded event queue", SynchronousReentrantPublish),
            ("contract fingerprints and reconnect epochs are enforced", HandshakeEnforcement),
            ("higher-epoch initialization failures echo the requested epoch without commit", HigherEpochInitializeFailure),
            ("transport-neutral conformance fixtures carry paired reconnect epochs", ConformanceFixtures),
            ("the duplicate-command ledger remains bounded at capacity", CommandLedgerCapacity),
            ("operations publish progress and support explicit cancellation", OperationLifecycle),
            ("non-cancellable operations reject explicit cancellation", NonCancellableOperation),
            ("session shutdown owns operation cancellation without disposal races", OperationShutdownOwnership),
            ("the strict codec rejects unknown and oversized input", CodecLimits),
            ("generated contract readers reject unknown and duplicate fields", GeneratedStrictness),
            ("optional conversions distinguish missing from explicit null", OptionalConversions),
            ("declared application errors cross the session as typed payloads", DeclaredApplicationError),
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

    private static Task OptionalConversions()
    {
        BridgeOptional<string> missing = (string?)null;
        True(!missing.HasValue);

        BridgeOptional<string> explicitNull = new(null);
        True(explicitNull.HasValue);
        Equal<string?>(null, explicitNull.Value);

        BridgeOptional<string> present = "value";
        True(present.HasValue);
        Equal("value", present.Value);
        return Task.CompletedTask;
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

    private static async Task DeclaredApplicationError()
    {
        await using var session = new ApplicationBridgeSession(new FailingDispatcher());
        BridgeHostEnvelope response = await session.DispatchAsync(Envelope(
            "initialize",
            Guid.Parse("00000000-0000-4000-8000-000000000099"),
            null,
            null,
            """{"_tag":"InitializeApplication"}""")).ConfigureAwait(false);
        Equal("error", response.Kind);
        Equal(0L, response.Sequence);
        Equal("QuotaExceeded", response.Payload.GetProperty("_tag").GetString());
        Equal(2L, response.Payload.GetProperty("limit").GetInt64());
    }

    private static async Task AtomicRevisionAdvance()
    {
        var dispatcher = new BlockingMutationDispatcher();
        await using var session = new ApplicationBridgeSession(dispatcher);
        _ = await session.DispatchAsync(Envelope(
            "initialize", Guid.Parse("00000000-0000-4000-8000-000000000014"), null, null,
            """{"_tag":"InitializeApplication"}"""));
        var events = new List<BridgeHostEnvelope>();
        var eventProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) =>
        {
            events.Add(message);
            eventProduced.TrySetResult();
        };
        Task<BridgeHostEnvelope> first = session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000015"), session.Id.Value, 0,
            """{"_tag":"Mutate"}""")).AsTask();
        await dispatcher.MutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Task<BridgeHostEnvelope> second = session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000016"), session.Id.Value, 0,
            """{"_tag":"Mutate"}""")).AsTask();
        dispatcher.ReleaseMutation.SetResult();
        Equal("receipt", (await first.ConfigureAwait(false)).Kind);
        BridgeHostEnvelope stale = await second.ConfigureAwait(false);
        Equal("StaleRevision", stale.Payload.GetProperty("_tag").GetString());
        Equal(1, dispatcher.Mutations);
        await eventProduced.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(1, events.Count);
        Equal(2L, session.Revision);
    }

    private static async Task HandshakeEnforcement()
    {
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()));
        BridgeHostEnvelope mismatch = await session.DispatchAsync(new BridgeClientEnvelope
        {
            Protocol = "runic.artifex.setup", Version = 1, ContractFingerprint = new('0', 64), ConnectionEpoch = 0,
            Kind = "initialize", CommandId = Guid.Parse("00000000-0000-4000-8000-000000000017"),
            Payload = JsonDocument.Parse("""{"_tag":"InitializeApplication"}""").RootElement.Clone(),
        });
        Equal("ProtocolVersionMismatch", mismatch.Payload.GetProperty("_tag").GetString());
        _ = await session.DispatchAsync(Envelope(
            "initialize", Guid.Parse("00000000-0000-4000-8000-000000000018"), null, null,
            """{"_tag":"InitializeApplication"}"""));
        BridgeHostEnvelope staleConnection = await session.DispatchAsync(Envelope(
            "initialize", Guid.Parse("00000000-0000-4000-8000-000000000019"), null, null,
            """{"_tag":"InitializeApplication"}""", connectionEpoch: 1));
        Equal("snapshot", staleConnection.Kind);
        Equal(1L, staleConnection.Sequence);
        BridgeHostEnvelope oldConnection = await session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000020"), session.Id.Value, 0,
            """{"_tag":"Navigate","target":"Destination","expectedRevision":0}"""));
        Equal("CommandRejected", oldConnection.Payload.GetProperty("_tag").GetString());
    }

    private static async Task ReconnectInterleaving()
    {
        var dispatcher = new BlockingMutationDispatcher();
        await using var session = new ApplicationBridgeSession(dispatcher);
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000081"), null, null, """{"_tag":"InitializeApplication"}"""));
        Task<BridgeHostEnvelope> oldCommand = session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000082"), session.Id.Value, 0, """{"_tag":"Mutate"}""")).AsTask();
        await dispatcher.MutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var externalPublished = new TaskCompletionSource<BridgeHostEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) =>
        {
            if (message.Payload.GetProperty("_tag").GetString() == "External") externalPublished.TrySetResult(message);
        };
        Task external = session.PublishAsync(new(JsonDocument.Parse("""{"_tag":"External"}""").RootElement.Clone())).AsTask();
        dispatcher.ReleaseMutation.TrySetResult();
        Equal(0L, (await oldCommand).ConnectionEpoch);
        await external;
        Equal(0L, (await externalPublished.Task.WaitAsync(TimeSpan.FromSeconds(2))).ConnectionEpoch);
        BridgeHostEnvelope snapshot = await session.DispatchAsync(Envelope(
            "initialize", Guid.Parse("00000000-0000-4000-8000-000000000083"), null, null, """{"_tag":"InitializeApplication"}""", connectionEpoch: 1));
        Equal("snapshot", snapshot.Kind);
        Equal(1L, snapshot.ConnectionEpoch);
        Equal(1L, snapshot.Sequence);
    }

    private static async Task PendingCapacityRetry()
    {
        var dispatcher = new BlockingMutationDispatcher();
        await using var session = new ApplicationBridgeSession(dispatcher, new BridgeLimits { MaxPendingCommands = 1 });
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000085"), null, null, """{"_tag":"InitializeApplication"}"""));
        Task<BridgeHostEnvelope> occupying = session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000086"), session.Id.Value, 0, """{"_tag":"Mutate"}""")).AsTask();
        await dispatcher.MutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Guid retryId = Guid.Parse("00000000-0000-4000-8000-000000000087");
        Task<BridgeHostEnvelope> rejection = session.DispatchAsync(Envelope("dispatch", retryId, session.Id.Value, 0, """{"_tag":"Mutate"}""")).AsTask();
        BridgeHostEnvelope rejected = await rejection.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(0L, rejected.Sequence);
        Equal("CommandRejected", rejected.Payload.GetProperty("_tag").GetString());
        dispatcher.ReleaseMutation.TrySetResult();
        _ = await occupying;
        await Task.Delay(20);
        BridgeHostEnvelope retry = await session.DispatchAsync(Envelope("dispatch", retryId, session.Id.Value, session.Revision, """{"_tag":"Mutate"}"""));
        Equal("receipt", retry.Kind);
    }

    private static async Task FullEventBudgetRejectsMutation()
    {
        var dispatcher = new BlockingMutationDispatcher();
        await using var session = new ApplicationBridgeSession(dispatcher, new BridgeLimits { MaxPendingCommands = 1 });
        using var pumpGate = new ManualResetEventSlim(false);
        session.EventProduced += (_, _) => pumpGate.Wait(TimeSpan.FromSeconds(2));
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000088"), null, null, """{"_tag":"InitializeApplication"}"""));
        await session.PublishAsync(new(JsonDocument.Parse("""{"_tag":"Queued"}""").RootElement.Clone()));
        BridgeHostEnvelope rejected = await session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000089"), session.Id.Value, 0, """{"_tag":"Mutate"}"""));
        Equal("CommandRejected", rejected.Payload.GetProperty("_tag").GetString());
        Equal(0, dispatcher.Mutations);
        pumpGate.Set();
    }

    private static async Task StagedPayloadOwnership()
    {
        await using var session = new ApplicationBridgeSession(new DisposedPayloadDispatcher());
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000090"), null, null, """{"_tag":"InitializeApplication"}"""));
        var produced = new TaskCompletionSource<BridgeHostEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) => produced.TrySetResult(message);
        _ = await session.DispatchAsync(Envelope("dispatch", Guid.Parse("00000000-0000-4000-8000-000000000091"), session.Id.Value, 0, """{"_tag":"Mutate"}"""));
        Equal("Owned", (await produced.Task.WaitAsync(TimeSpan.FromSeconds(2))).Payload.GetProperty("_tag").GetString());
    }

    private static async Task InitializePublication()
    {
        await using var session = new ApplicationBridgeSession(new InitializeEventDispatcher());
        var eventFrame = new TaskCompletionSource<BridgeHostEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) => eventFrame.TrySetResult(message);
        BridgeHostEnvelope snapshot = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000092"), null, null, """{"_tag":"InitializeApplication"}"""));
        Equal("snapshot", snapshot.Kind);
        Equal(1L, snapshot.Sequence);
        BridgeHostEnvelope published = await eventFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(2L, published.Sequence);
        Equal(snapshot.Revision, published.Revision);
    }

    private static async Task ReentrantSubscriber()
    {
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()));
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000071"), null, null, """{"_tag":"InitializeApplication"}"""));
        Task<BridgeHostEnvelope>? reentrant = null;
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, _) =>
        {
            reentrant = session.DispatchAsync(Envelope(
                "uiReady", Guid.Parse("00000000-0000-4000-8000-000000000072"), session.Id.Value, null, "{}" )).AsTask();
            reentered.TrySetResult();
        };
        await session.PublishAsync(new(JsonDocument.Parse("""{"_tag":"NavigationChanged","revision":0,"view":"Welcome"}""").RootElement.Clone()));
        await reentered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        BridgeHostEnvelope receipt = await reentrant!.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal("receipt", receipt.Kind);
    }

    private static async Task HigherEpochInitializeFailure()
    {
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()), new BridgeLimits { MaxPendingCommands = 1, MaxCommandLedgerEntries = 1 });
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000093"), null, null, """{"_tag":"InitializeApplication"}"""));
        BridgeHostEnvelope rejected = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000094"), null, null, """{"_tag":"InitializeApplication"}""", connectionEpoch: 1));
        Equal("error", rejected.Kind);
        Equal(1L, rejected.ConnectionEpoch);
        Equal(0L, rejected.Sequence);
        BridgeHostEnvelope oldEpoch = await session.DispatchAsync(Envelope("uiReady", Guid.Parse("00000000-0000-4000-8000-000000000095"), session.Id.Value, null, "{}"));
        Equal(0L, oldEpoch.ConnectionEpoch);

        await using var contractSession = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()));
        BridgeHostEnvelope mismatch = await contractSession.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000096"), null, null, """{"_tag":"InitializeApplication"}""", connectionEpoch: 1) with { ContractFingerprint = new('0', 64) });
        Equal(1L, mismatch.ConnectionEpoch);
        Equal(0L, mismatch.Sequence);

        await using var revisionSession = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()));
        _ = await revisionSession.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000097"), null, null, """{"_tag":"InitializeApplication"}"""));
        BridgeHostEnvelope stale = await revisionSession.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000098"), null, 99, """{"_tag":"InitializeApplication"}""", connectionEpoch: 1));
        Equal(1L, stale.ConnectionEpoch);
        Equal(0L, stale.Sequence);
    }

    private static async Task SynchronousReentrantPublish()
    {
        var limits = new BridgeLimits { MaxPendingCommands = 2 };
        await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()), limits);
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000073"), null, null, """{"_tag":"InitializeApplication"}"""));
        int received = 0;
        var nestedDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == 1)
            {
                session.PublishAsync(new(JsonDocument.Parse("""{"_tag":"Nested"}""").RootElement.Clone()))
                    .AsTask().GetAwaiter().GetResult();
            }
            else nestedDelivered.TrySetResult();
        };
        await session.PublishAsync(new(JsonDocument.Parse("""{"_tag":"Outer"}""").RootElement.Clone())).ConfigureAwait(false);
        await nestedDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(2, received);
    }

    private static async Task ReentrantCommandEvent()
    {
        var dispatcher = new BlockingMutationDispatcher();
        await using var session = new ApplicationBridgeSession(dispatcher);
        _ = await session.DispatchAsync(Envelope("initialize", Guid.Parse("00000000-0000-4000-8000-000000000074"), null, null, """{"_tag":"InitializeApplication"}"""));
        var nested = new TaskCompletionSource<BridgeHostEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, _) =>
        {
            BridgeHostEnvelope response = session.DispatchAsync(Envelope(
                "uiReady", Guid.Parse("00000000-0000-4000-8000-000000000075"), session.Id.Value, null, "{}"))
                .AsTask().GetAwaiter().GetResult();
            nested.TrySetResult(response);
        };
        dispatcher.ReleaseMutation.TrySetResult();
        BridgeHostEnvelope command = await session.DispatchAsync(Envelope(
            "dispatch", Guid.Parse("00000000-0000-4000-8000-000000000076"), session.Id.Value, 0, """{"_tag":"Mutate"}"""));
        Equal("receipt", command.Kind);
        Equal("receipt", (await nested.Task.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
    }

    private static async Task ConformanceFixtures()
    {
        byte[] initial = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "conformance", "initialize.client.json"));
        byte[] resync = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "conformance", "resynchronize.client.json"));
        True(ApplicationBridgeCodec.TryDecodeClient(initial, out BridgeClientEnvelope? initialEnvelope));
        True(ApplicationBridgeCodec.TryDecodeClient(resync, out BridgeClientEnvelope? resyncEnvelope));
        Equal(0L, initialEnvelope!.ConnectionEpoch);
        Equal(1L, resyncEnvelope!.ConnectionEpoch);
        False(initialEnvelope.CommandId == resyncEnvelope.CommandId);
        using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "conformance", "resynchronized.host.json")));
        Equal(1L, snapshot.RootElement.GetProperty("connectionEpoch").GetInt64());
        Equal(1L, snapshot.RootElement.GetProperty("sequence").GetInt64());
        byte[] oldAdmission = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "conformance", "late-old-admission-error.host.json"));
        byte[] futureAdmission = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "conformance", "future-admission-error.host.json"));
        using JsonDocument oldError = JsonDocument.Parse(oldAdmission);
        using JsonDocument futureError = JsonDocument.Parse(futureAdmission);
        Equal(0L, oldError.RootElement.GetProperty("sequence").GetInt64());
        Equal(2L, futureError.RootElement.GetProperty("connectionEpoch").GetInt64());
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
        var progressEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, message) =>
        {
            events.Add(message);
            if (message.Payload.GetProperty("_tag").GetString() == "OperationProgress") progressEvent.TrySetResult();
        };
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
        await progressEvent.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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

    private static async Task OperationShutdownOwnership()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new SetupHandler()));
            var started = new TaskCompletionSource[16];
            var completed = new TaskCompletionSource[16];
            for (int index = 0; index < started.Length; index++)
            {
                started[index] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                completed[index] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource operationStarted = started[index];
                TaskCompletionSource operationCompleted = completed[index];
                session.Start(async (_, token) =>
                {
                    operationStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        operationCompleted.TrySetResult();
                        throw;
                    }
                });
            }

            await Task.WhenAll(started.Select(static item => item.Task)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(completed.Select(static item => item.Task)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private static async Task NonCancellableOperation()
    {
        var dispatcher = new NonCancellableOperationDispatcher();
        await using var session = new ApplicationBridgeSession(dispatcher);
        _ = await session.DispatchAsync(Envelope(
            "initialize",
            Guid.Parse("00000000-0000-4000-8000-000000000024"),
            null,
            null,
            """{"_tag":"InitializeApplication"}"""));
        BridgeHostEnvelope started = await session.DispatchAsync(Envelope(
            "dispatch",
            Guid.Parse("00000000-0000-4000-8000-000000000025"),
            session.Id.Value,
            session.Revision,
            """{"_tag":"StartBackgroundWork"}"""));
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Guid operationId = started.OperationId!.Value;

        BridgeHostEnvelope cancelled = await session.DispatchAsync(Envelope(
            "cancelOperation",
            Guid.Parse("00000000-0000-4000-8000-000000000026"),
            session.Id.Value,
            session.Revision,
            $$"""{"operationId":"{{operationId}}"}"""));

        False(cancelled.Payload.GetProperty("accepted").GetBoolean());
        False(dispatcher.OperationToken.IsCancellationRequested);
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
        byte[] valid = Frame("""{"protocol":"runic.artifex.setup","version":1,"contractFingerprint":"FINGERPRINT","connectionEpoch":0,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{"_tag":"InitializeApplication"}}""");
        True(ApplicationBridgeCodec.TryDecodeClient(valid, out BridgeClientEnvelope? decoded));
        Equal("initialize", decoded!.Kind);
        byte[] unknown = Frame("""{"protocol":"runic.artifex.setup","version":1,"contractFingerprint":"FINGERPRINT","connectionEpoch":0,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{},"rawFrame":"secret"}""");
        False(ApplicationBridgeCodec.TryDecodeClient(unknown, out _));
        byte[] duplicate = Frame("""{"protocol":"runic.artifex.setup","protocol":"attacker","version":1,"contractFingerprint":"FINGERPRINT","connectionEpoch":0,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{}}""");
        False(ApplicationBridgeCodec.TryDecodeClient(duplicate, out _));
        byte[] longString = Frame(
            """{"protocol":"runic.artifex.setup","version":1,"contractFingerprint":"FINGERPRINT","connectionEpoch":0,"kind":"initialize","commandId":"00000000-0000-4000-8000-000000000031","payload":{"value":"VALUE"}}"""
                .Replace("VALUE", new string('x', 65), StringComparison.Ordinal));
        False(ApplicationBridgeCodec.TryDecodeClient(longString, out _, new BridgeLimits { MaxStringBytes = 64 }));
        False(ApplicationBridgeCodec.TryDecodeClient(new byte[2048], out _, new BridgeLimits { MaxFrameBytes = 1024 }));
        False(ApplicationBridgeCodec.TryDecodeClient(Encoding.UTF8.GetBytes("""{"protocol":null,"version":"one","contractFingerprint":null,"connectionEpoch":"zero","kind":null,"commandId":null,"payload":null}"""), out _));

        byte[] hostFrame = ApplicationBridgeCodec.EncodeHost(new BridgeHostEnvelope
        {
            Protocol = "runic.artifex.setup",
            Version = 1,
            ContractFingerprint = SetupBridgeContract.Fingerprint,
            ConnectionEpoch = 0,
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
        BridgeHostEnvelope wrongInitializeCommand = await session.DispatchAsync(Envelope(
            "initialize",
            Guid.Parse("00000000-0000-4000-8000-000000000043"),
            null,
            null,
            """{"_tag":"Navigate","target":"Destination","expectedRevision":0}"""));
        Equal("CommandRejected", wrongInitializeCommand.Payload.GetProperty("_tag").GetString());
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
        string payload,
        long connectionEpoch = 0) => new()
        {
            Protocol = "runic.artifex.setup",
            Version = 1,
            ContractFingerprint = SetupBridgeContract.Fingerprint,
            ConnectionEpoch = connectionEpoch,
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
    private static byte[] Frame(string json) => Encoding.UTF8.GetBytes(
        json.Replace("FINGERPRINT", SetupBridgeContract.Fingerprint, StringComparison.Ordinal));
}

internal sealed class BlockingMutationDispatcher : IApplicationBridgeDispatcher
{
    internal TaskCompletionSource MutationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseMutation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Mutations { get; private set; }
    public string ProtocolIdentity => "runic.artifex.setup";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => SetupBridgeContract.Fingerprint;

    public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        if (command.GetProperty("_tag").GetString() == "InitializeApplication")
        {
            return new(JsonDocument.Parse("""{"snapshot":{"revision":0,"viewId":"Welcome"}}""").RootElement.Clone());
        }
        MutationEntered.TrySetResult();
        await ReleaseMutation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Mutations++;
        await context.Events.PublishAsync(new(
            JsonDocument.Parse("""{"_tag":"MutationObserved"}""").RootElement.Clone(),
            AdvancesRevision: true), cancellationToken).ConfigureAwait(false);
        return new(JsonDocument.Parse("""{"_tag":"Mutated"}""").RootElement.Clone(), AdvancesRevision: true);
    }
}

internal sealed class FailingDispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.artifex.setup";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => SetupBridgeContract.Fingerprint;

    public ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken) =>
        throw new BridgeCommandFailureException(JsonDocument.Parse(
            """{"_tag":"QuotaExceeded","limit":2}""").RootElement.Clone());

    public JsonElement ValidateError(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            payload.GetRawText() != """{"_tag":"QuotaExceeded","limit":2}""")
        {
            throw new JsonException("Invalid declared error.");
        }
        return payload.Clone();
    }
}

internal sealed class NonCancellableOperationDispatcher : IApplicationBridgeDispatcher
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken OperationToken { get; private set; }
    public string ProtocolIdentity => "runic.artifex.setup";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => SetupBridgeContract.Fingerprint;

    public ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        if (command.GetProperty("_tag").GetString() == "InitializeApplication")
            return ValueTask.FromResult(new BridgeDispatchResult(JsonDocument.Parse("""{"snapshot":{"revision":0,"viewId":"Welcome"}}""").RootElement.Clone()));
        BridgeOperationId operation = context.Operations.Start(async (_, token) =>
        {
            OperationToken = token;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }, cancellationToken);
        return ValueTask.FromResult(new BridgeDispatchResult(
            JsonDocument.Parse("""{"_tag":"BackgroundWorkStarted"}""").RootElement.Clone(),
            OperationId: operation,
            Cancellable: false));
    }
}

internal sealed class DisposedPayloadDispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.artifex.setup";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => SetupBridgeContract.Fingerprint;

    public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        if (command.GetProperty("_tag").GetString() == "InitializeApplication")
            return new(JsonDocument.Parse("""{"snapshot":{"revision":0,"viewId":"Welcome"}}""").RootElement.Clone());
        using (JsonDocument document = JsonDocument.Parse("""{"_tag":"Owned"}"""))
            await context.Events.PublishAsync(new(document.RootElement), cancellationToken).ConfigureAwait(false);
        return new(JsonDocument.Parse("""{"_tag":"Mutated"}""").RootElement.Clone());
    }
}

internal sealed class InitializeEventDispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.artifex.setup";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => SetupBridgeContract.Fingerprint;
    public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        await context.Events.PublishAsync(new(JsonDocument.Parse("""{"_tag":"InitializedEvent"}""").RootElement.Clone()), cancellationToken);
        return new(JsonDocument.Parse("""{"snapshot":{"revision":0,"viewId":"Welcome"}}""").RootElement.Clone());
    }
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

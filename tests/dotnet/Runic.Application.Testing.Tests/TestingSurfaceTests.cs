using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W319-003 B8: the 1.0 shape of Runic.Application.Testing (D21 apptest-04, D27 h, D28 b, D31, D35 o, D37, D46 e).
internal sealed class TestingSurfaceTests
{
    private static readonly Assembly Testing = typeof(RunicWindowTestHost).Assembly;

    // D21 apptest-04: one transport named for the interface it implements, one publication type, one verb. Protocol 4:
    // the transport sends requests and records pushes; the drivers take parsed publications.
    [Test]
    public void TransportUsesBridgeNamesAndOnePublicationType()
    {
        foreach (var name in new[] { "InMemoryViewTransport", "ViewTestArguments", "ViewTestPublication", "BridgeTestArguments" })
            Require(Testing.GetType($"Runic.Application.Testing.{name}") is null, $"{name} must be gone.");
        Require(typeof(IBridgeConnectionTransport).IsAssignableFrom(typeof(InMemoryBridgeTransport)),
            "The transport must implement the Bridge transport interfaces.");
        foreach (var name in new[] { "Call", "CallAsync", "Bind", "BindAsync", "Publish", "TakePublications", "DrainPublications" })
            Require(typeof(InMemoryBridgeTransport).GetMethod(name) is null, $"InMemoryBridgeTransport.{name} must be gone.");
        Require(typeof(InMemoryBridgeTransport).GetProperty("Routes") is null, "InMemoryBridgeTransport.Routes must be gone.");
        Require(typeof(InMemoryBridgeTransport).GetMethod(nameof(InMemoryBridgeTransport.Request))!.ReturnType == typeof(string)
            && typeof(InMemoryBridgeTransport).GetMethod(nameof(InMemoryBridgeTransport.TakeMessages))!.ReturnType
                == typeof(IReadOnlyList<RunicBridgeMessage>),
            "The transport must send requests and take its recorded pushes.");
        Require(typeof(RunicViewDriver<object>).GetMethod(nameof(RunicViewDriver<object>.TakePublications))!.ReturnType
                == typeof(IReadOnlyList<RunicViewPublication>),
            "The drivers must take parsed publications.");
    }

    // I-017: the interaction exceptions have the standard exception constructors (CA1032).
    [Test]
    public void InteractionExceptionsHaveTheStandardConstructors()
    {
        foreach (var type in new[] { typeof(RunicInteractionCancelledException), typeof(RunicInteractionTimeoutException), typeof(RunicInteractionOutputException) })
            foreach (Type[] parameters in new[] { Type.EmptyTypes, [typeof(string)], [typeof(string), typeof(Exception)] })
                Require(type.GetConstructor(parameters) is not null, $"{type.Name} lacks the constructor ({string.Join(", ", parameters.Select(parameter => parameter.Name))}).");
        var inner = new InvalidOperationException("inner");
        Require(new RunicInteractionTimeoutException("late", inner) is { Message: "late" } timeout && ReferenceEquals(timeout.InnerException, inner),
            "The message and inner exception were not kept.");
    }

    // D35 o: the host keeps no raw Snapshot/Mount/Unmount beside the driver's; D46 e: no legacy constructor;
    // B3/D37: DrainAsync replaces BeginCloseAsync.
    [Test]
    public void HostHasNoDuplicateMembersOrLegacyConstructor()
    {
        var host = typeof(RunicWindowTestHost<object>);
        foreach (var name in new[] { "Snapshot", "Mount", "Unmount", "BeginCloseAsync" })
            Require(host.GetMethod(name, BindingFlags.Instance | BindingFlags.Public) is null, $"RunicWindowTestHost.{name} must be gone.");
        Require(host.GetConstructors().All(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(string))),
            "The constructor that takes a root route must be gone.");
        var drain = host.GetMethod(nameof(RunicWindowTestHost<object>.DrainAsync))!;
        Require(drain.ReturnType == typeof(Task<RunicWindowCloseResult>)
            && drain.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(TimeSpan), typeof(CancellationToken)]),
            "DrainAsync must take a timeout and a token and return a Task.");
        Require(typeof(RunicViewDriver<object>).GetMethod(nameof(RunicViewDriver<object>.Unmount))!.ReturnType == typeof(void),
            "A driver's Unmount must not return the raw reply.");
    }

    // D37 apptest-07: Task everywhere except the members that mirror IBridgeTransport; the path watch tester takes tokens.
    [Test]
    public void AsyncMembersReturnTasksAndTakeTokens()
    {
        foreach (var method in Testing.GetExportedTypes()
                     .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                     .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal)
                         && typeof(IDisposable) != method.ReturnType))
        {
            var name = $"{method.DeclaringType!.Name}.{method.Name}";
            var valueTask = method.ReturnType == typeof(ValueTask) || method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
            // ValueTask where a Bridge interface or IAsyncDisposable sets the shape.
            // InMemoryBridgeTransport.RequestAsync returns what the window's IBridgeRequestHandler.HandleAsync does.
            Require(valueTask == name is "InMemoryBridgeTransport.BroadcastAsync"
                    or "InMemoryBridgeTransport.SendAsync" or "InMemoryBridgeTransport.RequestAsync"
                    or "RunicServeOnlyApplication.DisposeAsync" or "RunicTestNavigationEntry`1.DisposeAsync",
                $"{name} returns {method.ReturnType.Name}.");
            var parameters = method.GetParameters();
            Require(parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken) || name.EndsWith(".DisposeAsync", StringComparison.Ordinal),
                $"{name} must take a CancellationToken last.");
        }
    }

    // D28 b and D27 h: the success and status names of Navigation and Platform.
    [Test]
    public void RepliesUseSucceededThrowIfFailedAndErrorMessage()
    {
        var reply = typeof(RunicCallReply<object>);
        Require(reply.GetProperty("Ok") is null && reply.GetMethod("EnsureOk") is null
            && reply.GetProperty(nameof(RunicCallReply<object>.Succeeded)) is not null
            && reply.GetMethod(nameof(RunicCallReply<object>.ThrowIfFailed))!.ReturnType == reply,
            "RunicCallReply must use Succeeded and ThrowIfFailed.");
        Require(typeof(RunicFieldWriteReceipt<object>).GetProperty("Message") is null
            && typeof(RunicFieldWriteReceipt<object>).GetProperty(nameof(RunicFieldWriteReceipt<object>.ErrorMessage)) is not null,
            "RunicFieldWriteReceipt must name its text ErrorMessage.");
        Require(typeof(RunicTestOperation).GetMethod("Status") is null
            && typeof(RunicTestOperation).GetMethod(nameof(RunicTestOperation.GetStatus)) is not null,
            "RunicTestOperation.Status() must be GetStatus().");
    }

    // D31 apptest-01: closed wire-value sets are enums, not strings.
    [Test]
    public void WireValueSetsAreEnums()
    {
        Require(typeof(RunicCollectionChange).GetProperty(nameof(RunicCollectionChange.Kind))!.PropertyType == typeof(RunicCollectionChangeKind)
            && typeof(RunicOperationStatus).GetProperty(nameof(RunicOperationStatus.Kind))!.PropertyType == typeof(RunicOperationStatusKind)
            && typeof(RunicFieldWriteReceipt<object>).GetProperty(nameof(RunicFieldWriteReceipt<object>.Kind))!.PropertyType == typeof(RunicFieldWriteReceiptKind)
            && typeof(RunicTestOperation).GetProperty(nameof(RunicTestOperation.Admission))!.PropertyType == typeof(RunicOperationAdmissionKind)
            && typeof(RunicTestOperation).GetMethod(nameof(RunicTestOperation.Cancel))!.ReturnType == typeof(RunicOperationCancelKind),
            "Kinds, admission and cancellation answers must be enums.");
        Require(Enum.GetNames<RunicCollectionChangeKind>().SequenceEqual(["Add", "Remove", "Replace", "Move"]),
            "RunicCollectionChangeKind must name the four wire edits as NotifyCollectionChangedAction does.");
        Require((int)RunicOperationStatusKind.Unknown == 0, "An operation status of 0 must not claim an outcome.");
    }

    [Test]
    public async Task TransportPublicationsAreParsedAsync()
    {
        var model = new ToolkitTypedViewModel();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        // A route publishes once a client read its first state, as the page's open does.
        _ = host.Root.Snapshot();
        (await host.Root.ExecuteAsync(vm => vm.AsyncIntCommand, "4")).ThrowIfFailed();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<RunicViewPublication> taken = [];
        while (!taken.Any(TotalIsFour) && DateTime.UtcNow < deadline)
        {
            taken.AddRange(host.Root.TakePublications());
            if (!taken.Any(TotalIsFour)) await Task.Delay(10);
        }
        // Protocol 4: changes after the first state are frames of set changes.
        Require(taken.Any(TotalIsFour) && taken.All(publication => publication.Route == host.RootRoute && publication.Kind == RunicPublicationKind.Frame),
            $"The transport did not return parsed frames: {string.Join(", ", taken)}");

        static bool TotalIsFour(RunicViewPublication publication) =>
            publication.Sets.Any(set => set.Field == "total" && set.Value.GetInt32() == 4);
    }

    // An operation the window does not know reads as Unknown, and an unrecognized wire token fails loudly.
    [Test]
    public void OperationKindsComeFromTheWire()
    {
        var route = new ScriptedRoute { Admission = "accepted" };
        using var host = new RunicWindowTestHost<ScriptedViewModel>(new ScriptedViewModel(),
            (transport, _, _) => BridgeDispatcher.For(transport).RegisterRoute("scripted", route),
            new RunicWindowTestHostOptions { RootRoute = "scripted" });
        var operation = host.Root.Start(vm => vm.RunCommand);
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"Admission was {operation.Admission}.");
        Require(operation.GetStatus().Kind == RunicOperationStatusKind.Unknown, "An operation the window never admitted was not Unknown.");
        Require(operation.Cancel() == RunicOperationCancelKind.Unknown, "Cancelling an unknown operation did not answer Unknown.");
        route.Admission = "queued";
        try
        {
            _ = host.Root.Start(vm => vm.RunCommand);
            throw new InvalidOperationException("An unrecognized admission token was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("'queued'", StringComparison.Ordinal)) { }
    }

    // D37 / Views-06: the token stops the caller's wait only.
    [Test]
    [Skip("P5: needs the start, status, cancel and watch requests of operations served")]
    public async Task DrainTokenStopsTheWaitAsync()
    {
        var model = new ToolkitTypedViewModel();
        var clock = new FakeTimeProvider();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { TimeProvider = clock });
        var waiting = host.Root.Start(vm => vm.CancelCommand, "\"wait\"");
        using var stop = new CancellationTokenSource();
        var close = host.DrainAsync(Timeout.InfiniteTimeSpan, stop.Token);
        Require(!close.IsCompleted, "The drain finished while an operation ran.");
        await stop.CancelAsync();
        try
        {
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("The cancelled drain completed.");
        }
        catch (OperationCanceledException) { }
        Require(waiting.GetStatus().Kind == RunicOperationStatusKind.Running, "Stopping the wait cancelled the operation.");
        Require(waiting.Cancel() == RunicOperationCancelKind.CancellationRequested, "The operation could not be cancelled.");
        Require((await waiting.WaitAsync()).Kind == RunicOperationStatusKind.Cancelled, "The operation did not end cancelled.");
    }

    // D37 apptest-07: the path watch tester's waits honour a token as well as their real-time limit.
    [Test]
    public async Task PathWatchTesterWaitsStopOnCancellationAsync()
    {
        var watching = new RunicPathWatchTester();
        using var stop = new CancellationTokenSource();
        var next = watching.NextChangeAsync(TimeSpan.FromSeconds(30), stop.Token);
        var drain = watching.DrainAsync(TimeSpan.FromSeconds(30), stop.Token);
        await stop.CancelAsync();
        foreach (var wait in new Task[] { next, drain })
            try
            {
                await wait.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("A path watch wait ignored its token.");
            }
            catch (OperationCanceledException) { }
    }

    internal sealed class ScriptedViewModel
    {
        public object? RunCommand { get; }
    }

    // A route that answers start with a scripted admission, and status and cancel as for an operation it never admitted.
    private sealed class ScriptedRoute : IBridgeRouteHandler
    {
        public string Admission { get; set; } = "";

        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
            new(System.Text.Encoding.UTF8.GetBytes(request.Kind switch
            {
                "start" => $$$"""{"ok":true,"revision":0,"admission":"{{{Admission}}}","status":{"kind":"running"}}""",
                "status" => """{"ok":true,"status":{"kind":"unknown"}}""",
                "cancel" => """{"ok":true,"cancellation":"unknown"}""",
                _ => """{"ok":true,"revision":0,"state":{}}""",
            }));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

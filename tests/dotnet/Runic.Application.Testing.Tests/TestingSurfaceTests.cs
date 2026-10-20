using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W319-003 B8: the 1.0 shape of Runic.Application.Testing (D21 apptest-04, D27 h, D28 b, D31, D35 o, D37, D46 e).
internal sealed class TestingSurfaceTests
{
    private static readonly Assembly Testing = typeof(RunicWindowTestHost).Assembly;

    // D21 apptest-04: one transport named for the interface it implements, one publication type, one verb.
    [Test]
    public void TransportUsesBridgeNamesAndOnePublicationType()
    {
        foreach (var name in new[] { "InMemoryViewTransport", "ViewTestArguments", "ViewTestPublication" })
            Require(Testing.GetType($"Runic.Application.Testing.{name}") is null, $"{name} must be gone.");
        Require(typeof(IBridgeTransport).IsAssignableFrom(typeof(InMemoryBridgeTransport))
            && typeof(IBridgeArguments).IsAssignableFrom(typeof(BridgeTestArguments)),
            "The transport and its arguments must implement the Bridge interfaces.");
        Require(typeof(InMemoryBridgeTransport).GetMethod("DrainPublications") is null
            && typeof(InMemoryBridgeTransport).GetMethod(nameof(InMemoryBridgeTransport.TakePublications))!.ReturnType
                == typeof(IReadOnlyList<RunicViewPublication>),
            "The transport must take parsed publications, as drivers do.");
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

    // D37 apptest-07: Task everywhere except CallAsync, which mirrors IBridgeTransport; the path watch tester takes tokens.
    // BindAsync registers a route (IBridgeTransport) and is not awaited.
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
            Require(valueTask == name is "InMemoryBridgeTransport.CallAsync" or "RunicServeOnlyApplication.DisposeAsync",
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
        (await host.Root.ExecuteAsync(vm => vm.AsyncIntCommand, "4")).ThrowIfFailed();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<RunicViewPublication> taken = [];
        while (taken.Count == 0 && DateTime.UtcNow < deadline)
        {
            taken = host.Transport.TakePublications();
            if (taken.Count == 0) await Task.Delay(10);
        }
        Require(taken.Count > 0 && taken.All(publication => publication.Route == host.RootRoute && publication.Kind == RunicPublicationKind.State
                && publication.State?.GetProperty("total").GetInt32() is 4 or 0),
            $"The transport did not return parsed state frames: {string.Join(", ", taken)}");
    }

    // An operation the window does not know reads as Unknown, and an unrecognized wire token fails loudly.
    [Test]
    public void OperationKindsComeFromTheWire()
    {
        var admission = """{"kind":"accepted","status":"running","reason":null,"terminal":null}""";
        using var host = new RunicWindowTestHost<ScriptedViewModel>(new ScriptedViewModel(), (transport, _, _) =>
        {
            var snapshot = transport.Bind("scriptedSnapshot", _ => """{"ok":true,"state":{"revision":0},"error":null}""");
            var start = transport.Bind("scriptedStartRun", _ => admission);
            return new Bindings(snapshot, start);
        }, new RunicWindowTestHostOptions { RootRoute = "scripted" });
        var operation = host.Root.Start(vm => vm.RunCommand);
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"Admission was {operation.Admission}.");
        Require(operation.GetStatus().Kind == RunicOperationStatusKind.Unknown, "An operation the window never admitted was not Unknown.");
        Require(operation.Cancel() == RunicOperationCancelKind.Unknown, "Cancelling an unknown operation did not answer Unknown.");
        admission = """{"kind":"queued","status":"running","reason":null,"terminal":null}""";
        try
        {
            _ = host.Root.Start(vm => vm.RunCommand);
            throw new InvalidOperationException("An unrecognized admission token was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("'queued'", StringComparison.Ordinal)) { }
    }

    // D37 / Views-06: the token stops the caller's wait only.
    [Test]
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

    private sealed class Bindings(params IDisposable[] bindings) : IDisposable
    {
        public void Dispose()
        {
            foreach (var binding in bindings) binding.Dispose();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

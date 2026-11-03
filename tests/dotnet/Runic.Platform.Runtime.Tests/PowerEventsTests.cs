using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;
using MacPower = Runic.Platform.MacOS.MacPowerEvents;
using WindowsPower = Runic.Platform.Windows.WindowsPowerEvents;

// B4: power and session events without a desktop: the watch over a fake source, the tracker's events, the Unavailable
// service, and the Windows and macOS mappings. The native sources are the providers' manual checks; Linux's runs
// against fake logind, UPower and power-profiles services (Runic.Platform.Linux.Portal.Tests).
#pragma warning disable CA1416 // The Windows and macOS mappings are pure functions, checked on every platform.
internal sealed class PowerEventsTests
{
    private static readonly DesktopPowerState Mains = new() { IsOnBattery = false, IsLowPower = false, IsLocked = false };

    [Test, DisplayName("Power events: the tracker reports each change once")]
    public void Tracker()
    {
        var events = new List<PlatformResult<DesktopPowerEvent>>();
        var tracker = new PowerStateTracker(events.Add);
        tracker.Start(Mains);
        tracker.Update(Mains);
        tracker.Update(Mains with { IsOnBattery = true });
        tracker.Suspending();
        tracker.Suspending();
        tracker.Resumed();
        tracker.Resumed();
        tracker.Locked(true);
        tracker.Locked(true);
        tracker.Locked(false);
        Check(Kinds(events).SequenceEqual([DesktopPowerEventKind.StateChanged, DesktopPowerEventKind.StateChanged, DesktopPowerEventKind.Suspending,
            DesktopPowerEventKind.Resumed, DesktopPowerEventKind.Locked, DesktopPowerEventKind.Unlocked]), "events: " + string.Join(", ", Kinds(events)));
        Check(events[4] is PlatformResult<DesktopPowerEvent>.Success { Value.State: { IsLocked: true, IsOnBattery: true } }, "Locked carries the state after it");

        // A wake whose sleep was not announced still counts.
        var wake = new List<PlatformResult<DesktopPowerEvent>>();
        new PowerStateTracker(wake.Add).Resumed();
        Check(Kinds(wake).SequenceEqual([DesktopPowerEventKind.Resumed]), "a lone Resumed");
    }

    [Test, DisplayName("Power events: the watch reports the state, then events in order, without repeats")]
    public async Task WatchAsync()
    {
        var source = new FakeSource { Current = Mains };
        await using var events = new PowerEventsService(source);
        Check(await events.ReadAsync() == new PlatformResult<DesktopPowerState>.Success(Mains), "the read");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var watch = events.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, Mains), "the current state first");
        // Bursts keep every event: a sleep and wake both arrive.
        source.Report(Event(DesktopPowerEventKind.StateChanged, Mains));
        source.Report(Event(DesktopPowerEventKind.Suspending, Mains));
        source.Report(Event(DesktopPowerEventKind.Resumed, Mains));
        source.Report(Event(DesktopPowerEventKind.Resumed, Mains));
        source.Report(Event(DesktopPowerEventKind.StateChanged, Mains with { IsOnBattery = true }));
        Check(await Next(watch) == Event(DesktopPowerEventKind.Suspending, Mains), "Suspending, after a StateChanged that changed nothing");
        Check(await Next(watch) == Event(DesktopPowerEventKind.Resumed, Mains), "Resumed");
        Check(await Next(watch) == Event(DesktopPowerEventKind.StateChanged, Mains with { IsOnBattery = true }), "on battery, after a repeated Resumed");
        source.Report(new PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        Check(await Next(watch) is PlatformResult<DesktopPowerEvent>.Unavailable, "the source left");

        // A source that cannot subscribe is reported once.
        await using var failing = new PowerEventsService(new FakeSource { Fails = true });
        await using var unavailable = failing.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await Next(unavailable) is PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "no source");

        var disposed = new PowerEventsService(source);
        await disposed.DisposeAsync();
        await Throws<ObjectDisposedException>(async () => await disposed.ReadAsync());
        Throws<ObjectDisposedException>(() => disposed.WatchAsync());
    }

    [Test, DisplayName("Power events: the queue keeps every event and only the latest state between them")]
    public async Task QueueAsync()
    {
        var queue = new PowerEventQueue();
        var kinds = new[] { DesktopPowerEventKind.Suspending, DesktopPowerEventKind.Resumed, DesktopPowerEventKind.Locked, DesktopPowerEventKind.Unlocked };
        // A reader far behind: 400 events with 2000 state changes between them.
        for (var index = 0; index < 400; index++)
        {
            for (var change = 0; change < 5; change++) queue.Add(Event(DesktopPowerEventKind.StateChanged, Mains with { IsOnBattery = change % 2 == 0 }));
            queue.Add(Event(kinds[index % 4], Mains));
        }
        queue.Add(new PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        queue.Add(Event(DesktopPowerEventKind.StateChanged, Mains with { IsLowPower = true }));
        Check(queue.Count == 801, $"the queue holds {queue.Count}: 400 events, a state before each and one last state");
        for (var index = 0; index < 400; index++)
        {
            Check(await queue.TakeAsync(CancellationToken.None) == Event(DesktopPowerEventKind.StateChanged, Mains with { IsOnBattery = true }),
                "the latest of the five states before the event");
            Check(await queue.TakeAsync(CancellationToken.None) == Event(kinds[index % 4], Mains), $"event {index} kept, in order");
        }
        Check(await queue.TakeAsync(CancellationToken.None) == Event(DesktopPowerEventKind.StateChanged, Mains with { IsLowPower = true }),
            "a state replaces an Unavailable before it");
        Check(queue.Count == 0, "the queue is empty");
    }

    [Test, DisplayName("Power events: Unavailable reports once, then waits until cancelled or disposed")]
    public async Task UnavailableAsync()
    {
        var events = DesktopPowerEvents.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(DesktopServiceStatus.Get(events) == new CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "status");
        Check(await events.ReadAsync() is PlatformResult<DesktopPowerState>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "read");
        await using var watch = events.WatchAsync().GetAsyncEnumerator();
        Check(await Next(watch) is PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "watch");
        var waiting = watch.MoveNextAsync().AsTask();
        await events.DisposeAsync();
        await Throws<OperationCanceledException>(() => waiting);
        Throws<ArgumentOutOfRangeException>(() => DesktopPowerEvents.Unavailable((PlatformUnavailableReason)99));
    }

    [Test, DisplayName("Power events: the Windows power status and the macOS power source as states")]
    public void PlatformStates()
    {
        Check(WindowsPower.State((0, 1, 0), false) == new DesktopPowerState { IsOnBattery = true, IsLowPower = false, IsLocked = false }, "on battery");
        Check(WindowsPower.State((1, 1, 1), true) == new DesktopPowerState { IsOnBattery = false, IsLowPower = true, IsLocked = true }, "mains, saver on, locked");
        Check(WindowsPower.State((1, 128, 0), null).IsOnBattery == false && WindowsPower.State((255, 128, 0), null).IsOnBattery == false,
            "no system battery is never on battery");
        Check(WindowsPower.State((255, 1, 0), null).IsOnBattery is null, "an unknown line status");
        Check(WindowsPower.State(null, null) == new DesktopPowerState(), "GetSystemPowerStatus failed");
        Check(MacPower.OnBattery("Battery Power") == true && MacPower.OnBattery("AC Power") == false && MacPower.OnBattery("UPS Power") == false
            && MacPower.OnBattery(null) is null && MacPower.OnBattery("Off Line") is null, "macOS power sources");
    }

    private static PlatformResult<DesktopPowerEvent> Event(DesktopPowerEventKind kind, DesktopPowerState state) =>
        new PlatformResult<DesktopPowerEvent>.Success(new DesktopPowerEvent(kind, state));

    private static IEnumerable<DesktopPowerEventKind> Kinds(IEnumerable<PlatformResult<DesktopPowerEvent>> events) =>
        events.OfType<PlatformResult<DesktopPowerEvent>.Success>().Select(static result => result.Value.Kind);

    private static async Task<T> Next<T>(IAsyncEnumerator<T> watch)
    {
        Check(await watch.MoveNextAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(5)), "the watch ended");
        return watch.Current;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeSource : IPowerEventSource
    {
        private readonly Lock _gate = new();
        private Action<PlatformResult<DesktopPowerEvent>>? _report;
        internal DesktopPowerState Current { get; set; } = new();
        internal bool Fails { get; init; }

        public ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<PlatformResult<DesktopPowerState>>(new PlatformResult<DesktopPowerState>.Success(Current));

        public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopPowerEvent>> report, CancellationToken cancellationToken)
        {
            if (Fails) throw new NativeBackendUnavailableException();
            lock (_gate) _report = report;
            new PowerStateTracker(report).Start(Current);
            return ValueTask.FromResult<IAsyncDisposable>(new Ending(this));
        }

        internal void Report(PlatformResult<DesktopPowerEvent> result)
        {
            Action<PlatformResult<DesktopPowerEvent>>? report;
            lock (_gate) report = _report;
            report?.Invoke(result);
        }

        private sealed class Ending(FakeSource source) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                lock (source._gate) source._report = null;
                return ValueTask.CompletedTask;
            }
        }
    }
}

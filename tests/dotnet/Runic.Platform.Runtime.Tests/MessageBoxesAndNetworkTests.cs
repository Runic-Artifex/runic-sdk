using System.Collections.Immutable;
using System.Threading.Channels;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;
using MacNetwork = Runic.Platform.MacOS.MacNetworkStatus;
using WindowsBoxes = Runic.Platform.Windows.WindowsMessageBoxes;
using WindowsNetwork = Runic.Platform.Windows.WindowsNetworkStatus;
using WindowsProvider = Runic.Platform.Windows.WindowsPlatformProvider;

// Message boxes and network status without a desktop: the rules of a box, the order and labels of its buttons on each
// platform, the default member and the Unavailable services, the presentation's lifetime, the Windows and macOS
// mappings, and the network watch over a fake source. The native boxes and sources are the providers' native cases.
#pragma warning disable CA1416 // The Windows and macOS mappings are pure functions, checked on every platform.
internal sealed class MessageBoxesAndNetworkTests
{
    private static readonly DesktopMessageBoxButton Save = new("save", "Save") { Role = DesktopMessageBoxButtonRole.Default, AccessKey = 's' };
    private static readonly DesktopMessageBoxButton Discard = new("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive, AccessKey = 'n' };
    private static readonly DesktopMessageBoxButton Cancel = new("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };
    private static readonly DesktopMessageBox Close = new("Save changes to “Notes”?")
    {
        Detail = "Your changes are lost if you don't save them.",
        Kind = DesktopMessageBoxKind.Warning,
        Buttons = [Discard, Cancel, Save],
    };

    [Test, DisplayName("Message boxes: every implementation checks the box")]
    public async Task RulesAsync()
    {
        IDesktopMessageBoxes[] implementations =
        [
            DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            ((IWindowPlatformServices)new MinimalWindowServices()).MessageBoxes,
            WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed).MessageBoxes,
            new PresentationMessageBoxes(new PresentationLifetime(() => true), new AnsweringBoxes(_ => null)),
        ];
        DesktopMessageBox[] invalid =
        [
            new(""), new("  "), new("Message\0"), Close with { Detail = "\0" }, Close with { Title = "a\0b" },
            Close with { Kind = (DesktopMessageBoxKind)7 }, Close with { OwnerPolicy = (OwnerPolicy)3 },
            Close with { Buttons = [.. Enumerable.Range(0, 7).Select(static index => new DesktopMessageBoxButton($"b{index}", "Button"))] },
            Close with { Buttons = [Save, Save with { Role = DesktopMessageBoxButtonRole.Normal }] },
            Close with { Buttons = [Save, new DesktopMessageBoxButton("", "Empty id")] },
            Close with { Buttons = [Save, new DesktopMessageBoxButton("x", " ")] },
            Close with { Buttons = [Save, new DesktopMessageBoxButton("x", "Label\0")] },
            Close with { Buttons = [Save, Save with { Id = "other" }] },
            Close with { Buttons = [Cancel, Cancel with { Id = "other" }] },
            Close with { Buttons = [Save with { AccessKey = '-' }] },
            Close with { Buttons = [Save with { Role = (DesktopMessageBoxButtonRole)9 }] },
            Close with { Buttons = [Save, null!] },
        ];
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var boxes in implementations)
        {
            await Throws<ArgumentNullException>(() => boxes.ShowAsync(null!).AsTask());
            foreach (var box in invalid) await Throws<ArgumentException>(() => boxes.ShowAsync(box).AsTask());
            await Throws<OperationCanceledException>(() => boxes.ShowAsync(Close, cancelled.Token).AsTask());
        }
        // Six buttons, the empty list and a default array are valid.
        var six = Close with { Buttons = [.. Enumerable.Range(0, 6).Select(static index => new DesktopMessageBoxButton($"b{index}", "Button"))] };
        foreach (var box in new[] { six, new DesktopMessageBox("Done"), Close with { Buttons = default } })
            Check(await implementations[0].ShowAsync(box) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "a valid box reports the reason");
    }

    [Test, DisplayName("Message boxes: the default member and Unavailable report their reason")]
    public async Task UnavailableAsync()
    {
        IWindowPlatformServices minimal = new MinimalWindowServices();
        Check(await minimal.MessageBoxes.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "a host without message boxes reports the owner unavailable");
        Check(DesktopServiceStatus.Get(minimal.MessageBoxes) is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "and its status says so");
        foreach (var reason in Enum.GetValues<PlatformUnavailableReason>())
        {
            var boxes = DesktopMessageBoxes.Unavailable(reason);
            Check(await boxes.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(var reported) && reported == reason, $"{reason} is reported");
            var services = WindowPlatformServices.Unavailable(reason);
            Check(services.GetSnapshot().Statuses[PlatformCapabilityIds.MessageBoxes] is CapabilityStatus.Unavailable(var status) && status == reason,
                "the unavailable window services report the capability");
            Check(await services.MessageBoxes.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(var shown) && shown == reason,
                "and their message boxes the reason");
        }
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(DesktopMessageBoxes.Unavailable((PlatformUnavailableReason)99)));
    }

    [Test, DisplayName("Message boxes: each platform orders the buttons by role and escapes labels")]
    public void ButtonsByPlatform()
    {
        ImmutableArray<DesktopMessageBoxButton> buttons = [Discard, Cancel, Save];
        Check(DesktopMessageBoxRules.Ordered(buttons, MessageBoxPlatform.Windows).SequenceEqual(new[] { Save, Discard, Cancel }), "Windows: Save, Don't Save, Cancel");
        Check(DesktopMessageBoxRules.Ordered(buttons, MessageBoxPlatform.MacOS).SequenceEqual(new[] { Save, Cancel, Discard }),
            "macOS adds Save (rightmost), Cancel, then Don't Save (leftmost)");
        Check(DesktopMessageBoxRules.Ordered(buttons, MessageBoxPlatform.Gtk).SequenceEqual(new[] { Discard, Cancel, Save }), "GTK: Don't Save, Cancel, Save");
        ImmutableArray<DesktopMessageBoxButton> plain = [new("a", "A"), new("b", "B"), new("c", "C")];
        foreach (var platform in Enum.GetValues<MessageBoxPlatform>())
            Check(DesktopMessageBoxRules.Ordered(plain, platform).SequenceEqual(plain), $"{platform} keeps buttons without roles in order");
        Check(DesktopMessageBoxRules.Ordered([], MessageBoxPlatform.Windows).IsEmpty && DesktopMessageBoxRules.Ordered(default, MessageBoxPlatform.Gtk).IsEmpty,
            "no buttons");

        Check(DesktopMessageBoxRules.Label(Save, '&') == "&Save" && DesktopMessageBoxRules.Label(Save, '_') == "_Save", "the access key is marked");
        Check(DesktopMessageBoxRules.Label(Discard, '&') == "Do&n't Save", "the first letter that matches in any case");
        Check(DesktopMessageBoxRules.Label(new("x", "Fish & Chips"), '&') == "Fish && Chips", "a literal & is doubled on Windows");
        Check(DesktopMessageBoxRules.Label(new("x", "snake_case") { AccessKey = 'c' }, '_') == "snake___case", "a literal _ is doubled on GTK");
        Check(DesktopMessageBoxRules.Label(new("x", "Speichern") { AccessKey = 'q' }, '&') == "Speichern (&Q)", "a key the label lacks follows it in parentheses");
        Check(DesktopMessageBoxRules.Label(new("x", "Plain"), '&') == "Plain", "a label without an access key");
    }

    [Test, DisplayName("Message boxes: Windows button ids map back to the application's buttons")]
    public void WindowsButtonIds()
    {
        var buttons = Close.Buttons;
        Check(WindowsBoxes.Id(buttons, Cancel) == 2, "the cancel button is IDCANCEL, which Esc and the close button send");
        foreach (var button in buttons)
            Check(WindowsBoxes.Chosen(buttons, WindowsBoxes.Id(buttons, button)) == button, $"{button.Id} round-trips");
        Check(WindowsBoxes.Chosen(buttons, 1) is null && WindowsBoxes.Chosen(buttons, 99) is null
            && WindowsBoxes.Chosen([], 1) is null, "unknown ids and the common OK button choose nothing");
        Check(WindowsBoxes.Chosen([Save], 2) is null, "IDCANCEL without a cancel button chooses nothing");
    }

    [Test, DisplayName("Message boxes: Windows without Common Controls 6 reports the backend unavailable")]
    public async Task WindowsWithoutCommonControlsAsync()
    {
        var boxes = WindowsProvider.CreateMessageBoxes(new IdleOwner(), controls: null);
        Check(DesktopServiceStatus.Get(boxes) is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), "the status");
        await using var lifetime = new PresentationLifetime(() => true);
        var presentation = new PresentationMessageBoxes(lifetime, boxes);
        Check(presentation.GetSnapshot().Statuses[PlatformCapabilityIds.MessageBoxes] is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "the window's capability");
        Check(await presentation.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "a box");
    }

    [Test, DisplayName("Message boxes: the presentation reports its owner and closes boxes when it stops")]
    public async Task PresentationAsync()
    {
        bool hasOwner = false;
        var lifetime = new PresentationLifetime(() => hasOwner);
        var shown = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new AnsweringBoxes(token => { shown.TrySetResult(token); return null; }, wait: true);
        var boxes = new PresentationMessageBoxes(lifetime, backend);
        Check(boxes.GetSnapshot().Statuses.Keys.SequenceEqual(new[] { PlatformCapabilityIds.MessageBoxes }), "one capability");
        Check(boxes.GetSnapshot().Statuses[PlatformCapabilityIds.MessageBoxes] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && await boxes.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "before the window opens");
        hasOwner = true;
        Check(boxes.GetSnapshot().Statuses[PlatformCapabilityIds.MessageBoxes] is CapabilityStatus.Available, "available with an owner");
        Check(new PresentationMessageBoxes(lifetime).GetSnapshot().Statuses[PlatformCapabilityIds.MessageBoxes]
            is CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "a provider without boxes");

        var answered = new PresentationMessageBoxes(lifetime, new AnsweringBoxes(_ => Save));
        Check(await answered.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Success(var chosen) && chosen == Save, "the chosen button");
        var foreign = new PresentationMessageBoxes(lifetime, new AnsweringBoxes(_ => new DesktopMessageBoxButton("other", "Other")));
        await Throws<InvalidOperationException>(() => foreign.ShowAsync(Close).AsTask());

        // The caller's cancellation closes the box and throws.
        using (var cancel = new CancellationTokenSource())
        {
            var pending = boxes.ShowAsync(Close, cancel.Token).AsTask();
            var token = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => pending);
            Check(token.IsCancellationRequested, "the provider saw the cancellation");
        }

        // Stopping the presentation closes a shown box, which reports the owner closed.
        shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = boxes.ShowAsync(Close).AsTask();
        var closing = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = lifetime.DisposeAsync().AsTask();
        Check(await open.WaitAsync(TimeSpan.FromSeconds(5)) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && closing.IsCancellationRequested, "closing the window closes the box");
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Check(await boxes.ShowAsync(Close) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && boxes.GetSnapshot().Statuses[PlatformCapabilityIds.MessageBoxes] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "after the window closed");
    }

    [Test, DisplayName("Network status: the Windows connectivity hint and the macOS path as states")]
    public void PlatformStates()
    {
        static global::Windows.Win32.Networking.WinSock.NL_NETWORK_CONNECTIVITY_HINT Hint(int level, int cost, bool overLimit = false, bool roaming = false) => new()
        {
            ConnectivityLevel = (global::Windows.Win32.Networking.WinSock.NL_NETWORK_CONNECTIVITY_LEVEL_HINT)level,
            ConnectivityCost = (global::Windows.Win32.Networking.WinSock.NL_NETWORK_CONNECTIVITY_COST_HINT)cost,
            OverDataLimit = overLimit, Roaming = roaming,
        };
        Check(WindowsNetwork.State(Hint(1, 1)) == new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.None, IsMetered = false },
            "no network");
        Check(WindowsNetwork.State(Hint(2, 1)) == new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Local, IsMetered = false },
            "local access");
        Check(WindowsNetwork.State(Hint(3, 1)) == new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Internet, IsMetered = false },
            "internet access, unrestricted");
        Check(WindowsNetwork.State(Hint(4, 0)) == new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.CaptivePortal },
            "constrained internet access is a captive portal; an unknown cost is unknown");
        Check(WindowsNetwork.State(Hint(3, 2)).IsMetered == true && WindowsNetwork.State(Hint(3, 3)).IsMetered == true,
            "fixed and variable costs are metered");
        Check(WindowsNetwork.State(Hint(3, 1, overLimit: true)).IsMetered == true
            && WindowsNetwork.State(Hint(3, 0, roaming: true)).IsMetered == true, "over the data limit and roaming are metered");
        foreach (var level in new[] { 0, 5 })
        {
            var state = WindowsNetwork.State(Hint(level, 1));
            Check(state.IsAvailable ? state.Connectivity == DesktopNetworkConnectivity.Unknown : state.Connectivity == DesktopNetworkConnectivity.None,
                $"an unknown or hidden level ({level}) is unknown connectivity");
        }

        Check(MacNetwork.State(1, false, false) == new DesktopNetworkState(true) { IsMetered = false }, "a satisfied path, connectivity unknown");
        Check(MacNetwork.State(2, false, false) == new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.None, IsMetered = false },
            "an unsatisfied path");
        Check(MacNetwork.State(0, false, false).IsAvailable == false, "an invalid path");
        Check(MacNetwork.State(3, false, false).IsAvailable, "a satisfiable path may connect");
        Check(MacNetwork.State(1, true, false).IsMetered == true && MacNetwork.State(1, false, true).IsMetered == true,
            "expensive and constrained (Low Data Mode) paths are metered");
    }

    [Test, DisplayName("Network status: the watch reports the initial state, changes and the source's absence")]
    public async Task WatchAsync()
    {
        var source = new FakeSource();
        await using var status = new NetworkStatusService(source);
        Check(DesktopServiceStatus.Get(status) is CapabilityStatus.Available, "a service with a source is available");
        var online = new DesktopNetworkState(true) { Connectivity = DesktopNetworkConnectivity.Internet, IsMetered = false };
        source.Current = Success(online);
        Check(await status.ReadAsync() == Success(online), "a read");
        // A source that says unavailable but some connectivity is corrected.
        source.Current = Success(new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.Local });
        Check(await status.ReadAsync() == Success(new DesktopNetworkState(false) { Connectivity = DesktopNetworkConnectivity.None }),
            "no network has no connectivity");
        source.Current = Success(online);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var watch = status.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await Next(watch) == Success(online), "the initial state");
        source.Report(Success(online));
        var metered = online with { IsMetered = true };
        source.Report(Success(metered));
        Check(await Next(watch) == Success(metered), "an unchanged state is not reported, a changed one is");
        source.Report(new PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        Check(await Next(watch) is PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "the source went away");
        source.Report(Success(metered));
        Check(await Next(watch) == Success(metered), "a fresh state once it is back, even when it equals the last one");
        Check(source.Subscriptions == 1, "one subscription per watch");
        cancel.Cancel();
        await Throws<OperationCanceledException>(() => watch.MoveNextAsync().AsTask());
        Check(source.Subscriptions == 0, "cancelling the watch ends its subscription");

        // A source that cannot subscribe: the watch reports it once and waits.
        var failing = new FakeSource { Fails = true };
        await using var unsubscribed = new NetworkStatusService(failing);
        using var wait = new CancellationTokenSource();
        await using var lonely = unsubscribed.WatchAsync(wait.Token).GetAsyncEnumerator(wait.Token);
        Check(await Next(lonely) is PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "no source");
        var more = lonely.MoveNextAsync().AsTask();
        await Task.Delay(100);
        Check(!more.IsCompleted, "then waits");
        wait.Cancel();
        await Throws<OperationCanceledException>(() => more);

        // Disposal ends watches and refuses new calls.
        var disposed = new NetworkStatusService(new FakeSource { Current = Success(online) });
        await using var ending = disposed.WatchAsync().GetAsyncEnumerator();
        Check(await Next(ending) == Success(online), "a watch before disposal");
        var waiting = ending.MoveNextAsync().AsTask();
        await disposed.DisposeAsync();
        await Throws<OperationCanceledException>(() => waiting);
        await Throws<ObjectDisposedException>(() => disposed.ReadAsync().AsTask());
        await Throws<ObjectDisposedException>(() => Task.FromResult(disposed.WatchAsync()));
    }

    [Test, DisplayName("Network status: Unavailable reports once, then waits until cancelled or disposed")]
    public async Task UnavailableNetworkAsync()
    {
        var status = DesktopNetworkStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(DesktopServiceStatus.Get(status) is CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "the status");
        Check(await status.ReadAsync() is PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "a read");
        await using var watch = status.WatchAsync().GetAsyncEnumerator();
        Check(await Next(watch) is PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "the watch's first item");
        var next = watch.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Check(!next.IsCompleted, "then it waits");
        await status.DisposeAsync();
        await Throws<OperationCanceledException>(() => next);
        await Throws<ObjectDisposedException>(() => status.ReadAsync().AsTask());
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(DesktopNetworkStatus.Unavailable((PlatformUnavailableReason)99)));
    }

    private static PlatformResult<DesktopNetworkState> Success(DesktopNetworkState state) => new PlatformResult<DesktopNetworkState>.Success(state);

    private static async Task<PlatformResult<DesktopNetworkState>> Next(IAsyncEnumerator<PlatformResult<DesktopNetworkState>> watch)
    {
        Check(await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "the watch ended");
        return watch.Current;
    }

    private sealed class FakeSource : INetworkStateSource
    {
        private readonly Lock _gate = new();
        private Action<PlatformResult<DesktopNetworkState>>? _report;
        internal PlatformResult<DesktopNetworkState> Current { get; set; } = new PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        internal bool Fails { get; init; }
        internal int Subscriptions { get { lock (_gate) return _report is null ? 0 : 1; } }

        public ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Current);

        public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopNetworkState>> report, CancellationToken cancellationToken)
        {
            if (Fails) throw new NativeBackendUnavailableException();
            lock (_gate) _report = report;
            report(Current);
            return ValueTask.FromResult<IAsyncDisposable>(new Ending(this));
        }

        internal void Report(PlatformResult<DesktopNetworkState> result)
        {
            Action<PlatformResult<DesktopNetworkState>>? report;
            lock (_gate) report = _report;
            report?.Invoke(result);
            // Each report is read before the next one: the watch keeps only the latest.
            Thread.Sleep(50);
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

    // A provider that answers each box with the button the function picks, after waiting for cancellation if asked.
    private sealed class AnsweringBoxes(Func<CancellationToken, DesktopMessageBoxButton?> answer, bool wait = false) : IDesktopMessageBoxes
    {
        public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
        {
            var button = answer(cancellationToken);
            if (wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new PlatformResult<DesktopMessageBoxButton?>.Success(button);
        }
    }

    private sealed class IdleOwner : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MinimalWindowServices : IWindowPlatformServices
    {
        private readonly IWindowPlatformServices _inner = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        public IFileDialogs FileDialogs => _inner.FileDialogs;
        public ITextClipboard Clipboard => _inner.Clipboard;
        public IDesktopFileLauncher FileLauncher => _inner.FileLauncher;
        public CapabilitySnapshot GetSnapshot() => _inner.GetSnapshot();
    }

    private static async Task Throws<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

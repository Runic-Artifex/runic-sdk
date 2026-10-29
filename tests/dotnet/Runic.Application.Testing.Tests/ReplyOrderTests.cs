using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// I-183: a call's reply reaches the page after the state frames that the call produced, also on another route of
// the window (read-your-writes). The page's transport takes each frame slowly, so before the fix every reply won.
// I-174: a frame that is on its way when its bridge is released and the window's transport disposed ends silently.
internal sealed class ReplyOrderTests
{
    [Test]
    public async Task AnAwaitedCommandRepliesAfterTheShellStateItChangedAsync()
    {
        await using var window = await Window.OpenAsync();
        using var reply = JsonDocument.Parse(await window.Transport.RequestAsync("""{"kind":"command","route":"page","member":"load"}"""));
        Require(reply, "load");
        window.RequireShellValue(1, "the awaited command load");
    }

    [Test]
    public async Task ASynchronousCommandRepliesAfterTheShellStateItChangedAsync()
    {
        await using var window = await Window.OpenAsync();
        using var reply = JsonDocument.Parse(await Task.Run(() => window.Transport.Request(Bump)));
        Require(reply, "bump");
        window.RequireShellValue(1, "the synchronous command bump");
    }

    [Test]
    public async Task ASetterRepliesAfterTheShellStateItChangedAsync()
    {
        await using var window = await Window.OpenAsync();
        using var reply = JsonDocument.Parse(await Task.Run(() =>
            window.Transport.Request("""{"kind":"set","route":"page","member":"value","value":7,"edit":1}""")));
        Require(reply, "set value");
        window.RequireShellValue(7, "the setter of value");
    }

    // A synchronous route called inside the model turn cannot wait for a capture that needs that turn. It replies
    // at once instead of blocking for the wait limit; the state follows.
    [Test]
    public async Task ASynchronousCallInsideTheModelTurnRepliesWithoutWaitingAsync()
    {
        await using var window = await Window.OpenAsync();
        var started = TimeProvider.System.GetTimestamp();
        var reply = await window.Context.InvokeAsync(() => window.Transport.Request(Bump));
        using (var document = JsonDocument.Parse(reply)) Require(document, "bump");
        if (TimeProvider.System.GetElapsedTime(started) >= BridgeReplyOrder.ReplyWaitLimit)
            throw new InvalidOperationException("A call inside the model turn waited for a capture that needs that turn.");
        await window.WaitForShellValueAsync(1);
    }

    // 0.13.1 (Artificers Storyline): an awaited reply resumed its caller inside the delivery of the route whose frame it
    // waited for. A test that then sent a synchronous request blocked that delivery: the shell kept a unit requested but
    // never run, and this reply and every later one waited the full ReplyWaitLimit.
    [Test]
    public async Task ACallerResumingAfterAReplyDoesNotHoldTheDeliveryAsync()
    {
        await using var window = await Window.OpenAsync();
        using (var first = JsonDocument.Parse(await window.Transport.RequestAsync(
            """{"kind":"set","route":"page","member":"value","value":7,"edit":1}""").ConfigureAwait(false)))
            Require(first, "set value");
        var started = TimeProvider.System.GetTimestamp();
        using (var second = JsonDocument.Parse(window.Transport.Request(
            """{"kind":"set","route":"page","member":"value","value":8,"edit":2}""")))
            Require(second, "set value");
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        if (elapsed >= BridgeReplyOrder.ReplyWaitLimit)
            throw new InvalidOperationException($"The second reply waited {elapsed} for a delivery that its own caller held.");
        window.RequireShellValue(8, "the second setter of value");
    }

    // A reply that waits for ReplyWaitLimit is sent without its frames, and the window logs the first such reply.
    [Test]
    public async Task AReplyThatWaitsTooLongIsLoggedOnceAsync()
    {
        var log = new LogCapture();
        using var transport = new StalledTransport();
        var shell = new ShellModel();
        var page = new PageModel(shell);
        BridgeDispatcher.For(transport).Configure(WindowContentSessionOptions.DefaultMaximumInputBytes, log, TimeProvider.System);
        BridgeReplyOrder.For(transport).WaitLimit = TimeSpan.FromMilliseconds(100);
        using var shellBridge = new ShellBridge(transport, shell);
        using var pageBridge = new PageBridge(transport, page);
        _ = transport.Request("""{"kind":"snapshot","route":"shell"}""");
        _ = transport.Request("""{"kind":"snapshot","route":"page"}""");
        try
        {
            for (var edit = 1; edit <= 2; edit++)
                using (var reply = JsonDocument.Parse(await transport.RequestAsync(
                    $$"""{"kind":"set","route":"page","member":"value","value":{{edit}},"edit":{{edit}}}""")))
                    Require(reply, "set value");
        }
        finally { transport.Release.Set(); }
        var warnings = log.Warnings;
        if (warnings.Count != 1 || !warnings[0].StartsWith("1024 ", StringComparison.Ordinal)
            || !warnings[0].Contains("route page", StringComparison.Ordinal) || !warnings[0].EndsWith("deliver: shell. The page did not take pushes, or code blocked a route's delivery. Logged once per window.", StringComparison.Ordinal))
            throw new InvalidOperationException($"The reply wait was not logged once: [{string.Join("; ", warnings)}]");
    }

    [Test]
    public async Task AFrameOnItsWayWhenTheWindowClosesEndsSilentlyAsync()
    {
        var log = new LogCapture();
        var transport = new ClosingTransport();
        var capturing = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var delivery = new BridgeSnapshotDelivery(transport, "shell", BridgeModelTurn.For(new ShellModel()), () =>
        {
            capturing.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return """{"kind":"state","route":"shell","revision":1,"state":{"value":1}}"""u8.ToArray();
        }, static () => { }, nameof(ShellModel), log);
        transport.Hello();
        delivery.RequestState();
        if (!capturing.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The drain did not capture the state.");
        // A closing window releases its bridges, then disposes its transport, while the drain holds a state.
        delivery.Dispose();
        transport.Dispose();
        release.Set();
        await transport.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(static _ => { });
        await Task.Delay(100);
        if (log.Errors.Count != 0)
            throw new InvalidOperationException($"A drain after the transport was disposed logged: {string.Join("; ", log.Errors)}");
    }

    private const string Bump = """{"kind":"command","route":"page","member":"bump"}""";

    private static void Require(JsonDocument reply, string route)
    {
        if (!reply.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"{route} failed: {reply.RootElement}");
    }

    private sealed class Window : IAsyncDisposable
    {
        private readonly IRunicModelContextLease _ownership;
        private readonly ShellBridge _shellBridge;
        private readonly PageBridge _pageBridge;

        private Window(RunicModelContext context)
        {
            Context = context;
            Shell = new ShellModel();
            Page = new PageModel(Shell);
            _ownership = RunicModelContextRegistry.Shared.Bind(context, Shell, Page);
            Transport = new SlowPageTransport();
            _shellBridge = new ShellBridge(Transport, Shell);
            _pageBridge = new PageBridge(Transport, Page);
        }

        public RunicModelContext Context { get; }
        public ShellModel Shell { get; }
        public PageModel Page { get; }
        public SlowPageTransport Transport { get; }

        public static async Task<Window> OpenAsync()
        {
            var window = new Window(new RunicModelContext());
            // The initial state of each route was read by a snapshot call; no frame is outstanding.
            _ = window.Transport.Request("""{"kind":"snapshot","route":"shell"}""");
            _ = window.Transport.Request("""{"kind":"snapshot","route":"page"}""");
            await Task.CompletedTask;
            return window;
        }

        public void RequireShellValue(int value, string call)
        {
            if (!Transport.ShellValues.Contains(value))
                throw new InvalidOperationException(
                    $"The reply of {call} reached the page before the shell state it changed (shell frames: [{string.Join(", ", Transport.ShellValues)}]).");
        }

        public async Task WaitForShellValueAsync(int value)
        {
            var started = TimeProvider.System.GetTimestamp();
            while (!Transport.ShellValues.Contains(value))
            {
                if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("The shell state was not published.");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _pageBridge.Dispose();
            _shellBridge.Dispose();
            _ownership.Dispose();
            Transport.Dispose();
            await Context.DisposeAsync();
        }
    }

    // The page takes each push slowly, as a busy WebView does. The shell's values are recorded once they were taken.
    private sealed class SlowPageTransport : PushTestTransport
    {
        private readonly List<int> _shellValues = [];
        public int[] ShellValues { get { lock (_shellValues) return [.. _shellValues]; } }
        protected override async ValueTask OnBroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            int? value;
            string? route;
            using (var push = JsonDocument.Parse(message))
            {
                var root = push.RootElement;
                route = root.GetProperty("route").GetString();
                value = root.GetProperty("kind").GetString() switch
                {
                    "state" => root.GetProperty("state").GetProperty("value").GetInt32(),
                    "frame" => root.GetProperty("changes").EnumerateArray()
                        .Where(change => change.GetProperty("field").GetString() == "value" && change.GetProperty("kind").GetString() == "set")
                        .Select(change => (int?)change.GetProperty("value").GetInt32()).FirstOrDefault(),
                    _ => null,
                };
            }
            await Task.Delay(200, cancellationToken);
            if (route == "shell" && value is { } shell) lock (_shellValues) _shellValues.Add(shell);
        }
    }

    // Throws ObjectDisposedException once disposed, as CS-WebUI's transport did while it published states.
    private sealed class ClosingTransport : PushTestTransport
    {
        private volatile bool _disposed;
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void OnBroadcast(string json)
        {
            Attempted.TrySetResult();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        // The window disposes its transport while a push is on its way: it keeps receiving pushes, which then fail as a
        // disposed transport's did.
        public override void Dispose()
        {
            _disposed = true;
            base.Dispose();
        }
    }

    private sealed class ShellModel : INotifyPropertyChanged
    {
        private int _value;
        public int Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // A page whose calls change the shell, as a page that loads documents into its shell's list does.
    private sealed class PageModel(ShellModel shell) : INotifyPropertyChanged
    {
        public ShellModel Shell { get; } = shell;
        public ICommand Bump { get; } = new Command(() => shell.Value++);
        public int Value
        {
            get => Shell.Value;
            set => Shell.Value = value;
        }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class Command(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    private sealed class ShellBridge(IBridgeTransport transport, ShellModel model) : ViewModelBridge<ShellModel>(
        transport, model, "shell",
        [new BridgeStateMember<ShellModel>("value", static (writer, vm) => writer.WriteNumberValue(vm.Value))], [], [], []);

    private sealed class PageBridge(IBridgeTransport transport, PageModel model) : ViewModelBridge<PageModel>(
        transport, model, "page",
        [new BridgeStateMember<PageModel>("value", static (writer, _) => writer.WriteNumberValue(0))],
        [new("Value", "value", vm => vm.Value, (vm, value) => vm.Value = BridgeWire.ReadInt32(value))], [],
        [
            new("Bump", vm => vm.Bump),
            new("Load", vm => vm.Bump)
            {
                ExecuteAsync = static async (vm, _, _) =>
                {
                    vm.Shell.Value++;
                    await Task.Yield();
                },
            },
        ]);

    // Takes no push until Release is set, as a page that stopped reading does.
    private sealed class StalledTransport : PushTestTransport
    {
        public ManualResetEventSlim Release { get; } = new();
        protected override void OnBroadcast(string json) => Release.Wait(TimeSpan.FromSeconds(5));
        public override void Dispose()
        {
            Release.Set();
            base.Dispose();
        }
    }

    private sealed class LogCapture : ILogger
    {
        private readonly List<string> _errors = [];
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Errors { get { lock (_errors) return [.. _errors]; } }
        public IReadOnlyList<string> Warnings { get { lock (_warnings) return [.. _warnings]; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) lock (_errors) _errors.Add($"{eventId.Id} {formatter(state, exception)}");
            else if (logLevel == LogLevel.Warning) lock (_warnings) _warnings.Add($"{eventId.Id} {formatter(state, exception)}");
        }
    }
}

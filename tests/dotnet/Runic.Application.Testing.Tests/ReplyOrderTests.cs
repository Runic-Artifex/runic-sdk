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
        using var reply = JsonDocument.Parse(await window.Transport.CallAsync("pageLoad"));
        Require(reply, "pageLoad");
        window.RequireShellValue(1, "the awaited command pageLoad");
    }

    [Test]
    public async Task ASynchronousCommandRepliesAfterTheShellStateItChangedAsync()
    {
        await using var window = await Window.OpenAsync();
        using var reply = JsonDocument.Parse(await Task.Run(() => window.Transport.Call("pageBump")));
        Require(reply, "pageBump");
        window.RequireShellValue(1, "the synchronous command pageBump");
    }

    [Test]
    public async Task ASetterRepliesAfterTheShellStateItChangedAsync()
    {
        await using var window = await Window.OpenAsync();
        using var reply = JsonDocument.Parse(await Task.Run(() => window.Transport.Call("pageSetValue", new(Int64Value: 7))));
        Require(reply, "pageSetValue");
        window.RequireShellValue(7, "the setter pageSetValue");
    }

    // A synchronous route called inside the model turn cannot wait for a capture that needs that turn. It replies
    // at once instead of blocking for the wait limit; the state follows.
    [Test]
    public async Task ASynchronousCallInsideTheModelTurnRepliesWithoutWaitingAsync()
    {
        await using var window = await Window.OpenAsync();
        var started = TimeProvider.System.GetTimestamp();
        var reply = await window.Context.InvokeAsync(() => window.Transport.Call("pageBump"));
        using (var document = JsonDocument.Parse(reply)) Require(document, "pageBump");
        if (TimeProvider.System.GetElapsedTime(started) >= BridgeReplyOrder.ReplyWaitLimit)
            throw new InvalidOperationException("A call inside the model turn waited for a capture that needs that turn.");
        await window.WaitForShellValueAsync(1);
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
            return """{"value":1}""";
        }, nameof(ShellModel), log);
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
            _ = window.Transport.Call("shellSnapshot");
            _ = window.Transport.Call("pageSnapshot");
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

    // The page takes each frame slowly, as a busy WebView does. Frames are recorded once they were taken.
    private sealed class SlowPageTransport : LegacyTestTransport
    {
        private readonly List<int> _shellValues = [];
        public int[] ShellValues { get { lock (_shellValues) return [.. _shellValues]; } }
        protected override async ValueTask PublishUtf8Async(string name, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken)
        {
            using var state = JsonDocument.Parse(stateUtf8);
            var value = state.RootElement.GetProperty("value").GetInt32();
            await Task.Delay(200, cancellationToken);
            if (name == "shell") lock (_shellValues) _shellValues.Add(value);
        }
    }

    // Throws ObjectDisposedException once disposed, as CS-WebUI's transport did while it published states.
    private sealed class ClosingTransport : LegacyTestTransport
    {
        private volatile bool _disposed;
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Publish(string name, string stateJson)
        {
            Attempted.TrySetResult();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        // The window disposes its transport while a publication is on its way: it keeps receiving publications, which
        // then fail as a disposed transport's did.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215", Justification = "The publisher must stay attached.")]
        public override void Dispose() => _disposed = true;
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
        transport, model, "shell", static (writer, vm, _, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", vm.Value);
            writer.WriteEndObject();
        }, [], [], []);

    private sealed class PageBridge(IBridgeTransport transport, PageModel model) : ViewModelBridge<PageModel>(
        transport, model, "page", static (writer, _, _, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", 0);
            writer.WriteEndObject();
        }, [new("Value", vm => vm.Value, (vm, args) => vm.Value = checked((int)args.GetInt64()))], [],
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

    private sealed class LogCapture : ILogger
    {
        private readonly List<string> _errors = [];
        public IReadOnlyList<string> Errors { get { lock (_errors) return [.. _errors]; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) lock (_errors) _errors.Add($"{eventId.Id} {formatter(state, exception)}");
        }
    }
}

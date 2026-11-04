using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// D4: a CommunityToolkit ViewModel of a multi-client window that shows who is viewing, records the caller of its
// commands and keeps a draft per connection.
public sealed partial class ConnectionsViewModel : ObservableObject, IDisposable
{
    private readonly IRunicConnections _connections;
    private readonly RunicConnectionState<ConnectionDraft> _drafts;

    public ConnectionsViewModel(IRunicConnections connections)
    {
        _connections = connections;
        _drafts = new RunicConnectionState<ConnectionDraft>(connections, connection => new ConnectionDraft(connection));
        connections.Connected += (_, _) => Viewers = connections.Current.Count;
        connections.Disconnected += (_, e) =>
        {
            Viewers = connections.Current.Count;
            Left.Add(e.Connection);
        };
    }

    [ObservableProperty]
    public partial int Viewers { get; private set; }

    internal List<RunicConnection?> Callers { get; } = [];
    internal List<RunicConnection> Left { get; } = [];
    internal RunicConnectionState<ConnectionDraft> Drafts => _drafts;
    internal IRunicConnections Connections => _connections;

    [RelayCommand]
    private void Touch() => Callers.Add(_connections.Caller);

    // The caller stays the same after an await, on another thread.
    [RelayCommand]
    private async Task TouchLaterAsync()
    {
        await Task.Run(() => Task.Delay(1)).ConfigureAwait(false);
        lock (Callers) Callers.Add(_connections.Caller);
    }

    [RelayCommand]
    private void Type(string text)
    {
        if (_drafts.TryGetCaller(out var draft)) draft.Text = text;
    }

    // A command that awaits until the test releases it, then reads its caller and the caller's draft.
    internal TaskCompletionSource HoldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource HoldGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<(RunicConnection? Caller, bool HasDraft)> HoldDone { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand]
    private async Task HoldAsync()
    {
        HoldStarted.TrySetResult();
        await HoldGate.Task.ConfigureAwait(false);
        HoldDone.TrySetResult((_connections.Caller, _drafts.TryGetCaller(out _)));
    }

    // Work the command starts and that outlives it: it reads its caller and asks the page after the command ended.
    internal TaskCompletionSource BackgroundGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<(RunicConnection? Caller, RunicConnection? InCommand, bool Answer)>? Background { get; private set; }

    [RelayCommand]
    private void StartBackground()
    {
        var inCommand = _connections.Caller;
        Background = Task.Run(async () =>
        {
            await BackgroundGate.Task.ConfigureAwait(false);
            var answer = await Confirm.AskAsync(true).ConfigureAwait(false);
            return (_connections.Caller, inCommand, answer);
        });
    }

    // A page answers true; without a single page to ask, the .NET fallback answers false.
    public ViewQuestion<bool, bool> Confirm { get; } = new((_, _) => ValueTask.FromResult(false));

    public void Dispose() => _drafts.Dispose();
}

public sealed class ConnectionDraft(RunicConnection connection) : IDisposable
{
    public RunicConnection Connection { get; } = connection;
    public string Text { get; set; } = "";
    public bool Disposed { get; private set; }
    public bool ThrowOnDispose { get; set; }

    public void Dispose()
    {
        Disposed = true;
        if (ThrowOnDispose) throw new InvalidOperationException("draft");
    }
}

public sealed partial class ConnectionsWindow : RunicWindow<ConnectionsViewModel>;

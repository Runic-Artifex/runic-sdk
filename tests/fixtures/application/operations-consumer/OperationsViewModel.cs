using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;
using Runic.Navigation;
using SharedContracts;
using System.Text.Json.Serialization;

namespace OperationsConsumer;

public sealed class OperationsViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly IRunicModelContext _context;
    private readonly IRunicModelContextLease _lease;
    private readonly AcceptedWorkScope _accepted = new();
    private readonly TaskCompletionSource _recoveryRelease = NewGate();
    private TaskCompletionSource _readRelease = NewGate();
    private Task _readTask = Task.CompletedTask;
    private CancellationTokenSource? _longCancellation;
    private CancellationTokenSource? _readCancellation;
    private string _draft = "", _selected = "";
    private string[] _readAccepted = [];
    private int _session, _cancelReadCalls;
    private bool _longActive, _readActive, _mutationActive, _recoveryStarted, _recoveryPublished;

    public OperationsViewModel(IRunicModelContext context, ISequencer scheduler)
    {
        _context = context;
        _lease = RunicModelContextRegistry.Shared.Bind(context, this);
        LongCommand = ReactiveCommand.CreateFromTask<RxVoid, string>((_, token) => LongAsync(token), scheduler);
        CancelLongCommand = ReactiveCommand.Create(() => _longCancellation?.Cancel(), scheduler);
        SetDraftCommand = ReactiveCommand.Create<string>(value => Draft = value, scheduler);
        ReadCommand = ReactiveCommand.CreateFromTask<string, string>((label, token) =>
        {
            var task = _accepted.RunAsync(() => ReadAsync(label, token));
            _readTask = task;
            return task;
        }, scheduler);
        CancelReadCommand = ReactiveCommand.Create(() =>
        {
            CancelReadCalls++;
            _readCancellation?.Cancel();
        }, scheduler);
        ReleaseReadCommand = ReactiveCommand.Create(() => _readRelease.TrySetResult(), scheduler);
        WaitReadDrainedCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            try { await _readTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }, scheduler);
        ReplaceSessionCommand = ReactiveCommand.Create(() =>
        {
            Session++;
            _readRelease.TrySetResult();
            Selected = "";
        }, scheduler);
        MutationCommand = ReactiveCommand.CreateFromTask<RxVoid, string>((_, token) =>
            _accepted.RunAsync(() => MutateAsync(token)), scheduler);
    }

    public HistoryQuery Query { get; } = new("history", "author", null);
    [JsonIgnore] public string RootVisible => "root-visible";
    public string Draft { get => _draft; private set => this.RaiseAndSetIfChanged(ref _draft, value); }
    public string Selected { get => _selected; private set => this.RaiseAndSetIfChanged(ref _selected, value); }
    public string[] ReadAccepted { get => _readAccepted; private set => this.RaiseAndSetIfChanged(ref _readAccepted, value); }
    public int Session { get => _session; private set => this.RaiseAndSetIfChanged(ref _session, value); }
    public int CancelReadCalls { get => _cancelReadCalls; private set => this.RaiseAndSetIfChanged(ref _cancelReadCalls, value); }
    public bool LongActive { get => _longActive; private set => this.RaiseAndSetIfChanged(ref _longActive, value); }
    public bool ReadActive { get => _readActive; private set => this.RaiseAndSetIfChanged(ref _readActive, value); }
    public bool MutationActive { get => _mutationActive; private set => this.RaiseAndSetIfChanged(ref _mutationActive, value); }
    public bool RecoveryStarted { get => _recoveryStarted; private set => this.RaiseAndSetIfChanged(ref _recoveryStarted, value); }
    public bool RecoveryPublished { get => _recoveryPublished; private set => this.RaiseAndSetIfChanged(ref _recoveryPublished, value); }
    [RunicIgnore] public bool Disposed { get; private set; }
    public ReactiveCommand<RxVoid, string> LongCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelLongCommand { get; }
    public ReactiveCommand<string, RxVoid> SetDraftCommand { get; }
    public ReactiveCommand<string, string> ReadCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelReadCommand { get; }
    public ReactiveCommand<RxVoid, bool> ReleaseReadCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> WaitReadDrainedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ReplaceSessionCommand { get; }
    public ReactiveCommand<RxVoid, string> MutationCommand { get; }

    private async Task<string> LongAsync(CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _longCancellation = cancellation;
        await _context.InvokeAsync(() => LongActive = true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token).ConfigureAwait(false);
            return "finished";
        }
        finally
        {
            _longCancellation = null;
            await _context.InvokeAsync(() => LongActive = false);
        }
    }

    private async Task<string> ReadAsync(string label, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _readCancellation = cancellation;
        var session = Session;
        _readRelease = NewGate();
        await _context.InvokeAsync(() =>
        {
            ReadActive = true;
            ReadAccepted = [.. ReadAccepted, label];
        });
        try
        {
            if (label.StartsWith("held:", StringComparison.Ordinal))
                await _readRelease.Task.ConfigureAwait(false); // Deliberately finishes independently of the invocation wrapper.
            cancellation.Token.ThrowIfCancellationRequested();
            await _context.InvokeAsync(() => { if (session == Session) Selected = label; });
            return label;
        }
        finally
        {
            _readCancellation = null;
            await _context.InvokeAsync(() => ReadActive = false);
        }
    }

    private async Task<string> MutateAsync(CancellationToken token)
    {
        await _context.InvokeAsync(() => MutationActive = true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "finished";
        }
        catch (OperationCanceledException)
        {
            await _context.InvokeAsync(() => RecoveryStarted = true);
            await _recoveryRelease.Task.ConfigureAwait(false);
            await _context.InvokeAsync(() => RecoveryPublished = true);
            throw;
        }
        finally { await _context.InvokeAsync(() => MutationActive = false); }
    }

    public void ReleaseRecovery() => _recoveryRelease.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        await _accepted.DisposeAsync();
        if (!RecoveryPublished || MutationActive)
            throw new InvalidOperationException("Accepted recovery was not complete before scope disposal.");
        foreach (var command in new IDisposable[] { LongCommand, CancelLongCommand, SetDraftCommand, ReadCommand,
            CancelReadCommand, ReleaseReadCommand, WaitReadDrainedCommand, ReplaceSessionCommand, MutationCommand }) command.Dispose();
        _lease.Dispose();
        Disposed = true;
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

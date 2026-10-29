using System.Globalization;
using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace ReactiveUi25AotProof;

public sealed record AotSaveRequest(string DocumentId, long ExpectedVersion, decimal Amount, DateTime When);
public sealed record AotSaveResult(string DocumentId, long SavedVersion, decimal Amount, DateTime When);
public sealed record AotStateItem(string? Label, long ExactId, DateOnly Day, TimeSpan Duration);

public sealed class AotValidationItem : INotifyDataErrorInfo
{
    public string Text { get; set; } = string.Empty;
    [RunicIgnore]
    public bool HasErrors => true;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
    public IEnumerable GetErrors(string? propertyName) => propertyName == nameof(Text)
        ? new[] { new RunicValidationMessage("Required", "required", "error", [nameof(Text)]) }
        : Array.Empty<RunicValidationMessage>();
}

public sealed class AotProofViewModel : ReactiveObject
{
    public AotProofViewModel(IRunicModelContext context)
    {
        SaveCommand = ReactiveCommand.CreateFromTask<AotSaveRequest, AotSaveResult>(request => Task.FromResult(
            new AotSaveResult(request.DocumentId, request.ExpectedVersion + 1, request.Amount, request.When)),
            ReactiveSchedulers.For(context));
    }

    public long ExactId { get; set; } = 9_007_199_254_740_993;
    public decimal Amount { get; set; } = 1234567890.123456789m;
    public int? Optional { get; set; }
    public AotValidationItem ValidationItem { get; } = new();
    public DateTime When { get; set; } = new(2026, 9, 28, 10, 11, 12, DateTimeKind.Unspecified);
    public IReadOnlyList<AotStateItem> Items { get; } = [new("item", 9_007_199_254_740_993, new DateOnly(2026, 9, 28), TimeSpan.FromTicks(123456789))];
    public IReactiveCommand<AotSaveRequest, AotSaveResult> SaveCommand { get; }
}

public sealed partial class AotProofWindow : RunicWindow<AotProofViewModel>;

// The window's transport without a page: the proof sends Views protocol 4 requests to the window's request handler
// on one connection, as a page does.
internal sealed class AotTransport : IBridgeTransport
{
    private IBridgeRequestHandler? _handler;

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _handler = handler;
        return new Lease(() => _handler = null);
    }

    // The pushes the window broadcast, as JSON.
    public System.Collections.Concurrent.ConcurrentQueue<string> Pushes { get; } = new();

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        Pushes.Enqueue(System.Text.Encoding.UTF8.GetString(message.Span));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<string> RequestAsync(string json)
    {
        var handler = _handler ?? throw new InvalidOperationException("No window serves the transport.");
        var reply = await handler.HandleAsync(new BridgeRequest(System.Text.Encoding.UTF8.GetBytes(json), "client", "connection"),
            CancellationToken.None).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(reply.Span);
    }

    private sealed class Lease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

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
        ? new[] { new BridgeValidationMessage("Required", "required", "error", [nameof(Text)]) }
        : Array.Empty<BridgeValidationMessage>();
}

public sealed class AotProofViewModel : ReactiveObject
{
    public AotProofViewModel(IRunicModelContext context)
    {
        SaveCommand = ReactiveCommand.CreateFromTask<AotSaveRequest, AotSaveResult>(request => Task.FromResult(
            new AotSaveResult(request.DocumentId, request.ExpectedVersion + 1, request.Amount, request.When)),
            new RunicReactiveSchedulerProvider().For(context));
    }

    public long ExactId { get; set; } = 9_007_199_254_740_993;
    public decimal Amount { get; set; } = 1234567890.123456789m;
    public int? Optional { get; set; }
    public AotValidationItem ValidationItem { get; } = new();
    public DateTime When { get; set; } = new(2026, 9, 28, 10, 11, 12, DateTimeKind.Unspecified);
    public IReadOnlyList<AotStateItem> Items { get; } = [new("item", 9_007_199_254_740_993, new DateOnly(2026, 9, 28), TimeSpan.FromTicks(123456789))];
    public IReactiveCommand<AotSaveRequest, AotSaveResult> SaveCommand { get; }
}

public sealed partial class AotProofWindow(AotProofViewModel model) : RunicWindow<AotProofViewModel>(model);

internal sealed class AotArguments(string json) : IBridgeArguments
{
    public long GetInt64() => throw new InvalidOperationException();
    public bool GetBoolean() => throw new InvalidOperationException();
    public string GetString() => json;
}

internal sealed class AotTransport : IBridgeTransport
{
    private readonly Dictionary<string, Func<IBridgeArguments, string>> _sync = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IBridgeArguments, CancellationToken, ValueTask<string>>> _async = new(StringComparer.Ordinal);
    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) { _sync.Add(name, handler); return new Lease(() => _sync.Remove(name)); }
    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) { _async.Add(name, handler); return new Lease(() => _async.Remove(name)); }
    public void Publish(string name, string stateJson) { }
    public string Call(string name, string json = "null") => _sync[name](new AotArguments(json));
    public ValueTask<string> CallAsync(string name, string json) => _async[name](new AotArguments(json), CancellationToken.None);
    private sealed class Lease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

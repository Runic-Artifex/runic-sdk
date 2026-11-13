using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// sdk#900 (W4a): a Window whose operations declare what its close does to them. The class's Cancel covers its read-only
// operations, and the properties override it.
public sealed partial class DrainingWindow : RunicWindow<DrainingViewModel>
{
    public DrainingWindow() => GuardClose = true;

    protected override ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ViewModel.GuardAsked);
        return ValueTask.FromResult(ViewModel.AllowClose);
    }
}

// The application work a test shares between Windows (IRunicWindowContext.DrainOnClose), or none.
public sealed class DrainingSettings
{
    public AcceptedWorkScope? Shared { get; init; }
}

[RunicOperationClose(RunicOperationClose.Cancel)]
public sealed partial class DrainingViewModel : ObservableObject
{
    private readonly IRunicWindowContext _window;

    public DrainingViewModel(IRunicWindowContext window, DrainingSettings settings)
    {
        _window = window;
        // From the constructor, before the Window's content session attached.
        if (settings.Shared is { } shared) window.DrainOnClose(shared);
    }

    internal RetireWork Query { get; } = new("Query");
    internal RetireWork Refresh { get; } = new("Refresh");
    internal RetireWork Save { get; } = new("Save");
    internal TaskCompletionSource<bool> CloseRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool AllowClose { get; set; } = true;
    internal int GuardAsked;

    [ObservableProperty]
    private string _status = "idle";

    // The class's Cancel: a read-only query.
    [RelayCommand]
    private Task QueryAsync(CancellationToken cancellationToken) => Query.RunAsync(cancellationToken);

    // Default, which overrides the class's Cancel.
    [RelayCommand]
    [property: RunicOperationClose(RunicOperationClose.Default)]
    private Task RefreshAsync(CancellationToken cancellationToken) => Refresh.RunAsync(cancellationToken);

    // Durable: a change to an external system; it reports its progress through the Window's state.
    [RelayCommand]
    [property: RunicOperationClose(RunicOperationClose.Durable)]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Status = "saving";
        await Save.RunAsync(cancellationToken);
        Status = "saved";
    }

    // "Save and close": accepted work that closes its own Window.
    [RelayCommand]
    [property: RunicOperationClose(RunicOperationClose.Durable)]
    private async Task SaveAndCloseAsync(CancellationToken cancellationToken)
    {
        Status = "closing";
        CloseRequested.TrySetResult(await _window.RequestCloseAsync(cancellationToken));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// View requests and view questions (specs/application, "View requests") in both MVVM flavors. BridgeCodegen
// generates their Bridges when this project builds, so the build proves that the generated C# compiles.

/// <summary>The field a page focuses.</summary>
public sealed record ViewRequestFocusTarget(string Field, bool SelectAll);

/// <summary>What a discard question shows.</summary>
public sealed record ViewRequestDiscard(string Title);

public sealed class ReactiveViewRequestViewModel : ReactiveObject, IDisposable
{
    private string _title = "Draft";

    public ReactiveViewRequestViewModel() => DiscardCommand = ReactiveCommand.CreateFromTask(DiscardAsync);

    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }

    /// <summary>Focuses a field of the editor.</summary>
    public ViewRequest<ViewRequestFocusTarget> Focus { get; } = new();

    /// <summary>Shows a toast in every pane.</summary>
    public ViewRequest<string?> Notify { get; } = new(ViewRequestDelivery.Broadcast);

    /// <summary>Asks whether to discard the draft.</summary>
    public ViewQuestion<ViewRequestDiscard, bool> ConfirmDiscard { get; } = new((_, _) => ValueTask.FromResult(false));

    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }

    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        if (await ConfirmDiscard.AskAsync(new ViewRequestDiscard(Title), cancellationToken).ConfigureAwait(false)) Title = "";
    }

    public void Dispose() => DiscardCommand.Dispose();
}

public sealed partial class ReactiveViewRequestWindow : RunicWindow<ReactiveViewRequestViewModel>;

public sealed partial class ToolkitViewRequestViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "Draft";

    /// <summary>Focuses a field of the editor.</summary>
    public ViewRequest<ViewRequestFocusTarget> Focus { get; } = new(ViewRequestDelivery.NextMounted);

    /// <summary>Asks whether to discard the draft.</summary>
    public ViewQuestion<ViewRequestDiscard, bool> ConfirmDiscard { get; } = new();

    [RelayCommand]
    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        if (await ConfirmDiscard.AskAsync(new ViewRequestDiscard(Title), cancellationToken).ConfigureAwait(false)) Title = "";
    }
}

public sealed partial class ToolkitViewRequestWindow : RunicWindow<ToolkitViewRequestViewModel>;

/// <summary>Only view requests: a ViewModel needs no state, command or interaction besides them.</summary>
public sealed class RequestOnlyViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public ViewRequest<ViewRequestFocusTarget> Focus { get; } = new();
}

public sealed partial class RequestOnlyWindow : RunicWindow<RequestOnlyViewModel>;

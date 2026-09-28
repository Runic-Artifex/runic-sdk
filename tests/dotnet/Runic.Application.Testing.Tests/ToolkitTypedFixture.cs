using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public sealed record ToolkitCommandRequest(string DocumentId, int Delta);

/// <summary>
/// Public Toolkit command shapes consumed by BridgeCodegen. Keep properties
/// typed as interfaces: inspecting only RelayCommand's concrete type would
/// miss ordinary CommunityToolkit MVVM applications.
/// </summary>
public sealed class ToolkitTypedViewModel : ObservableObject
{
    private int _total;
    private string? _label;
    private string? _optionalDocumentId;
    private string? _asyncDocumentId;
    private string? _asyncLabel;
    private string? _asyncOptionalDocumentId;
    private int _cancelledCount;

    public ToolkitTypedViewModel()
    {
        ApplyCommand = new RelayCommand<ToolkitCommandRequest>(request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            Total += request.Delta;
        }, request => request is { DocumentId: "document-1", Delta: > 0 and <= 10 });
        AddCommand = new RelayCommand<int>(delta => Total += delta, delta => delta is > 0 and <= 10);
        RenameCommand = new RelayCommand<string?>(label => Label = label,
            label => !string.IsNullOrWhiteSpace(label));
        OptionalCommand = new RelayCommand<ToolkitCommandRequest?>(request => OptionalDocumentId = request?.DocumentId);

        AsyncDtoCommand = new AsyncRelayCommand<ToolkitCommandRequest>(async (request, token) =>
        {
            ArgumentNullException.ThrowIfNull(request);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            AsyncDocumentId = request.DocumentId;
            Total += request.Delta;
        }, request => request is { DocumentId: "document-1", Delta: > 0 and <= 10 });
        AsyncIntCommand = new AsyncRelayCommand<int>(async (delta, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Total += delta;
        }, delta => delta is > 0 and <= 10);
        AsyncStringCommand = new AsyncRelayCommand<string?>(async (label, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            AsyncLabel = label;
        }, label => !string.IsNullOrWhiteSpace(label));
        AsyncOptionalCommand = new AsyncRelayCommand<ToolkitCommandRequest?>(async (request, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            AsyncOptionalDocumentId = request?.DocumentId;
        });
        CancelCommand = new AsyncRelayCommand<string>(WaitForCancellationAsync,
            value => value == "wait");
    }

    public int Total { get => _total; private set => SetProperty(ref _total, value); }
    public string? Label { get => _label; private set => SetProperty(ref _label, value); }
    public string? OptionalDocumentId { get => _optionalDocumentId; private set => SetProperty(ref _optionalDocumentId, value); }
    public string? AsyncDocumentId { get => _asyncDocumentId; private set => SetProperty(ref _asyncDocumentId, value); }
    public string? AsyncLabel { get => _asyncLabel; private set => SetProperty(ref _asyncLabel, value); }
    public string? AsyncOptionalDocumentId { get => _asyncOptionalDocumentId; private set => SetProperty(ref _asyncOptionalDocumentId, value); }
    public int CancelledCount { get => _cancelledCount; private set => SetProperty(ref _cancelledCount, value); }

    public IRelayCommand<ToolkitCommandRequest> ApplyCommand { get; }
    public IRelayCommand<int> AddCommand { get; }
    public IRelayCommand<string?> RenameCommand { get; }
    public IRelayCommand<ToolkitCommandRequest?> OptionalCommand { get; }
    public IAsyncRelayCommand<ToolkitCommandRequest> AsyncDtoCommand { get; }
    public IAsyncRelayCommand<int> AsyncIntCommand { get; }
    public IAsyncRelayCommand<string?> AsyncStringCommand { get; }
    public IAsyncRelayCommand<ToolkitCommandRequest?> AsyncOptionalCommand { get; }
    public IAsyncRelayCommand<string> CancelCommand { get; }

    private async Task WaitForCancellationAsync(string? _, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelledCount++;
            throw;
        }
    }
}

public sealed partial class ToolkitTypedWindow(ToolkitTypedViewModel model)
    : RunicWindow<ToolkitTypedViewModel>(model);

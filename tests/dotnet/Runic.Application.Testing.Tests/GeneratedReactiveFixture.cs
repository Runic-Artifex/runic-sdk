using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Exercises the ReactiveUI 25 generators before the Runic bridge's compiled
/// model inspection runs. The bridge only sees the generated public members.
/// </summary>
public sealed partial class GeneratedReactiveViewModel : ReactiveObject, IDisposable
{
    private readonly IObservable<bool> _canSave;

    public GeneratedReactiveViewModel()
    {
        _canSave = this.WhenAnyValue(model => model.Name)
            .Select(static name => !string.IsNullOrWhiteSpace(name));
        _summaryHelper = this.WhenAnyValue(model => model.Name)
            .Select(static name => $"summary:{name}")
            .ToProperty(this, model => model.Summary);
    }

    [Reactive]
    public partial string Name { get; set; } = string.Empty;

    [ObservableAsProperty]
    public partial string Summary { get; }

    public int SaveCount { get; private set; }

    [ReactiveCommand(CanExecute = nameof(_canSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;
        this.RaisePropertyChanged(nameof(SaveCount));
    }

    public void Dispose()
    {
        (_summaryHelper ?? throw new InvalidOperationException("The OAPH was not initialized.")).Dispose();
        SaveCommand.Dispose();
    }
}

public sealed partial class GeneratedReactiveWindow(GeneratedReactiveViewModel model)
    : RunicWindow<GeneratedReactiveViewModel>(model);

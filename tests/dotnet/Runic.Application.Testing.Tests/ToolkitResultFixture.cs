using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Application.Testing.Tests;

public sealed record ToolkitEstimateChoice(int Retention);

/// <summary>What keeping the newest restore points frees.</summary>
public sealed record ToolkitEstimate(int Retention, double Bytes);

// [RelayCommand] methods that return Task<T> deliver T as their operation result (#312).
public sealed partial class ToolkitResultViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int Runs { get; private set; }

    /// <summary>Measures what keeping <paramref name="choice"/> frees.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task<ToolkitEstimate> EstimateAsync(ToolkitEstimateChoice choice, CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        Runs++;
        return new(choice.Retention, choice.Retention * 1024.5);
    }

    [RelayCommand]
    private Task<string?> LabelAsync() => Task.FromResult<string?>(Runs == 0 ? null : $"runs {Runs}");

    // Without a result: still a BridgeOperation<void>.
    [RelayCommand]
    private Task RefreshAsync()
    {
        OnPropertyChanged(nameof(Runs));
        return Task.CompletedTask;
    }
}

public sealed partial class ToolkitResultWindow
    : Runic.Application.Views.RunicWindow<ToolkitResultViewModel>;

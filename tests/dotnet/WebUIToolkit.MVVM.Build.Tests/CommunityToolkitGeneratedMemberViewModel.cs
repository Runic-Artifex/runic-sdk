using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.MVVM.Build.Tests.Fixtures;

/// <summary>A real CommunityToolkit producer whose generated public members are inspected as compiled PE metadata.</summary>
public partial class GeneratedMemberViewModel : ObservableValidator
{
    private readonly ObservableCollection<string> _readOnlyItems = ["read-only"];

    public GeneratedMemberViewModel()
    {
        ReadOnlyItems = new ReadOnlyObservableCollection<string>(_readOnlyItems);
    }

    [ObservableProperty]
    private string? title;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    private string? name = "valid";

    [ObservableProperty]
    private string requiredName = "required";

    // This deliberately has a keyword metadata name so the PE reader must escape it
    // before emitting direct-access C#.
    public string @class { get; set; } = string.Empty;

    public IReadOnlyList<string?> NullableItems { get; set; } = [];

    public ObservableCollection<string> Items { get; } = ["first"];

    public ReadOnlyObservableCollection<string> ReadOnlyItems { get; }

    public void AddReadOnlyItem(string item) => _readOnlyItems.Add(item);

    public IRelayCommand? OptionalCommand { get; set; }

    /// <summary>Gets the number of times the generated command invoked its target method.</summary>
    public int SubmissionCount { get; private set; }

    [RelayCommand]
    private void Submit() => SubmissionCount++;

    /// <summary>Gets the last strongly typed command parameter.</summary>
    public int MultipliedBy { get; private set; }

    /// <summary>Gets the last strongly typed asynchronous command parameter.</summary>
    public int ScaledBy { get; private set; }

    /// <summary>Signals that the cancellable generated command started.</summary>
    public TaskCompletionSource LoadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets whether the cancellable generated command observed cancellation.</summary>
    public bool LoadCanceled { get; private set; }

    [RelayCommand(CanExecute = nameof(CanMultiply))]
    private void Multiply(int value) => MultipliedBy = value;

    private static bool CanMultiply(int value) => value > 0;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        LoadStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadCanceled = true;
            throw;
        }
    }

    [RelayCommand]
    private async Task ScaleAsync(int value, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        ScaledBy = value;
    }
}

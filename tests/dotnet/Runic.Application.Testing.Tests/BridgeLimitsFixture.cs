using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Application.Testing.Tests;

// The Bridge's input limits: argument size, number ranges and concurrent awaited commands.
public sealed partial class BridgeLimitsViewModel : ObservableObject
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial System.Numerics.BigInteger Large { get; set; }

    public int Held => Volatile.Read(ref _held);

    public void Release() => _release.TrySetResult();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task HoldAsync()
    {
        Interlocked.Increment(ref _held);
        await _release.Task;
    }

    [RelayCommand]
    private void Echo(string value) => Text = value;

    [RelayCommand]
    private void Take(int value) => Count = value;
}

public sealed partial class BridgeLimitsWindow : Runic.Application.Views.RunicWindow<BridgeLimitsViewModel>;

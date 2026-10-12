using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// The Window that RunicWindowHostTests opens on a recording host. Fixtures compile in the bootstrap pass,
// so the Bridge generator sees this Window and its ViewModel like any application Window.
public sealed class ScopeProbe : IAsyncDisposable
{
    internal static int Live;

    public ScopeProbe() => Interlocked.Increment(ref Live);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    // Completes when the Window's scope is disposed.
    public Task Released => _released.Task;

    public ValueTask DisposeAsync()
    {
        if (!Disposed) Interlocked.Decrement(ref Live);
        Disposed = true;
        _released.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

public sealed class HostProbeViewModel : INotifyPropertyChanged
{
    internal static int Created;

    public HostProbeViewModel(ScopeProbe scope)
    {
        Scope = scope;
        Interlocked.Increment(ref Created);
    }

    internal ScopeProbe Scope { get; }

    public string Title { get; set; } = "probe";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class HostProbeWindow : RunicWindow<HostProbeViewModel>;

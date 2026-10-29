using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// Opens Windows on RunicWindowTestHost, so a test drives a Window's routes as its browser does. It has no close request
// of its own: RequestCloseAsync asks the Window's close guard and then closes it, and UserCloseAsync asks the guard as
// Runic Desktop's close button does.
internal sealed class TestWindowHost(IServiceProvider services) : IRunicWindowHost
{
    public IServiceProvider Services { get; } = services;

    public IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options) => [];

    public async ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening,
        CancellationToken cancellationToken) where TViewModel : class
    {
        try
        {
            var host = new RunicWindowTestHost<TViewModel>(opening.ViewModel,
                (transport, content, _) => opening.AttachBridge(transport, content),
                new RunicWindowTestHostOptions
                {
                    ViewLocator = new NavViewLocator(),
                    ModelContext = opening.Scope.ServiceProvider.GetRequiredService<IRunicModelContext>(),
                });
            return new TestWindow<TViewModel>(opening.Scope, host, opening.CloseGuard);
        }
        catch
        {
            await opening.Scope.DisposeAsync();
            throw;
        }
    }
}

internal sealed class TestWindow<TViewModel>(AsyncServiceScope scope, RunicWindowTestHost<TViewModel> host,
    Func<CancellationToken, ValueTask<bool>>? closeGuard) : IBridgeWindow where TViewModel : class
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<RunicWindowCloseResult>? _closing;

    public RunicWindowTestHost<TViewModel> Host { get; } = host;
    public bool IsOpen => !_closed.Task.IsCompleted;

    // The user's close button: the Window's close guard decides, then the window closes.
    public async Task<bool> UserCloseAsync()
    {
        if (closeGuard is not null && !await closeGuard(CancellationToken.None)) return false;
        await CloseAsync(TimeSpan.FromSeconds(5));
        return true;
    }

    public ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_gate) _closing ??= Task.Run(() => CloseCoreAsync(timeout));
        return new(_closing.WaitAsync(cancellationToken));
    }

    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        new(_closed.Task.WaitAsync(cancellationToken));

    public async ValueTask DisposeAsync() => await CloseAsync(TimeSpan.Zero);

    private async Task<RunicWindowCloseResult> CloseCoreAsync(TimeSpan timeout)
    {
        _closed.TrySetResult();
        var result = await Host.DrainAsync(timeout);
        Host.Dispose();
        await scope.DisposeAsync();
        return result;
    }
}

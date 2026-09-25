using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>
/// Owns one headless Window content session and its generated root attachment.
/// The caller retains ownership of the ViewModel and any dependency injection scope.
/// </summary>
public sealed class RunicWindowTestHost<TViewModel> : IDisposable where TViewModel : class
{
    private readonly IDisposable _attachment;
    private bool _disposed;

    public RunicWindowTestHost(TViewModel viewModel, string rootRoute,
        Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable> attach,
        IRunicViewLocator? viewLocator = null,
        CancellationToken operationShutdown = default)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRoute);
        ArgumentNullException.ThrowIfNull(attach);
        RootRoute = rootRoute;
        Transport = new InMemoryViewTransport();
        try
        {
            Content = new WindowContentSession(Transport, viewLocator, operationShutdown);
            IDisposable? attachment = null;
            try
            {
                _attachment = attach(Transport, Content, viewModel)
                    ?? throw new InvalidOperationException("The generated Bridge factory returned no attachment.");
                attachment = _attachment;
                if (!Transport.Routes.Contains($"{rootRoute}Snapshot"))
                    throw new InvalidOperationException($"The root snapshot route '{rootRoute}Snapshot' was not attached.");
            }
            catch
            {
                try { attachment?.Dispose(); }
                finally { Content.Dispose(); }
                throw;
            }
        }
        catch { Transport.Dispose(); throw; }
    }

    public TViewModel ViewModel { get; }
    public string RootRoute { get; }
    public InMemoryViewTransport Transport { get; }
    public WindowContentSession Content { get; }

    /// <summary>Reads a generated snapshot from the root route.</summary>
    public JsonDocument Snapshot() => JsonDocument.Parse(Transport.Call($"{RootRoute}Snapshot"));

    /// <summary>Reads a generated snapshot from a routed View.</summary>
    public JsonDocument Snapshot(PageReference reference) =>
        JsonDocument.Parse(Transport.Call($"content{reference.Id}Snapshot"));

    /// <summary>Acknowledges that a frontend View was mounted.</summary>
    public string Mount(PageReference reference, string token,
        string? clientKey = null, string? connectionKey = null) =>
        Transport.Call($"content{reference.Id}Mount", new(StringValue: token,
            ClientKey: clientKey, ConnectionKey: connectionKey));

    /// <summary>Acknowledges that a frontend View was unmounted.</summary>
    public string Unmount(PageReference reference, string token,
        string? clientKey = null, string? connectionKey = null) =>
        Transport.Call($"content{reference.Id}Unmount", new(StringValue: token,
            ClientKey: clientKey, ConnectionKey: connectionKey));

    /// <summary>Stops admission and waits for accepted Window operations.</summary>
    public ValueTask<WindowContentSessionCloseResult> BeginCloseAsync(TimeSpan timeout) =>
        Content.BeginCloseAsync(timeout);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _attachment.Dispose(); }
        finally
        {
            try { Content.Dispose(); }
            finally { Transport.Dispose(); }
        }
    }
}

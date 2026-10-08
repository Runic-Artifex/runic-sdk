using Microsoft.Extensions.Time.Testing;
using System.Globalization;
using System.Text.Json;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>
/// Owns one headless Window content session and its generated root attachment.
/// The caller retains ownership of the ViewModel and any dependency injection scope.
/// </summary>
public sealed class RunicWindowTestHost<TViewModel> : IDisposable where TViewModel : class
{
    private readonly IDisposable _attachment;
    private bool _disposed;

    /// <summary>Creates a content session for <paramref name="viewModel"/> and attaches its generated root bridge.</summary>
    /// <param name="viewModel">The root ViewModel; the caller keeps ownership.</param>
    /// <param name="rootRoute">The generated root route name.</param>
    /// <param name="attach">The generated bridge factory.</param>
    /// <param name="viewLocator">Resolves .NET Views for presented content.</param>
    /// <param name="operationShutdown">Cancels window-owned operations when signalled.</param>
    /// <param name="modelContext">The model context the window graph must share, if any.</param>
    /// <remarks>
    /// This host uses the system clock and random content ids. The constructor that takes
    /// <see cref="RunicWindowTestHostOptions"/> uses a fake clock and sequential ids.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
        Justification = "Published constructor that mirrors WindowContentSession; reordering would break callers.")]
    public RunicWindowTestHost(TViewModel viewModel, string rootRoute,
        Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable> attach,
        IRunicViewLocator? viewLocator = null,
        CancellationToken operationShutdown = default,
        IRunicModelContext? modelContext = null)
        : this(viewModel, attach, rootRoute, new WindowContentSessionOptions
        {
            ViewLocator = viewLocator, OperationShutdown = operationShutdown, ModelContext = modelContext, RootModel = viewModel,
        }, TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>
    /// Creates a content session for <paramref name="viewModel"/> and attaches its generated root
    /// bridge. By default the window uses a <see cref="FakeTimeProvider"/> and sequential ids.
    /// </summary>
    /// <param name="viewModel">The root ViewModel; the caller keeps ownership.</param>
    /// <param name="attach">
    /// The generated bridge factory, such as the
    /// <c>Func&lt;IBridgeTransport, WindowContentSession, TViewModel, IDisposable&gt;</c> that
    /// the generated <c>AddRunicBridges()</c> registers.
    /// </param>
    /// <param name="options">The root route, View locator, clock and id source.</param>
    public RunicWindowTestHost(TViewModel viewModel,
        Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable> attach,
        RunicWindowTestHostOptions? options)
        : this(viewModel, attach, options?.RootRoute ?? RunicMembers.RouteOf(typeof(TViewModel)), Create(viewModel, options),
            options?.PublicationTimeout ?? TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>
    /// Creates a content session for <paramref name="viewModel"/> with a <see cref="FakeTimeProvider"/>
    /// and sequential ids, and attaches its generated root bridge.
    /// </summary>
    /// <param name="viewModel">The root ViewModel; the caller keeps ownership.</param>
    /// <param name="attach">The generated bridge factory.</param>
    public RunicWindowTestHost(TViewModel viewModel,
        Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable> attach)
        : this(viewModel, attach, options: null)
    {
    }

    private RunicWindowTestHost(TViewModel viewModel,
        Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable> attach, string rootRoute,
        WindowContentSessionOptions sessionOptions, TimeSpan publicationTimeout)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRoute);
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(publicationTimeout, TimeSpan.Zero);
        RootRoute = rootRoute;
        Time = sessionOptions.TimeProvider ?? TimeProvider.System;
        Transport = new InMemoryViewTransport();
        try
        {
            Content = new WindowContentSession(Transport, sessionOptions);
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
        Publications = new RunicPublicationLog(Transport, publicationTimeout);
        Root = new RunicViewDriver<TViewModel>(this.Publications, Transport, rootRoute, null, NextId);
    }

    /// <summary>The root ViewModel.</summary>
    public TViewModel ViewModel { get; }
    /// <summary>The generated root route name.</summary>
    public string RootRoute { get; }
    /// <summary>The in-memory transport that records routes and publications.</summary>
    public InMemoryViewTransport Transport { get; }
    /// <summary>The window content session.</summary>
    public WindowContentSession Content { get; }

    /// <summary>
    /// The window's clock. With the options constructor it is a <see cref="FakeTimeProvider"/>
    /// unless the options supply another clock.
    /// </summary>
    public TimeProvider Time { get; }

    /// <summary>Drives the root ViewModel's generated routes.</summary>
    public RunicViewDriver<TViewModel> Root { get; }

    internal RunicPublicationLog Publications { get; }

    private long _nextId;

    // Mount tokens and operation request ids the drivers issue.
    private string NextId() => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);

    /// <summary>Drives the routes of presented content, such as a reference from a snapshot.</summary>
    public RunicViewDriver<TContent> View<TContent>(PageReference reference) where TContent : class
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new RunicViewDriver<TContent>(Publications, Transport, $"content{reference.Id}", reference, NextId);
    }

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
    public ValueTask<BridgeWindowCloseResult> BeginCloseAsync(TimeSpan timeout) =>
        Content.BeginCloseAsync(timeout);

    /// <summary>Detaches the root bridge and disposes the content session and transport.</summary>
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

    private static WindowContentSessionOptions Create(TViewModel viewModel, RunicWindowTestHostOptions? options)
    {
        var next = 0L;
        return new()
        {
            ViewLocator = options?.ViewLocator,
            OperationShutdown = options?.OperationShutdown ?? default,
            RootModel = viewModel,
            ModelContext = options?.ModelContext,
            LoggerFactory = options?.LoggerFactory,
            TimeProvider = options?.TimeProvider ?? new FakeTimeProvider(),
            CreateId = options?.CreateId ?? (() => Interlocked.Increment(ref next).ToString(CultureInfo.InvariantCulture)),
        };
    }
}

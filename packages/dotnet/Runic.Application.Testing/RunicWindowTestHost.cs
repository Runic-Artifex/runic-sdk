using Microsoft.Extensions.Time.Testing;
using System.Globalization;
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

    /// <summary>
    /// Creates a content session for <paramref name="viewModel"/> and attaches its generated root
    /// bridge. Unless <paramref name="options"/> say otherwise, the window uses a <see cref="FakeTimeProvider"/> and
    /// sequential ids.
    /// </summary>
    /// <param name="viewModel">The root ViewModel; the caller keeps ownership.</param>
    /// <param name="attach">
    /// The generated bridge factory, such as the
    /// <c>Func&lt;IBridgeTransport, WindowContentSession, TViewModel, IDisposable&gt;</c> that
    /// the generated <c>AddRunicViews()</c> registers.
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
        Transport = new InMemoryBridgeTransport();
        try
        {
            Content = new WindowContentSession(Transport, sessionOptions);
            // blobs/1: the window serves blobs and uploads at the test origin; ReadBlobAsync serves what a page loads.
            Blobs.Attach(BridgeBlobEndpoint.Below(static () => RunicBlobs.TestOrigin));
            IDisposable? attachment = null;
            try
            {
                _attachment = attach(Transport, Content, viewModel)
                    ?? throw new InvalidOperationException("The generated Bridge factory returned no attachment.");
                attachment = _attachment;
                if (!Transport.Serves(rootRoute))
                    throw new InvalidOperationException($"The root route '{rootRoute}' was not attached.");
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
        Root = new RunicViewDriver<TViewModel>(Publications, Transport, rootRoute, null, NextId);
        _connections.Bind(Content);
    }

    private readonly RunicConnections _connections = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// The window's page connections, as the window scope's <see cref="IRunicConnections"/> reports them: each test
    /// connection that sent its <c>hello</c>, such as the one of <see cref="Root"/> and those of
    /// <see cref="RunicViewDriver{TModel}.ForConnection"/>, until <see cref="InMemoryBridgeTransport.CloseConnection"/>.
    /// </summary>
    /// <remarks>
    /// Changes arrive in a turn of the window's model context; await a command's reply or the model context before
    /// reading <see cref="IRunicConnections.Current"/>.
    /// </remarks>
    public IRunicConnections Connections => _connections;

    /// <summary>The root ViewModel.</summary>
    public TViewModel ViewModel { get; }
    /// <summary>The generated root route name.</summary>
    public string RootRoute { get; }
    /// <summary>The in-memory transport that records routes and publications.</summary>
    public InMemoryBridgeTransport Transport { get; }
    /// <summary>The window content session.</summary>
    public WindowContentSession Content { get; }

    /// <summary>
    /// The services <see cref="RunicWindowTestHost.Create{TViewModel}(IServiceProvider, RunicWindowTestHostOptions?)"/>
    /// resolved the window from, such as the window scope's provider, or <see langword="null"/> for a host created with a
    /// constructor. The caller keeps ownership of them.
    /// </summary>
    public IServiceProvider? Services { get; internal init; }

    /// <summary>
    /// The application's locale, which the window follows while it sets no locale of its own
    /// (<see cref="IRunicWindowLocale"/>): the <see cref="IRunicApplicationLocale"/> registered in <see cref="Services"/>,
    /// or, for a host without one, a locale of this host alone. Switch it as the application does, and read the pushes
    /// from <see cref="Transport"/>:
    /// <code>
    /// await host.ApplicationLocale.SetLocaleAsync("de");
    /// </code>
    /// </summary>
    public IRunicApplicationLocale ApplicationLocale
    {
        get
        {
            if (Volatile.Read(ref _applicationLocale) is { } known) return known;
            if (Services?.GetService(typeof(IRunicApplicationLocale)) is IRunicApplicationLocale registered)
                return Interlocked.CompareExchange(ref _applicationLocale, registered, null) ?? registered;
            lock (_gate)
            {
                if (_applicationLocale is { } created) return created;
                var own = new RunicApplicationLocale();
                // The window follows this host's locale as a Window scope's IRunicWindowLocale follows the application's.
                _ownLocale = new RunicWindowContext(own);
                _ownLocale.AttachContent(Content);
                Volatile.Write(ref _applicationLocale, own);
                return own;
            }
        }
    }

    private IRunicApplicationLocale? _applicationLocale;
    private RunicWindowContext? _ownLocale;

    /// <summary>
    /// The window's recording presentation: the <see cref="RunicTestWindowPresentation"/> of a Window that
    /// <see cref="RunicWindowTestHost.CreateWindowAsync{TWindow}"/> opened, else the one that
    /// <see cref="RunicTestWindowPresentationServiceCollectionExtensions.AddRunicTestWindowPresentation"/> registered in
    /// <see cref="Services"/>, or <see langword="null"/> without one.
    /// </summary>
    public RunicTestWindowPresentation? Presentation => WindowPresentation ??
        Services?.GetService(typeof(RunicTestWindowPresentation)) as RunicTestWindowPresentation;

    // The presentation of a Window that RunicWindowTestHost.CreateWindowAsync opened.
    internal RunicTestWindowPresentation? WindowPresentation { get; init; }

    /// <summary>
    /// Changes the activity of <see cref="Presentation"/>, as a real window reports that it gained or lost the focus,
    /// was minimized or hidden, and waits until the ViewModel received the change in a turn of the window's model
    /// context.
    /// </summary>
    /// <param name="activity">The new activity, such as <c>new RunicWindowActivity(IsActive: false, IsMinimized: false, IsVisible: true)</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the turn.</param>
    /// <exception cref="InvalidOperationException">The services resolve no <see cref="RunicTestWindowPresentation"/>.</exception>
    public async Task SetWindowActivityAsync(RunicWindowActivity activity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var presentation = Presentation ?? throw new InvalidOperationException(
            "The window's services resolve no RunicTestWindowPresentation. Call services.AddRunicTestWindowPresentation() " +
            "and create the host with RunicWindowTestHost.Create(scope.ServiceProvider).");
        presentation.SetActivity(activity);
        await FlushWindowPresentationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the changes that <see cref="Presentation"/> reported so far, such as those of a command's
    /// <c>SetVisibleAsync</c>, reached the ViewModel: the window's presentation applies them in later turns of its
    /// model context.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the turn.</param>
    /// <remarks>Call it outside a model turn; without a model context it returns at once.</remarks>
    public async Task FlushWindowPresentationAsync(CancellationToken cancellationToken = default)
    {
        // Turns run in order, so a turn queued now runs after the changes queued before it.
        if (Content.ModelContext is { } context)
            await context.InvokeAsync(static () => { }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The window's platform fakes: the scope's <see cref="RunicTestPlatform"/> when <see cref="Services"/> registers
    /// one with <c>AddRunicTestPlatform()</c>, or <see langword="null"/>.
    /// </summary>
    /// <remarks>Script dialogs, message boxes, context menus and the clipboard through it, and read what the ViewModel did
    /// with them. Disposing the host closes it, as closing a window closes its platform services.</remarks>
    public RunicTestPlatform? Platform { get; internal init; }

    /// <summary>
    /// The window's clock: a <see cref="FakeTimeProvider"/> unless the options supply another clock.
    /// </summary>
    public TimeProvider Time { get; }

    /// <summary>Drives the root ViewModel's generated routes.</summary>
    public RunicViewDriver<TViewModel> Root { get; }

    internal RunicPublicationLog Publications { get; }

    private long _nextId;

    // Mount tokens and operation request ids the drivers issue.
    private string NextId() => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);

    private BridgeBlobs Blobs => BridgeDispatcher.For(Transport).Blobs;

    /// <summary>
    /// Simulates a page's upload (blobs/1): stores <paramref name="content"/> as the window's host stores a page's
    /// upload, with the window's upload limits, and returns the upload, which a command input takes, as
    /// <c>Root.ExecuteAsync(vm => vm.AttachCommand, upload)</c>.
    /// </summary>
    /// <param name="content">The uploaded bytes.</param>
    /// <param name="fileName">The file name the page reports.</param>
    /// <param name="contentType">The media type the page reports.</param>
    /// <param name="connectionKey">
    /// The connection that uploads, by its transport key, as <see cref="RunicViewDriver{TModel}.ConnectionKey"/>; a command
    /// decodes the upload only on that connection. As a page has its connection's upload token only after its
    /// <c>hello</c>, the connection sends its <c>hello</c> first if it has not yet.
    /// </param>
    /// <param name="cancellationToken">Cancels storing.</param>
    /// <exception cref="InvalidOperationException">The window refused the upload, for example over its limit.</exception>
    public async Task<RunicUpload> UploadAsync(ReadOnlyMemory<byte> content, string fileName = "upload",
        string contentType = "application/octet-stream", string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentException.ThrowIfNullOrEmpty(connectionKey);
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        // The window keys uploads by its own key of the connection ("{transport key}#n" on this transport), as it keys the
        // upload token that the connection's hello grants.
        await Transport.EnsureHelloAsync(connectionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        var connection = BridgeDispatcher.For(Transport).ConnectionKeyOf(connectionKey)
            ?? throw new InvalidOperationException($"The connection {connectionKey} closed before its upload.");
        return await Blobs.StoreAsync(stream, fileName, contentType, connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a blob URL from the window's state or a command result (blobs/1) as the host serves it to the page, and
    /// returns the response: its status, headers and content. A released or unknown URL answers 404.
    /// </summary>
    /// <param name="url">The blob's <c>url</c>, such as <c>state.Value("avatar").GetProperty("url").GetString()</c>.</param>
    /// <param name="range">A <c>Range</c> header to send, such as <c>bytes=0-99</c>, or <see langword="null"/>.</param>
    /// <param name="head">Sends <c>HEAD</c> instead of <c>GET</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<RunicServedBlob> ReadBlobAsync(string url, string? range = null, bool head = false,
        CancellationToken cancellationToken = default) =>
        RunicBlobs.ServeAsync(Blobs, url, range, head, cancellationToken);

    /// <summary>The number of blobs whose URLs the window serves now, as a test of their release.</summary>
    public int ServedBlobCount => Blobs.BlobCount;

    /// <summary>The number of uploads that the window holds and no command claimed yet.</summary>
    public int PendingUploadCount => Blobs.UploadCount;

    /// <summary>Drives the routes of presented content, such as a reference from a snapshot.</summary>
    public RunicViewDriver<TContent> View<TContent>(PageReference reference) where TContent : class
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new RunicViewDriver<TContent>(Publications, Transport, $"content{reference.Id}", reference, NextId);
    }

    /// <summary>
    /// Goes back as the window's Back does, the mouse's Back button and Alt+Left in a page that installed it
    /// (<c>navigation-path/1</c>): <see cref="RunicNavigator.BackAsync"/> in the innermost presented region that can.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request, as a closed page connection does.</param>
    public Task<RunicNavigationReply> NavigateBackAsync(CancellationToken cancellationToken = default) =>
        NavigationAsync(writer => writer.WriteString("action", "back"), cancellationToken);

    /// <summary>
    /// Opens a route path as a page's browser does when the user goes Back or Forward (<c>popstate</c>) or edits the address
    /// bar (<c>navigation-path/1</c>): in the window's path region (<see cref="RunicPresentation.SyncNavigationPath"/>),
    /// with <see cref="NavigationRouteOpen.BackToOrPush"/>. <see cref="RunicNavigationReply.Path"/> is the path the page's
    /// URL shows afterwards, the previous one when a guard refused.
    /// </summary>
    /// <param name="path">The route path, such as <c>customers/42</c>.</param>
    /// <param name="cancellationToken">Cancels the request, as a closed page connection does.</param>
    public Task<RunicNavigationReply> OpenNavigationPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NavigationAsync(writer =>
        {
            writer.WriteString("action", "open");
            writer.WriteString("path", path);
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the <c>navigation-path</c> pushes the default connection received (<c>navigation-path/1</c>), in order: the
    /// window's route path after the connection's hello and after each change of its path region, and whether the page
    /// pushes, goes back in or replaces its browser history.
    /// </summary>
    public IReadOnlyList<RunicNavigationPathPush> NavigationPathPushes =>
    [
        .. Transport.Messages
            .Where(static message => message.ConnectionKey == InMemoryBridgeTransport.DefaultConnectionKey && message.Kind == "navigation-path")
            .Select(static message =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(message.Json);
                var root = document.RootElement;
                return new RunicNavigationPathPush(
                    root.GetProperty("path").ValueKind == System.Text.Json.JsonValueKind.String ? root.GetProperty("path").GetString() : null,
                    root.GetProperty("operation").GetString()!);
            }),
    ];

    private async Task<RunicNavigationReply> NavigationAsync(Action<System.Text.Json.Utf8JsonWriter> members, CancellationToken cancellationToken)
    {
        await Transport.EnsureHelloAsync(InMemoryBridgeTransport.DefaultConnectionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        var json = RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "navigation");
            members(writer);
            writer.WriteEndObject();
        });
        return new RunicNavigationReply(await Transport.RequestAsync(json, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Stops operation admission and waits up to <paramref name="timeout"/> for accepted Window operations, as a
    /// closing window does (<see cref="WindowContentSession.DrainAsync"/>). The timeout runs on <see cref="Time"/>, so
    /// with a fake clock it passes only when the test advances it.
    /// </summary>
    /// <param name="timeout">How long accepted operations may run, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Stops the wait only; admission stays closed.</param>
    public async Task<RunicWindowCloseResult> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        await Content.DrainAsync(timeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Detaches the root bridge and disposes the content session and transport.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connections.Dispose();
        lock (_gate) _ownLocale?.Dispose();
        try { _attachment.Dispose(); }
        finally
        {
            try { Content.Dispose(); }
            finally
            {
                try { Transport.Dispose(); }
                finally { Platform?.Close(); }
            }
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
            IncludeFailureDetail = options?.IncludeFailureDetail,
            MaximumInputBytes = options?.MaximumInputBytes,
            MaximumUploadBytes = options?.MaximumUploadBytes,
        };
    }
}

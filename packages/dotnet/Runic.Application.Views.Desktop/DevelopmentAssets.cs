using Microsoft.Extensions.Logging;
using Runic.Assets;
using Runic.Assets.Desktop;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Serves a frontend output directory live in a development run, the internal contract between
/// <c>dotnet runic dev</c> and <see cref="RunicDesktopHost"/>. Without a Vite or Angular development server,
/// <c>dev</c> sets <see cref="EnvironmentVariable"/> to the frontend output directory, such as <c>Frontend/dist</c>.
/// Each window then serves that directory before its <c>www/</c> folder, and reloads when the directory changes.
/// </summary>
/// <remarks>
/// A window reloads the last HTML document it loaded from the surface, or else its start page. Only the path is kept:
/// its query, its hash and routes set with <c>history.pushState</c> are not, because the surface never sees them.
/// </remarks>
internal sealed class DevelopmentAssets
{
    internal const string EnvironmentVariable = "RUNIC_APPLICATION_DEVELOPMENT_ASSETS";

    private readonly object _gate = new();
    private readonly Dictionary<(string Directory, string StartPage), Shared> _shared = [];

    /// <summary>
    /// Gets the directory to serve live: the variable names an existing directory, the environment is
    /// <c>Development</c> and the application chose no surface options of its own.
    /// </summary>
    internal static string? ResolveDirectory(Func<string, string?> read, bool surfaceOptionsSet)
    {
        if (surfaceOptionsSet || !IsDevelopment(read)) return null;
        var directory = read(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        directory = Path.GetFullPath(directory);
        return Directory.Exists(directory) ? directory : null;
    }

    // The rule of BridgeFailureDetails.IsDevelopment in Runic.Application.Views.
    private static bool IsDevelopment(Func<string, string?> read)
    {
        var name = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(name)) name = read("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the content of one window: the live directory, then <paramref name="webRoot"/> for the files the build
    /// adds, such as <c>runic-views.js</c>. Returns <see langword="null"/>, after logging why, when the directory
    /// cannot be served; the window then serves <paramref name="webRoot"/> as usual.
    /// </summary>
    internal Window? Open(string directory, string startPage, string webRoot, ILogger logger)
    {
        Shared shared;
        lock (_gate)
        {
            var key = (directory, startPage);
            if (!_shared.TryGetValue(key, out shared!))
            {
                try { shared = Shared.Create(this, key, webRoot, logger); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                    or InvalidDataException or PlatformNotSupportedException)
                {
                    DesktopLog.DevelopmentAssetsUnavailable(logger, error, directory, error.GetType().Name, webRoot);
                    return null;
                }
                _shared.Add(key, shared);
                DesktopLog.DevelopmentAssetsServed(logger, directory);
            }
            shared.References++;
        }
        return new Window(shared, startPage, logger);
    }

    private void Release(Shared shared)
    {
        lock (_gate)
        {
            if (--shared.References != 0) return;
            _shared.Remove(shared.Key);
        }
        shared.Dispose();
    }

    // One watched source per directory and start page, shared by the windows that serve it.
    internal sealed class Shared : IDisposable
    {
        private readonly DevelopmentAssets _owner;
        private readonly object _windowsGate = new();
        private readonly List<Window> _windows = [];

        private Shared(DevelopmentAssets owner, (string, string) key, DevelopmentDirectoryAssetSource source,
            DevelopmentDirectoryAssetSource? webRoot)
        {
            _owner = owner;
            Key = key;
            Source = source;
            WebRoot = webRoot;
            Primary = source.ToDesktopContentHandler();
            Fallback = webRoot?.ToDesktopContentHandler();
        }

        internal (string Directory, string StartPage) Key { get; }
        internal int References { get; set; }
        internal DevelopmentDirectoryAssetSource Source { get; }
        internal DevelopmentDirectoryAssetSource? WebRoot { get; }
        internal ContentHandler Primary { get; }
        internal ContentHandler? Fallback { get; }

        internal static Shared Create(DevelopmentAssets owner, (string Directory, string StartPage) key, string webRoot,
            ILogger logger)
        {
            var source = new DevelopmentDirectoryAssetSource(key.Directory, key.StartPage);
            DevelopmentDirectoryAssetSource? fallback = null;
            try
            {
                // The build's www/ copy is fixed while the application runs; it supplies what the frontend build does
                // not write, such as runic-views.js and the frontends of referenced projects.
                try { fallback = new DevelopmentDirectoryAssetSource(webRoot, key.StartPage); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                    or InvalidDataException)
                {
                    DesktopLog.DevelopmentAssetsUnavailable(logger, error, webRoot, error.GetType().Name, key.Directory);
                }
                var shared = new Shared(owner, key, source, fallback);
                source.Changed += shared.OnChanged;
                source.StartWatching();
                return shared;
            }
            catch
            {
                fallback?.Dispose();
                source.Dispose();
                throw;
            }
        }

        internal void Add(Window window)
        {
            lock (_windowsGate) _windows.Add(window);
        }

        internal void Remove(Window window)
        {
            lock (_windowsGate) _windows.Remove(window);
            _owner.Release(this);
        }

        private void OnChanged(object? sender, AssetSourceChangedEventArgs change)
        {
            Window[] windows;
            lock (_windowsGate) windows = [.. _windows];
            foreach (var window in windows) window.Reload();
        }

        public void Dispose()
        {
            Source.Dispose();
            WebRoot?.Dispose();
        }
    }

    /// <summary>The live content of one window; dispose it when the window closes.</summary>
    internal sealed class Window : IDisposable
    {
        private readonly Shared _shared;
        private readonly string _startPage;
        private readonly ILogger _logger;
        private DesktopSurface? _surface;
        private string? _document;
        private int _disposed;

        internal Window(Shared shared, string startPage, ILogger logger)
        {
            _shared = shared;
            _startPage = startPage;
            _logger = logger;
            Content = new DesktopContent.Handler(ResolveAsync);
        }

        /// <summary>The window's surface content.</summary>
        internal DesktopContent Content { get; }

        /// <summary>The path of the last HTML document served, such as <c>/settings.html</c>.</summary>
        internal string? Document => Volatile.Read(ref _document);

        /// <summary>Starts reloading <paramref name="surface"/> when the directory changes.</summary>
        internal void Attach(DesktopSurface surface)
        {
            Volatile.Write(ref _surface, surface);
            _shared.Add(this);
        }

        private async ValueTask<ContentResponse?> ResolveAsync(ContentRequest request, CancellationToken cancellationToken)
        {
            var response = await _shared.Primary(request, cancellationToken).ConfigureAwait(false);
            if (response is { StatusCode: 404 } && _shared.Fallback is { } fallback &&
                await fallback(request, cancellationToken).ConfigureAwait(false) is { StatusCode: not 404 } served)
                response = served;
            if (response is { StatusCode: 200 } &&
                StringComparer.Ordinal.Equals(request.Method, "GET") &&
                response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                Volatile.Write(ref _document, request.Path);
            return response;
        }

        // Navigates the window's pages; their Bridge reconnects as after any reload. Navigation through the surface
        // runs no page script, so an application's Content-Security-Policy does not block it.
        internal void Reload()
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _surface) is not { } surface) return;
            _ = ReloadAsync(surface, ReloadUrl(surface.Url, Document, _startPage));
        }

        private async Task ReloadAsync(DesktopSurface surface, string url)
        {
            try { await surface.NavigateAsync(url).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A window that closed meanwhile; the next change or the next window reloads.
                DesktopLog.DevelopmentReloadFailed(_logger, error);
            }
        }

        internal static string ReloadUrl(Uri surface, string? document, string startPage)
        {
            var path = document ?? startPage;
            var relative = string.Join('/', path.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
            return new Uri(surface, relative).AbsoluteUri;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shared.Remove(this);
        }
    }
}

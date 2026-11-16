using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// sdk#1043: DesktopWindow.OpenDevToolsAsync and DesktopHostOptions.DevToolsExtensions. Extensions are a Development-only
// option of the WebView2 environment; the Windows native smoke (--devtools) installs one and opens the DevTools window.
public sealed class DevToolsTests
{
    private static readonly string First = Path.Combine(Path.GetTempPath(), "runic-devtools", "react");
    private static readonly string Second = Path.Combine(Path.GetTempPath(), "runic-devtools", "runic-panel");

    private static Func<string, string?> Environment(string? dotnet, string? aspnet = null, string? folders = null) => name => name switch
    {
        "DOTNET_ENVIRONMENT" => dotnet,
        "ASPNETCORE_ENVIRONMENT" => aspnet,
        DevToolsExtensionFolders.Variable => folders,
        _ => null,
    };

    [Test]
    public void ExtensionsLoadOnlyInDevelopment()
    {
        var both = First + Path.PathSeparator + Second;
        Assert.Empty(DevToolsExtensionFolders.Resolve([First], Environment(null, folders: both)));
        Assert.Empty(DevToolsExtensionFolders.Resolve([First], Environment("Production", "Development", both)));
        Assert.Empty(DevToolsExtensionFolders.Resolve([First], Environment("Staging", folders: both)));
        Assert.Equal([First], DevToolsExtensionFolders.Resolve([First], Environment("Development")));
        Assert.Equal([First], DevToolsExtensionFolders.Resolve([First], Environment(null, "development")));
        Assert.Empty(DevToolsExtensionFolders.Resolve([], Environment("Development")));
    }

    [Test]
    public void TheApplicationsFoldersComeFirstThenTheSessionsEachOnce()
    {
        var variable = string.Join(Path.PathSeparator, ["", "  ", Second, "relative/extension", First + Path.DirectorySeparatorChar, Second]);
        Assert.Equal([First, Second], DevToolsExtensionFolders.Resolve([First], Environment("Development", folders: variable)));
        Assert.Equal([Second, First], DevToolsExtensionFolders.Resolve([], Environment("Development", folders: variable)));
        // The full form: a "." segment and a trailing separator name the same folder.
        var dotted = Path.Combine(Path.GetTempPath(), "runic-devtools", ".", "react") + Path.DirectorySeparatorChar;
        Assert.Equal([First], DevToolsExtensionFolders.Resolve([dotted, First], Environment("Development")));
    }

    [Test]
    public async Task TheHostRejectsFoldersThatAreNotFullPaths()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(new() { DevToolsExtensions = ["react-devtools"] }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(new() { DevToolsExtensions = [" "] }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(new() { DevToolsExtensions = [null!] }));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await DesktopHost.StartAsync(new() { DevToolsExtensions = null! }));
        await using var host = await DesktopHost.StartAsync(new() { DevToolsExtensions = [First], WaitForConnection = false });
        Assert.Equal([First], host.DevToolsExtensions);
        Assert.Empty(new DesktopHostOptions().DevToolsExtensions);
    }

    // A window host of the application's own cannot open WebView2's DevTools window: the call returns false, also with
    // the developer tools on, and the host receives no extensions.
    [Test]
    public async Task AWindowWithoutWebView2OpensNoDevToolsWindow()
    {
        var factory = new DevToolsHostFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false, DevToolsExtensions = [First] });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded,
            WebContent = DesktopWebContentPolicy.Default with { DevTools = true },
        });
        Assert.True(factory.Host!.Options!.WebContent.DevTools);
        Assert.False(await window.OpenDevToolsAsync());
        await window.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.OpenDevToolsAsync());
    }

    [Test]
    public async Task TheEmbeddedHostDefaultOpensNothing()
    {
        IWebUiEmbeddedHost host = new EmbeddedHostTests.RecordingHost();
        Assert.False(await host.OpenDevToolsAsync());
    }

    private sealed class DevToolsHostFactory : IDesktopWindowHostFactory
    {
        internal DevToolsHost? Host { get; private set; }
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => Host = new DevToolsHost();
    }

    private sealed class DevToolsHost : IDesktopWindowHost
    {
        public event EventHandler? Closed;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { Options = options; IsOpen = true; return ValueTask.CompletedTask; }
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (IsOpen) { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); }
            return ValueTask.CompletedTask;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}

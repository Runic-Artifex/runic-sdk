using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// dotnet runic dev without a development server sets RUNIC_APPLICATION_DEVELOPMENT_ASSETS to the frontend output
// directory: a Development host serves it before www/ and reloads its windows when it changes (DevelopmentAssets).
internal sealed class DevelopmentAssetsChecks
{
    private const byte Navigation = 0xFB;

    [Test, NotInParallel(Order = 35)]
    public void TheVariableIsReadOnlyInDevelopmentWithoutApplicationSurfaceOptions()
    {
        string existing = Directory.CreateTempSubdirectory("runic-dev-assets-").FullName;
        try
        {
            Func<string, string?> development = name => name switch
            {
                "DOTNET_ENVIRONMENT" => "Development",
                DevelopmentAssets.EnvironmentVariable => existing,
                _ => null,
            };
            Check(DevelopmentAssets.ResolveDirectory(development, surfaceOptionsSet: false) == existing,
                "A Development host did not serve the directory.");
            // ASPNETCORE_ENVIRONMENT counts when DOTNET_ENVIRONMENT is unset, as for Bridge failure detail.
            Check(DevelopmentAssets.ResolveDirectory(name => name switch
            {
                "ASPNETCORE_ENVIRONMENT" => "development",
                DevelopmentAssets.EnvironmentVariable => existing,
                _ => null,
            }, surfaceOptionsSet: false) == existing, "ASPNETCORE_ENVIRONMENT=Development was ignored.");
            // The application's own surface content wins.
            Check(DevelopmentAssets.ResolveDirectory(development, surfaceOptionsSet: true) is null,
                "The directory replaced the application's SurfaceOptions.");
            foreach (string? environment in new[] { null, "Production", "Staging" })
            {
                Check(DevelopmentAssets.ResolveDirectory(name => name switch
                {
                    "DOTNET_ENVIRONMENT" => environment,
                    DevelopmentAssets.EnvironmentVariable => existing,
                    _ => null,
                }, surfaceOptionsSet: false) is null, $"A {environment ?? "default"} host read the variable.");
            }
            foreach (string value in new[] { "", "relative/dist", Path.Combine(existing, "missing") })
            {
                Check(DevelopmentAssets.ResolveDirectory(name => name switch
                {
                    "DOTNET_ENVIRONMENT" => "Development",
                    DevelopmentAssets.EnvironmentVariable => value,
                    _ => null,
                }, surfaceOptionsSet: false) is null, $"The value '{value}' was served.");
            }
        }
        finally
        {
            Directory.Delete(existing, recursive: true);
        }
    }

    [Test, NotInParallel(Order = 35)]
    public void AReloadKeepsTheLastDocumentPath()
    {
        var surface = new Uri("http://127.0.0.1:41000/");
        Check(DevelopmentAssets.Window.ReloadUrl(surface, null, "index.html") == "http://127.0.0.1:41000/index.html",
            "Without a document, the reload did not open the start page.");
        Check(DevelopmentAssets.Window.ReloadUrl(surface, "/", "settings.html") == "http://127.0.0.1:41000/",
            "The surface root was not reloaded as the root.");
        Check(DevelopmentAssets.Window.ReloadUrl(surface, "/pages/a b#?.html", "index.html")
            == "http://127.0.0.1:41000/pages/a%20b%23%3F.html", "A decoded document path was not escaped.");
    }

    [Test, NotInParallel(Order = 35)]
    public async Task ADevelopmentHostServesTheDirectoryLiveAndReloadsItsWindow()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Directory.CreateTempSubdirectory("runic-dev-assets-").FullName;
        try
        {
            string dist = Path.Combine(root, "dist");
            string www = Path.Combine(root, "www");
            Write(dist, "index.html", "<p>one</p>");
            Write(dist, "settings.html", "<p>settings one</p>");
            Write(dist, "assets/app.js", "export {};");
            // The build's copy: an older frontend and the host's Views client.
            Write(www, "index.html", "<p>stale</p>");
            Write(www, "runic-views.js", "// host client");

            await using var provider = Services();
            var factory = new NativeDispatchFactory();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = factory,
                WaitForConnection = false,
            });
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            })
            {
                ReadEnvironment = name => name switch
                {
                    "DOTNET_ENVIRONMENT" => "Development",
                    DevelopmentAssets.EnvironmentVariable => dist,
                    _ => null,
                },
            };
            await using var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = www });
            var surface = ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface;
            using var http = new HttpClient();
            Check((await http.GetStringAsync(surface.Url, cancellation.Token)).Contains("one", StringComparison.Ordinal),
                "The window did not serve the frontend output directory.");
            Check(await http.GetStringAsync(new Uri(surface.Url, "runic-views.js"), cancellation.Token) == "// host client",
                "The window did not serve www/ for a file the frontend build does not write.");
            using (var nested = await http.GetAsync(new Uri(surface.Url, "assets/app.js"), cancellation.Token))
                Check(nested.IsSuccessStatusCode && nested.Content.Headers.ContentType?.MediaType == "text/javascript",
                    $"A nested asset was not served as JavaScript: {nested.StatusCode} {nested.Content.Headers.ContentType}.");
            Check((await http.GetStringAsync(new Uri(surface.Url, "settings.html"), cancellation.Token)).Contains("settings one",
                StringComparison.Ordinal), "A second page was not served.");

            string url;
            using (var page = await DesktopWireClient.ConnectAsync(surface, cancellation.Token))
            {
                var navigation = page.WaitForCommandAsync(Navigation, cancellation.Token);
                Write(dist, "settings.html", "<p>settings two</p>");
                url = await navigation;
            }
            // The last document the window loaded, not its start page.
            Check(url == new Uri(surface.Url, "settings.html").AbsoluteUri, $"The window reloaded '{url}'.");
            // The surface admits one page at a time; once the navigated one has left, the reload gets the new content.
            string reloaded = "";
            while (!reloaded.Contains("settings two", StringComparison.Ordinal))
            {
                await Task.Delay(50, cancellation.Token);
                using var response = await http.GetAsync(url, cancellation.Token);
                if (response.IsSuccessStatusCode) reloaded = await response.Content.ReadAsStringAsync(cancellation.Token);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A reload reaches only a connected page. A page that is still loading or reconnecting when a change is published
    // misses it: it is reloaded when it connects, and a page whose document is current is not.
    [Test, NotInParallel(Order = 35)]
    public async Task APageThatMissedAReloadIsReloadedWhenItConnects()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Directory.CreateTempSubdirectory("runic-dev-assets-").FullName;
        try
        {
            string dist = Path.Combine(root, "dist");
            string www = Path.Combine(root, "www");
            Write(dist, "index.html", "<p>one</p>");
            Write(dist, "probe.txt", "one");
            Write(www, "runic-views.js", "// host client");

            await using var provider = Services();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory(),
                WaitForConnection = false,
            });
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            })
            {
                ReadEnvironment = name => name switch
                {
                    "DOTNET_ENVIRONMENT" => "Development",
                    DevelopmentAssets.EnvironmentVariable => dist,
                    _ => null,
                },
            };
            await using var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = www });
            var surface = ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface;
            using var http = new HttpClient();

            // The page loads its document; the change is published before its Bridge connects, so no page gets the reload.
            Check((await http.GetStringAsync(surface.Url, cancellation.Token)).Contains("one", StringComparison.Ordinal),
                "The window did not serve the frontend output directory.");
            Write(dist, "index.html", "<p>two</p>");
            Write(dist, "probe.txt", "two");
            // probe.txt is not a document: polling it does not count as loading the page again. A file read while it is
            // written does not match the manifest and fails until the refresh.
            while (true)
            {
                using var probe = await http.GetAsync(new Uri(surface.Url, "probe.txt"), cancellation.Token);
                if (probe.IsSuccessStatusCode && await probe.Content.ReadAsStringAsync(cancellation.Token) == "two") break;
                await Task.Delay(50, cancellation.Token);
            }

            string url;
            using (var page = await DesktopWireClient.ConnectAsync(surface, cancellation.Token))
                url = await page.WaitForCommandAsync(Navigation, cancellation.Token);
            Check(url == surface.Url.AbsoluteUri, $"The window reloaded '{url}'.");

            // The reloaded page has the current document: it connects without another reload. The surface admits one page
            // at a time, so the document is served once the navigated page has left.
            string reloaded = "";
            while (!reloaded.Contains("two", StringComparison.Ordinal))
            {
                await Task.Delay(50, cancellation.Token);
                using var response = await http.GetAsync(surface.Url, cancellation.Token);
                if (response.IsSuccessStatusCode) reloaded = await response.Content.ReadAsStringAsync(cancellation.Token);
            }
            using var current = await ConnectWhenFreeAsync(surface, cancellation.Token);
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            quiet.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                url = await current.WaitForCommandAsync(Navigation, quiet.Token);
                throw new InvalidOperationException($"A page with the current document was reloaded to '{url}'.");
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The surface admits one page at a time; a page that just left may still hold the connection for a moment.
    private static async Task<DesktopWireClient> ConnectWhenFreeAsync(DesktopSurface surface, CancellationToken cancellationToken)
    {
        while (true)
        {
            try { return await DesktopWireClient.ConnectAsync(surface, cancellationToken); }
            catch (Exception error) when (error is System.Net.WebSockets.WebSocketException or HttpRequestException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    [Test, NotInParallel(Order = 35)]
    public async Task AHostOutsideDevelopmentIgnoresTheVariable()
    {
        string root = Directory.CreateTempSubdirectory("runic-dev-assets-").FullName;
        try
        {
            string dist = Path.Combine(root, "dist");
            string www = Path.Combine(root, "www");
            Write(dist, "index.html", "<p>live</p>");
            Write(www, "index.html", "<p>built</p>");
            await using var provider = Services();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory(),
                WaitForConnection = false,
            });
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            })
            {
                ReadEnvironment = name => name switch
                {
                    "DOTNET_ENVIRONMENT" => "Production",
                    DevelopmentAssets.EnvironmentVariable => dist,
                    _ => null,
                },
            };
            await using var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = www });
            var surface = ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface;
            using var http = new HttpClient();
            string served = await http.GetStringAsync(surface.Url);
            Check(served.Contains("built", StringComparison.Ordinal), $"A Production host served '{served}'.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        return services.BuildServiceProvider();
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

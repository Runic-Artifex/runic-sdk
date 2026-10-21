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

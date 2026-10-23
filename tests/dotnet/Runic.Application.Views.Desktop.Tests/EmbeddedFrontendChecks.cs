using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Assets;
using Runic.Desktop;
using TUnit.Core;

// I-027 (D039.10): RunicDesktopHostOptions.Assets, or the build's embedded frontend, replaces the web root: the root
// serves the Window's start page from the archive. SurfaceOptions and RunicWindowOptions.ContentRoot still win.
internal sealed class EmbeddedFrontendChecks
{
    [Test, NotInParallel(Order = 40)]
    public async Task HostServesItsAssetsInPlaceOfTheWebRoot()
    {
        var directory = Directory.CreateTempSubdirectory("runic-embedded-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "index.html"), "archive index");
            File.WriteAllText(Path.Combine(directory, "settings.html"), "archive settings");
            Directory.CreateDirectory(Path.Combine(directory, "assets"));
            File.WriteAllText(Path.Combine(directory, "assets/app.js"), "archive script");
            var webRoot = Directory.CreateDirectory(Path.Combine(directory, "..", Path.GetFileName(directory) + "-www")).FullName;
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "web root");
            using var archive = new MemoryStream();
            await AssetArchive.WriteTrustedDirectoryAsync(directory, archive);
            archive.Position = 0;
            var assets = await AssetArchive.ReadAsync(archive);

            var services = new ServiceCollection();
            services.AddScoped<FileViewModel>();
            services.AddScoped<Func<IBridgeTransport, FileViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
            services.AddRunicPlatformServices();
            await using var provider = services.BuildServiceProvider();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
                Assets = assets,
            });
            using var client = new HttpClient();

            async Task<string> RootAsync(RunicWindowOptions? options, string path = "")
            {
                var window = await host.OpenWindowAsync<FileWindow>(options);
                try { return await client.GetStringAsync(new Uri(RunicDesktopHost.GetSurface(window)!.Url, path)); }
                finally { await window.DisposeAsync(); }
            }

            Require(await RootAsync(null) == "archive index", "The root did not serve the archive's entry point.");
            Require(await RootAsync(null, "assets/app.js") == "archive script", "A declared asset was not served.");
            Require(await RootAsync(new RunicWindowOptions { StartPage = "settings.html" }) == "archive settings",
                "The root did not serve the Window's start page.");
            Require(await RootAsync(new RunicWindowOptions { ContentRoot = webRoot }) == "web root",
                "A Window's ContentRoot did not win over the host's assets.");
            var missing = false;
            try { await RootAsync(new RunicWindowOptions { StartPage = "missing.html" }); }
            catch (FileNotFoundException) { missing = true; }
            Require(missing, "A start page that the archive does not declare opened.");
            Directory.Delete(webRoot, recursive: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

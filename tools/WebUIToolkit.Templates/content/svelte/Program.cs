using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.CsWebUi.Mvvm;
using WebUIToolkit.Hosting.WebUi;

string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(webRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
var builder = WebUiApp.CreateBuilder(args);
builder.UseSvelte(new CsWebUiAppOptions(
    assets,
    new ApplicationRoot(),
    new CsWebUiAdapterOptions(webRoot),
    new BrowserHostOptions("webuitoolkitstarter-svelte"),
    new BrowserWindowOptions("main", "WebUIToolkitStarter", 960, 720)));
return await builder.RunAsync();

internal sealed class ApplicationRoot : IRootSessionFactory
{
    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IRootSession>(new ApplicationSession());
    }
    private sealed class ApplicationSession : IRootSession
    {
        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

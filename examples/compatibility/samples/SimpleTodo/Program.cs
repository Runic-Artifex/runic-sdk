using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
using WebUIToolkit.Samples.SimpleTodo;

string staticWebRoot = Path.Combine(AppContext.BaseDirectory, "www");
if (!Directory.Exists(staticWebRoot))
{
    throw new DirectoryNotFoundException(
        $"The sample's local assets were not copied to '{staticWebRoot}'.");
}

await using var root = new TodoApplicationRoot();
await root.PrepareAsync(staticWebRoot);
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    return await root.RunSmokeTestAsync();
}

if (args.Contains("--browser-smoke-test", StringComparer.Ordinal))
{
    return await TodoBrowserSmoke.RunAsync(root);
}

FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(root.WebRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(root.WebRoot, manifest);
var builder = WebUiApp.CreateBuilder(args);
builder.UseCwhtmlHtmx(new CsWebUiAppOptions(
    assets,
    root,
    new CsWebUiAdapterOptions(root.WebRoot, configureWindow: root.ConfigureWindow),
    new BrowserHostOptions("simple-todo"),
    new BrowserWindowOptions("main", "Simple Todo", 760, 720)));

return await builder.RunAsync();

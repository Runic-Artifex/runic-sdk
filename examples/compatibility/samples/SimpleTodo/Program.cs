using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
using WebUIToolkit.Samples.SimpleTodo;

string staticWebRoot = Path.Combine(AppContext.BaseDirectory, "www");
if (!Directory.Exists(staticWebRoot))
{
    throw new DirectoryNotFoundException(
        $"The sample's local assets were not copied to '{staticWebRoot}'.");
}

var builder = WebUiApp.CreateBuilder(args);
CwhtmlHtmxAppBuilder frontend = builder.CwhtmlHtmx
    .ConfigureEndpoint(new HtmxEndpointOptions(
        TodoApplicationRoot.AllowedOrigin,
        idleTimeout: TimeSpan.FromMinutes(15),
        maximumBodyBytes: 32 * 1024,
        maximumFields: 8,
        maximumFieldBytes: 1024,
        maximumResponseBytes: 256 * 1024))
    .ConfigureTransport(new CsWebUiHtmxTransportOptions(
        TodoApplicationRoot.AllowedOrigin,
        maximumRequestBytes: 32 * 1024,
        maximumResponseBytes: 256 * 1024,
        maximumFields: 8,
        maximumFieldBytes: 1024));
if (args.Contains("--csharp-markup-smoke-test", StringComparer.Ordinal) ||
    args.Contains("--csharp-markup-browser-smoke-test", StringComparer.Ordinal))
{
    await using TodoCsharpMarkupApplication csharpMarkupRoot =
        await TodoCsharpMarkupApplicationRoot.CreateAsync(staticWebRoot, frontend);
    if (args.Contains("--csharp-markup-browser-smoke-test", StringComparer.Ordinal))
    {
        return await TodoBrowserSmoke.RunAsync(csharpMarkupRoot);
    }
    return await TodoCsharpMarkupSmoke.RunAsync(csharpMarkupRoot);
}

await using TodoApplication root = await TodoApplicationRoot.CreateAsync(staticWebRoot, frontend);
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    return await TodoSmoke.RunAsync(root);
}

if (args.Contains("--browser-smoke-test", StringComparer.Ordinal))
{
    return await TodoBrowserSmoke.RunAsync(root);
}

frontend.UseNativeWindow(
    root,
    new BrowserHostOptions("simple-todo"),
    new BrowserWindowOptions("main", "Simple Todo", 760, 720));

return await builder.RunAsync();

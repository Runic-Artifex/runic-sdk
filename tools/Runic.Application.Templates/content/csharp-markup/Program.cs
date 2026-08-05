using System;
using System.IO;
using System.Threading.Tasks;
using RunicToolkitStarter;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.MVVM;
using RunicMarkup.RunicToolkit.Htmx;
using RunicMarkup.RunicToolkit.Htmx.CsWebUi;

const string origin = "https://runic-toolkitstarter.native";
string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
var builder = WebUiApp.CreateBuilder(args);
CwhtmlHtmxAppBuilder frontend = builder.CwhtmlHtmx
    .ConfigureEndpoint(new HtmxEndpointOptions(origin))
    .ConfigureTransport(new CsWebUiHtmxTransportOptions(origin));

await using var application = await frontend.CreateApplicationAsync(
    CounterGenerated.HtmxView,
    CounterDocumentGenerated.CwhtmlView,
    new MvvmContract("runic-toolkitstarter.counter"),
    origin,
    webRoot,
    static _ => ValueTask.FromResult(new CounterViewModel()),
    static model => CounterGenerated.CreateHtmxAdapter(model, CounterJsonContext.Default),
    CounterRenderModel.Initial,
    CounterRenderModel.Response,
    static (view, developmentAssets) =>
        new CounterDocumentModel(view, developmentAssets));

frontend.UseNativeWindow(
    application,
    new BrowserHostOptions("runic-toolkitstarter-csharp-markup"),
    new BrowserWindowOptions("main", "RunicToolkitStarter", 760, 640));

return await builder.RunAsync();

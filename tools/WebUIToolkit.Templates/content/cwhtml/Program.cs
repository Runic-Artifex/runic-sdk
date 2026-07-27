using System;
using System.IO;
using System.Threading.Tasks;
using WebUIToolkitStarter;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

const string origin = "https://webuitoolkitstarter.native";
string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
var builder = WebUiApp.CreateBuilder(args);
CwhtmlHtmxAppBuilder frontend = builder.CwhtmlHtmx
    .ConfigureEndpoint(new HtmxEndpointOptions(origin))
    .ConfigureTransport(new CsWebUiHtmxTransportOptions(origin));

await using var application = await frontend.CreateApplicationAsync(
    CounterView.HtmxView,
    CounterDocumentView.CwhtmlView,
    new MvvmContract("webuitoolkitstarter.counter"),
    origin,
    webRoot,
    static _ => ValueTask.FromResult(new CounterViewModel()),
    static model => CounterView.CreateHtmxAdapter(model, CounterJsonContext.Default),
    CounterRenderModel.Initial,
    CounterRenderModel.Response,
    static (view, developmentAssets) =>
        new CounterDocumentModel(view, developmentAssets));

frontend.UseNativeWindow(
    application,
    new BrowserHostOptions("webuitoolkitstarter-cwhtml"),
    new BrowserWindowOptions("main", "WebUIToolkitStarter", 760, 640));

return await builder.RunAsync();

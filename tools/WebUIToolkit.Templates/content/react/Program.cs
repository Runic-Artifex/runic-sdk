using System;
using System.IO;
using System.Threading.Tasks;
using WebUIToolkitStarter;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.CsWebUi;
using WebUIToolkit.Hosting.CsWebUi.Mvvm;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;

if (Array.Exists(args, static argument => argument == "--smoke-test"))
    return await CounterSmokeTest.RunAsync();

string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
    .BuildFromDirectory(webRoot, "index.html");
var assets = new DirectoryFrontendAssetProvider(webRoot, manifest);
var builder = WebUiApp.CreateBuilder(args);
var options = new MvvmFrontendApplicationOptions<CounterViewModel>(
    assets,
    new CsWebUiAdapterOptions(webRoot),
    new BrowserHostOptions("webuitoolkitstarter-react"),
    new BrowserWindowOptions("main", "WebUIToolkit Counter · React", 760, 680),
    new MvvmContract(CounterContracts.Counter.Name),
    static _ => ValueTask.FromResult(new CounterViewModel()),
    CounterContracts.Counter.CreateAdapter);
await using MvvmFrontendApplication frontend =
    builder.React.CreateApplication(options);
return await builder.RunAsync();

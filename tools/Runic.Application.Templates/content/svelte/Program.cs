using System;
using System.IO;
using System.Threading.Tasks;
using RunicToolkitStarter;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.Build;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.CsWebUi.Mvvm;
using RunicToolkit.Hosting.WebUi;
using RunicToolkit.MVVM;

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
    new BrowserHostOptions("runic-toolkitstarter-svelte"),
    new BrowserWindowOptions("main", "RunicToolkit Counter · Svelte", 760, 680),
    new MvvmContract(CounterContracts.Counter.Name),
    static _ => ValueTask.FromResult(new CounterViewModel()),
    CounterContracts.Counter.CreateAdapter);
await using MvvmFrontendApplication frontend =
    builder.Svelte.CreateApplication(options);
return await builder.RunAsync();

using CsWebUi;
using FirstWindow;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi.DependencyInjection;

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
using var window = provider.OpenWindow<CounterWindow, CounterViewModel>(host => new CounterWindow(host));
window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
if (args.Contains("--serve-only", StringComparer.Ordinal))
{
    Console.WriteLine(window.StartServer("index.html"));
    Console.Out.Flush();
    await Console.In.ReadLineAsync();
}
else
{
    window.Show("index.html");
    WebUiApplication.Wait();
}
WebUiApplication.Clean();

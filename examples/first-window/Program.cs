using CsWebUi;
using FirstWindow;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi;

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

// The window releases asynchronously, before WebUI cleans up its native state.
await using (var window = provider.OpenWindow<CounterWindow, CounterViewModel>(host => new CounterWindow(host)))
{
    window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
    window.Show("index.html");
    WebUiApplication.Wait();
}
WebUiApplication.Clean();

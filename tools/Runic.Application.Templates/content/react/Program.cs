using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi;
using RunicWindowApp;

var services = new ServiceCollection();
services.AddScoped<WorkspaceViewModel>();
services.AddScoped<WelcomeViewModel>();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

using var window = provider.OpenWindow<WorkspaceWindow, WorkspaceViewModel>(host => new WorkspaceWindow(host));
window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
window.Show("index.html");
WebUiApplication.Wait();
WebUiApplication.Clean();

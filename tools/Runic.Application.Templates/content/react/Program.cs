using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi.DependencyInjection;
using RunicWindowApp;

var services = new ServiceCollection();
services.AddScoped<WorkspaceViewModel>();
services.AddScoped<WelcomeViewModel>();
services.AddScoped<CounterViewModel>();
services.AddTransient<WelcomeView>();
services.AddTransient<CounterView>();
services.AddScoped<IRunicViewLocator, MicrosoftViewLocator>();
services.AddRunicBridges();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    using var scope = provider.CreateScope();
    var workspace = scope.ServiceProvider.GetRequiredService<WorkspaceViewModel>();
    workspace.ShowCounterCommand.Execute(null);
    if (workspace.Main is not CounterViewModel counter)
        throw new InvalidOperationException("The Window did not select its Counter View.");
    counter.IncrementCommand.Execute(null);
    if (counter.Count != 1)
        throw new InvalidOperationException("The generated command did not update the Counter ViewModel.");
    Console.WriteLine("RUNIC_VIEWS_TEMPLATE_OK|window|view|command");
    return;
}

using var window = provider.OpenWindow<WorkspaceWindow, WorkspaceViewModel>(host => new WorkspaceWindow(host));
window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
window.Show("index.html");
WebUiApplication.Wait();
WebUiApplication.Clean();

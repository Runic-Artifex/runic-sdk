using DocumentTabs;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

using var provider = new ServiceCollection()
    .AddDocumentTabs()
    .AddRunicViews()
    .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// The main thread waits for WebUI while the application runs; Run cleans up WebUI's native state at the end.
return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<DocumentsWindow>(new RunicWindowOptions { Width = 900, Height = 640 });
    await window.WaitForCloseAsync();
    return 0;
});

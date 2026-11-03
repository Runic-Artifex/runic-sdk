using DocumentTabs;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using Runic.Platform;
using Runic.Platform.Runtime;

// One running instance: a second launch, such as `document-tabs document-tabs://documents/2/info` from a registered URL
// scheme, forwards its arguments to it and ends.
// The claim blocks so the main thread, which WebUI runs on below, stays this thread.
var launch = ApplicationLaunch.FromProcess(args);
var claim = ApplicationInstance.ClaimAsync("org.runicartifex.examples.DocumentTabs", launch).AsTask().GetAwaiter().GetResult();
if (claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }) return 0;
var instance = claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary primary }
    ? primary.Instance
    : null;
// Created right after the claim, so later launches are received at once; their requests wait for the handler below.
var opens = ApplicationInstance.CreateOpenRequests(new DesktopOpenOptions([DocumentsViewModel.LinkScheme]), instance, launch);

// The tabs are saved next to the application's other state when the window closes, and reopened at the next start.
var stateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunicArtifex", "DocumentTabs", "navigation.json");
using var provider = new ServiceCollection()
    .AddDocumentTabs(stateFile)
    .AddRunicViews()
    .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// The main thread waits for WebUI while the application runs; Run cleans up WebUI's native state at the end.
try
{
    return RunicCsWebUiHost.Run(provider, async host =>
    {
        await using var window = await host.OpenWindowAsync<DocumentsWindow>(new RunicWindowOptions { Width = 900, Height = 640 });
        var documents = window.DataContext!;
        await documents.RestoreNavigationAsync();

        // Links from this launch and from later launches that the instance forwards. Raised on a worker thread;
        // navigation requests may come from any thread.
        opens.Opened += (_, request) => _ = Task.Run(async () =>
        {
            foreach (var link in request.Items) await documents.OpenLinkAsync(link);
        });
        await window.WaitForCloseAsync();
        return 0;
    });
}
finally
{
    opens.DisposeAsync().AsTask().GetAwaiter().GetResult();
    instance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

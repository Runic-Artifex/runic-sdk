using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;
using WebUIToolkit.Hosting.WebUi;

FrontendAssetManifest manifest = new FrontendAssetManifestBuilder().Build(
    [new FrontendAssetBuildItem("index.html", Encoding.UTF8.GetBytes("<main>Hosting UI</main>"), true)]);
var provider = new MemoryAssets(manifest);
var stop = new ApplicationStopControllerBinding();
var genericHost = new GenericHostWebUIToolkitApplicationBuilder();
genericHost.Application.AddValidator(
    LaunchKind.UserInterface,
    new FrontendAssetValidator(provider));
genericHost.Application.AddModeRunner(new WebUiModeRunner(
    new SampleBrowserFactory(),
    new SampleRootFactory(),
    new FrontendAssetEndpoint(provider, new Uri("app://hosting-ui/")),
    stop,
    new WebUiModeOptions(
        new BrowserHostOptions("hosting-ui"),
        new BrowserWindowOptions("root", "Hosting UI"),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2))));

await using WebUIToolkitApplication application = genericHost.Build();
stop.Bind(application.StopController);
ApplicationRunResult result = await application.RunAsync();
return result.ExitCode ?? 1;

internal sealed class MemoryAssets(IFrontendAssetManifest manifest) : IFrontendAssetProvider
{
    public IFrontendAssetManifest Manifest { get; } = manifest;
    public ValueTask ValidateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream());
}

internal sealed class SampleBrowserFactory : IBrowserHostFactory
{
    public ValueTask<IBrowserHost> CreateAsync(
        BrowserHostOptions options,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IBrowserHost>(new SampleBrowserHost());
}

internal sealed class SampleBrowserHost : IBrowserHost
{
    public IUiDispatcher Dispatcher { get; } = new InlineDispatcher();
    public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<IBrowserWindow> CreateWindowAsync(
        BrowserWindowOptions options,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IBrowserWindow>(new SampleBrowserWindow());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SampleBrowserWindow : IBrowserWindow
{
    public event EventHandler? CloseRequested;
    public ValueTask NavigateAsync(Uri entryPoint, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask ShowAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public Task WaitForCloseAsync(CancellationToken cancellationToken)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken) => callback(cancellationToken);
    public ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken) => callback(cancellationToken);
}

internal sealed class SampleRootFactory : IRootSessionFactory
{
    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IRootSession>(new SampleRoot());
}

internal sealed class SampleRoot : IRootSession
{
    public ValueTask ActivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

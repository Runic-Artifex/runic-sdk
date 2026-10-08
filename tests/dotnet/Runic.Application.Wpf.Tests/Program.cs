using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Navigation;

internal static class Program
{
#if WINDOWS
    [STAThread]
    private static int Main()
    {
        var application = new System.Windows.Application();
        var exitCode = 0;
        application.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(); await NativeChecks.RunAsync(); }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { application.Shutdown(); }
        });
        application.Run();
        return exitCode;
    }
#else
    private static Task Main() => RunAsync();
#endif
    private static async Task RunAsync()
    {
        await MissingBridgeFailsBeforeListener();
        await BorrowedResourcesSurviveCloseAndAcceptedWork();
        await NativeUnloadReleasesSession();
        Check(WpfPresentationOptions.Validate(new()).Count == 0, "Default options produced layout warnings.");
        var warnings = WpfPresentationOptions.Validate(new() { Width = 1200, Transparent = true, X = 20 });
        Check(warnings.Select(item => item.Option).ToHashSet().SetEquals(["Width", "Transparent", "X"]), "Unsupported layout options were not reported.");
        Console.WriteLine("Runic.Application.Wpf portable session checks passed.");
    }

    private static async Task MissingBridgeFailsBeforeListener()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var host = await DesktopHost.StartAsync();
        try
        {
            await services.CreateWpfViewAsync(host, HtmlOptions(), new Model());
            throw new InvalidOperationException("Missing Bridge was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("AddRunicViews", StringComparison.Ordinal)) { }
        Check(host.Port == 0, "Missing registration allocated a listener.");
    }

    private static async Task BorrowedResourcesSurviveCloseAndAcceptedWork()
    {
        var model = new Model();
        var probe = new BorrowedService();
        await using var context = new RunicModelContext();
        using var existingLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var calls = new InMemoryViewTransport();
        var collection = new ServiceCollection();
        collection.AddSingleton(probe);
        collection.AddSingleton<Func<IBridgeTransport, WindowContentSession, Model, IDisposable>>(
            (transport, content, supplied) => new SlowBridge(calls, supplied, content));
        await using var services = collection.BuildServiceProvider();
        await using var host = await DesktopHost.StartAsync();
        var view = await services.CreateWpfViewAsync(host, HtmlOptions(), model, context);
        var url = view.Surface.Url;
        Check(ReferenceEquals(view.ViewModel, model), "The host substituted a model.");
        Check(ReferenceEquals(RunicModelContextRegistry.Shared.GetRequired(model), context), "The session changed the model context.");
        var save = calls.CallAsync("editorSave").AsTask();
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await view.CloseAsync(TimeSpan.Zero);
        Check(!result.Drained && result.RemainingOperations == 1 && !result.Completion.IsCompleted, "Close did not retain accepted work.");
        Check(!model.Disposed && !probe.Disposed, "Close disposed borrowed application resources.");
        model.Release.SetResult();
        await result.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        await view.DisposeAsync();
        Check(!model.Disposed && !probe.Disposed, "Drain disposed borrowed application resources.");
        Check(ReferenceEquals(RunicModelContextRegistry.Shared.GetRequired(model), context), "Close removed the navigation-owned model lease.");
        var ran = false;
        await context.InvokeAsync(() => ran = true);
        Check(ran, "Close shut down the application context.");
        using var http = new HttpClient();
        Check((await http.GetAsync(url)).StatusCode == HttpStatusCode.NotFound, "Closed view assets remained available.");
    }

    private static async Task NativeUnloadReleasesSession()
    {
        var model = new Model();
        var attachment = new BorrowedService();
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<IBridgeTransport, Model, IDisposable>>((_, _) => attachment);
        await using var services = collection.BuildServiceProvider();
        var factory = new FakeFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var view = await services.CreateWpfViewAsync(host, HtmlOptions(), model);
        var url = view.Surface.Url;
        await view.OpenAsync();
        Check(factory.Host.Options?.DocumentStartScript is { Length: > 0 }, "Desktop did not supply document-start authentication.");
        using var http = new HttpClient();
        using var forged = new HttpRequestMessage(HttpMethod.Get, url);
        forged.Headers.Host = "attacker.invalid";
        Check((await http.SendAsync(forged)).StatusCode == HttpStatusCode.MisdirectedRequest, "The child surface admitted a hostile Host header.");
        using var foreignScript = new HttpRequestMessage(HttpMethod.Get, new Uri(url, "webui.js"));
        foreignScript.Headers.Add("Sec-Fetch-Site", "cross-site");
        foreignScript.Headers.Add("Sec-Fetch-Dest", "script");
        Check((await http.SendAsync(foreignScript)).StatusCode == HttpStatusCode.Forbidden, "The child surface admitted a foreign bootstrap load.");
        await factory.Host.CloseAsync();
        await view.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Check(attachment.Disposed && !model.Disposed, "Native unload did not release the attachment or disposed the model.");
        Check((await http.GetAsync(url)).StatusCode == HttpStatusCode.NotFound, "Unloaded child assets remained available.");
    }

    internal static DesktopSurfaceOptions HtmlOptions() => new()
    { Content = new DesktopContent.Html("<!doctype html><script src=\"webui.js\"></script><input id=\"editor\" aria-label=\"Editor\">") };
    internal static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class BorrowedService : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    private sealed class Model : INotifyPropertyChanged, IDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ICommand Save { get; } = new NoopCommand();
        internal bool Disposed { get; private set; }
        internal async Task SaveAsync() { Started.SetResult(); await Release.Task; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Dispose() => Disposed = true;
    }
    private sealed class SlowBridge(IBridgeTransport transport, Model model, WindowContentSession content)
        : ViewModelBridge<Model>(transport, model, "editor", static (writer, _, revision) =>
        { writer.WriteStartObject(); writer.WriteNumber("revision", revision); writer.WriteEndObject(); }, [],
        [new("Save", vm => vm.Save, ExecuteAsync: (vm, _, _) => vm.SaveAsync(), CanExecute: (_, _) => true)], content: content);
    private sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
    private sealed class FakeFactory : IDesktopWindowHostFactory
    {
        internal FakeHost Host { get; } = new();
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => Host;
    }
    private sealed class FakeHost : IDesktopWindowHost
    {
        internal DesktopWindowHostOptions? Options { get; private set; }
        public bool SupportsDocumentStartScript => true;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => 0;
        public event EventHandler? Closed;
        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { Options = options; IsOpen = true; return ValueTask.CompletedTask; }
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        { if (IsOpen) { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); } return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => CloseAsync();
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

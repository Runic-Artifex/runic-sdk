using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NavigationConsumer;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

// A minimal consumer of the packed packages: AddRunicNavigation, a generated
// region slot, a guard and Back through a CS-WebUI Bridge window. The same
// program runs with JIT and as a NativeAOT publish (package-smoke.mjs).
var log = new ErrorLog();
var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(log);
services.AddRunicNavigation();
services.AddScoped<HomeViewModel>();
services.AddScoped<ShellViewModel>();
services.AddRunicViews();
await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
{
    provider.ValidateWindow<ShellViewModel>();
    var window = provider.OpenWindow<ShellWindow, ShellViewModel>(host => new ShellWindow(host));
    var shell = window.DataContext!;
    var navigator = shell.Navigator;
    var home = (HomeViewModel)shell.Main.Current!;

    // The window session and the navigator share the scoped model context,
    // and the generated Bridge bound the session as the presentation.
    var context = RunicModelContextRegistry.Shared.GetRequired(shell);
    using (var otherTransport = new DetachedTransport())
    using (var other = new WindowContentSession(otherTransport, modelContext: context))
        Require(Throws<InvalidOperationException>(() => navigator.BindPresentation(other)),
            "The generated Bridge did not bind the window session to the navigator.");
    Require(Throws<ArgumentException>(() => navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(new InputPage()))),
        "An initializable initial target was accepted.");

    // Push an owned editor created with the window's services.
    EditorViewModel? editor = null;
    var pushed = await shell.Main.PushAsync(NavigationTarget.Create<IPageViewModel>(window =>
        editor = new EditorViewModel(window.GetRequiredService<HomeViewModel>() == home ? "editor" : "wrong scope")));
    Require(pushed is NavigationResult<IPageViewModel>.Committed { Current.Content.Title: "editor" },
        $"The push did not commit: {pushed}");
    Require(shell.Main.CanGoBack && RunicModelContextRegistry.Shared.TryGet(editor!, out var bound) && bound == context,
        "The owned editor was not bound to the window's model context.");

    // The guard vetoes the first Back; history is unchanged.
    var vetoed = await shell.Main.BackAsync();
    Require(vetoed is NavigationResult<IPageViewModel>.Rejected { Reason: NavigationRejection.Guard } && shell.Main.Current == editor,
        $"The guard did not veto Back: {vetoed}");
    editor!.CanLeave = true;
    var back = await shell.Main.BackAsync();
    Require(back is NavigationResult<IPageViewModel>.Committed && shell.Main.Current == home && editor.Disposed,
        $"Back did not retire the owned editor: {back}");

    // The window's next capture presents the borrowed home through the session,
    // which binds it to the window's context (the navigator never binds borrowed content).
    await Until(() => RunicModelContextRegistry.Shared.TryGet(home, out var presented) && presented == context,
        "The Bridge window did not present the region's current content.");

    // Closing the window disposes its scope: the navigator retires the owned
    // entry that is still current, after the session is gone.
    EditorViewModel? last = null;
    await shell.Main.PushAsync(NavigationTarget.Create<IPageViewModel>(_ => last = new EditorViewModel("last") { CanLeave = true }));
    var close = await window.CloseAsync(TimeSpan.FromSeconds(5));
    await close.Completion;
    await window.DisposeAsync();
    Require(last is { Disposed: true }, "Closing the window did not retire the owned entry.");
    Require(Throws<ObjectDisposedException>(() => navigator.CreateRegion<IPageViewModel>(new object())),
        "The window scope did not dispose the navigator.");
}
Require(log.Errors.Count == 0, $"Errors were logged: {string.Join("; ", log.Errors)}");
WebUiApplication.Clean();
Console.WriteLine("NAVIGATION_CONSUMER_OK");

static async Task Until(Func<bool> condition, string message)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
        await Task.Delay(10);
    }
}

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

namespace NavigationConsumer
{
    internal sealed class ErrorLog : ILoggerFactory
    {
        public List<string> Errors { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(ErrorLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                lock (owner.Errors) owner.Errors.Add($"{category} {eventId.Id} {formatter(state, exception)} {exception}");
            }
        }
    }

    internal sealed class DetachedTransport : IBridgeTransport, IDisposable
    {
        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) => this;
        public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) => this;
        public void Publish(string name, string stateJson) { }
        public void Dispose() { }
    }
}

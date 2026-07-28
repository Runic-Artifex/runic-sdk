using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Desktop;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("options normalize and validate configuration", OptionsValidate),
            ("entry points translate only safe app paths", EntryPointsValidate),
            ("dispatcher serializes and permits reentrancy", DispatcherSerializes),
            ("host applies window configuration headlessly", HostAppliesConfiguration),
            ("all presentation modes map to CsWebUi", PresentationModesMap),
            ("disconnect and application exit complete window lifetime", WindowCloseSignals),
            ("close and disposal are idempotent", CloseAndDisposalAreIdempotent),
        ];

        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"ok - {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"not ok - {name}");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        return 0;
    }

    private static Task OptionsValidate()
    {
        var options = new CsWebUiAdapterOptions(
            ".",
            CsWebUiPresentationMode.Browser,
            WebUiBrowser.Chrome,
            static _ => { });
        Equal(Path.GetFullPath("."), options.WebRoot);
        Equal(CsWebUiPresentationMode.Browser, options.PresentationMode);
        Equal(WebUiBrowser.Chrome, options.Browser);
        True(options.ConfigureWindow is not null);

        Throws<ArgumentException>(() =>
            _ = new CsWebUiAdapterOptions(" ", CsWebUiPresentationMode.Auto));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new CsWebUiAdapterOptions(".", (CsWebUiPresentationMode)99));
        Throws<ArgumentException>(() =>
            _ = new CsWebUiAdapterOptions(
                ".",
                CsWebUiPresentationMode.Browser,
                WebUiBrowser.WebView));
        return Task.CompletedTask;
    }

    private static Task EntryPointsValidate()
    {
        Equal(
            "index.html",
            CsWebUiEntryPointPath.Translate(new Uri("app://demo/index.html"), "demo"));
        Equal(
            "pages/My Page.html",
            CsWebUiEntryPointPath.Translate(
                new Uri("app://demo/pages/My%20Page.html"),
                "demo"));

        Uri[] rejected =
        [
            new("https://demo/index.html"),
            new("app://other/index.html"),
            new("app://demo/index.html?theme=dark"),
            new("app://demo/index.html#main"),
            new("app://demo:1234/index.html"),
            new("app://demo/"),
            new("app://demo/%2e%2e/secret.html"),
            new("app://demo/%2Fetc/passwd"),
            new("app://demo/folder%5Csecret.html"),
        ];
        foreach (Uri entryPoint in rejected)
        {
            try
            {
                _ = CsWebUiEntryPointPath.Translate(entryPoint, "demo");
            }
            catch (ArgumentException)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Expected entry point '{entryPoint.OriginalString}' to be rejected.");
        }

        Equal(
            "http://127.0.0.1:9123/pages/My%20Page.html",
            CsWebUiEntryPointPath.BuildNavigationUrl(
                "http://127.0.0.1:9123/index.html",
                "pages/My Page.html"));

        return Task.CompletedTask;
    }

    private static async Task DispatcherSerializes()
    {
        var dispatcher = new CsWebUiDispatcher();
        int active = 0;
        int greatestActive = 0;
        Task[] calls = Enumerable.Range(0, 8)
            .Select(_ => dispatcher.InvokeAsync(
                async cancellationToken =>
                {
                    int current = Interlocked.Increment(ref active);
                    UpdateMaximum(ref greatestActive, current);
                    True(dispatcher.CheckAccess());
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    Interlocked.Decrement(ref active);
                },
                CancellationToken.None).AsTask())
            .ToArray();
        await Task.WhenAll(calls).ConfigureAwait(false);
        Equal(1, greatestActive);
        False(dispatcher.CheckAccess());

        await dispatcher.InvokeAsync(
            cancellationToken => dispatcher.InvokeAsync(
                _ =>
                {
                    True(dispatcher.CheckAccess());
                    return ValueTask.CompletedTask;
                },
                cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task HostAppliesConfiguration()
    {
        using var webRoot = new TemporaryDirectory();
        var runtime = new FakeRuntime();
        var adapterOptions = new CsWebUiAdapterOptions(
            webRoot.Path,
            CsWebUiPresentationMode.Browser,
            WebUiBrowser.Chrome,
            static _ => { });
        var factory = new CsWebUiBrowserHostFactory(adapterOptions, runtime);
        await using IBrowserHost host = await factory.CreateAsync(
            new BrowserHostOptions("demo"),
            CancellationToken.None);

        await ThrowsAsync<InvalidOperationException>(async () =>
            _ = await host.CreateWindowAsync(
                new BrowserWindowOptions("main", "Demo"),
                CancellationToken.None));

        await host.InitializeAsync(CancellationToken.None);
        using var profileRoot = new TemporaryDirectory();
        string profilePath = Path.Combine(profileRoot.Path, "profile");
        await using IBrowserWindow window = await host.CreateWindowAsync(
            new BrowserWindowOptions(
                "main",
                "Demo title",
                900,
                600,
                isResizable: false,
                browserProfile: new DesktopBrowserProfile("demo-profile", profilePath)),
            CancellationToken.None);
        FakeWindow native = runtime.Windows.Single();
        Equal(webRoot.Path, native.RootFolder);
        Equal((uint)900, native.Width);
        Equal((uint)600, native.Height);
        False(native.IsResizable);
        False(native.IsPublic);
        True(native.ConfigurationHookSupplied);
        Equal("demo-profile", native.ProfileName);
        Equal(profilePath, native.ProfilePath);
        True(Directory.Exists(profilePath));

        await ThrowsAsync<InvalidOperationException>(async () =>
            await window.ShowAsync(CancellationToken.None));
        await window.NavigateAsync(new Uri("app://demo/index.html"), CancellationToken.None);
        Equal(0, native.NavigateCalls.Count);
        await window.ShowAsync(CancellationToken.None);
        Equal("index.html", native.ShownPath);
        Equal(CsWebUiPresentationMode.Browser, native.PresentationMode);
        Equal(WebUiBrowser.Chrome, native.Browser);
        Equal("Demo title", native.Title);

        await window.NavigateAsync(new Uri("app://demo/settings.html"), CancellationToken.None);
        Equal("settings.html", native.NavigateCalls.Single());

        var desktop = (IBrowserWindowDesktopAdapter)window;
        True(desktop.Capabilities[DesktopCapability.WindowFocus].IsSupported);
        True(desktop.Capabilities[DesktopCapability.BrowserProfile].IsSupported);
        True(desktop.Capabilities[DesktopCapability.BrowserStorage].IsSupported);
        await desktop.FocusWindowAsync(CancellationToken.None);
        await desktop.FocusElementAsync("title", CancellationToken.None);
        await desktop.SetSizeAsync(new DesktopSize(700, 500), CancellationToken.None);
        await desktop.SetPositionAsync(new DesktopPosition(20, 30), CancellationToken.None);
        await desktop.CenterAsync(CancellationToken.None);
        await desktop.SetStateAsync(DesktopWindowState.Minimized, CancellationToken.None);
        await desktop.SetStateAsync(DesktopWindowState.Maximized, CancellationToken.None);
        await desktop.SetStateAsync(DesktopWindowState.Normal, CancellationToken.None);
        Equal(2, native.FocusCount);
        Equal((uint)900, native.Width);
        Equal((uint)600, native.Height);
        Equal((uint)20, native.PositionX);
        Equal((uint)30, native.PositionY);
        Equal(1, native.CenterCount);
        Equal(1, native.MinimizeCount);
        Equal(1, native.MaximizeCount);
        True(native.Scripts.Any(script => script.Contains("title", StringComparison.Ordinal)));
        True(native.Scripts.Any(
            script => script.Contains("__webuitoolkitDesktop", StringComparison.Ordinal)));
        native.DesktopScriptResponder = script =>
        {
            const string marker = "invoke(\"";
            int start = script.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            int end = script.IndexOf('"', start);
            string id = script[start..end];
            return $$"""{"kind":"result","id":"{{id}}","ok":true,"value":"stored"}""";
        };
        Equal(
            "\"stored\"",
            await desktop.InvokeBrowserAsync(
                "storage.read",
                """{"key":"theme"}""",
                CancellationToken.None));
        BrowserDesktopEventArgs? observedEvent = null;
        desktop.DesktopEventReceived += (_, desktopEvent) => observedEvent = desktopEvent;
        native.RaiseDesktop(
            """{"kind":"event","name":"accelerator","id":"save","payload":{}}""");
        Equal("save", observedEvent!.Id);
    }

    private static async Task PresentationModesMap()
    {
        using var webRoot = new TemporaryDirectory();
        (CsWebUiPresentationMode Mode, WebUiBrowser Browser)[] cases =
        [
            (CsWebUiPresentationMode.Auto, WebUiBrowser.AnyBrowser),
            (CsWebUiPresentationMode.Browser, WebUiBrowser.Firefox),
            (CsWebUiPresentationMode.WebView, WebUiBrowser.AnyBrowser),
        ];

        foreach ((CsWebUiPresentationMode mode, WebUiBrowser browser) in cases)
        {
            var runtime = new FakeRuntime();
            var factory = new CsWebUiBrowserHostFactory(
                new CsWebUiAdapterOptions(webRoot.Path, mode, browser),
                runtime);
            await using IBrowserHost host = await factory.CreateAsync(
                new BrowserHostOptions("demo"),
                CancellationToken.None);
            await host.InitializeAsync(CancellationToken.None);
            await using IBrowserWindow window = await CreateShownWindow(host);
            FakeWindow native = runtime.Windows.Single();
            Equal(mode, native.PresentationMode);
            Equal(browser, native.Browser);
        }
    }

    private static async Task WindowCloseSignals()
    {
        using var webRoot = new TemporaryDirectory();
        var disconnectedRuntime = new FakeRuntime();
        await using IBrowserHost disconnectedHost = await CreateHost(
            webRoot.Path,
            disconnectedRuntime);
        await using IBrowserWindow disconnectedWindow = await CreateShownWindow(disconnectedHost);
        int closeRequests = 0;
        disconnectedWindow.CloseRequested += (_, _) => closeRequests++;
        disconnectedRuntime.Windows.Single().Raise(WebUiEventType.Disconnected);
        await disconnectedWindow.WaitForCloseAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Equal(1, closeRequests);

        var exitRuntime = new FakeRuntime();
        await using IBrowserHost exitHost = await CreateHost(webRoot.Path, exitRuntime);
        await using IBrowserWindow exitWindow = await CreateShownWindow(exitHost);
        int exitCloseRequests = 0;
        exitWindow.CloseRequested += (_, _) => exitCloseRequests++;
        exitRuntime.SignalApplicationExit();
        await exitWindow.WaitForCloseAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Equal(1, exitCloseRequests);
    }

    private static async Task CloseAndDisposalAreIdempotent()
    {
        using var webRoot = new TemporaryDirectory();
        var runtime = new FakeRuntime();
        IBrowserHost host = await CreateHost(webRoot.Path, runtime);
        IBrowserWindow window = await CreateShownWindow(host);
        FakeWindow native = runtime.Windows.Single();

        await window.CloseAsync(CancellationToken.None);
        await window.CloseAsync(CancellationToken.None);
        await window.WaitForCloseAsync(CancellationToken.None);
        Equal(1, native.CloseCount);

        await window.DisposeAsync();
        await window.DisposeAsync();
        Equal(1, native.DisposeCount);
        await host.DisposeAsync();
        await host.DisposeAsync();
        Equal(1, native.DisposeCount);
    }

    private static async Task<IBrowserHost> CreateHost(string webRoot, FakeRuntime runtime)
    {
        var factory = new CsWebUiBrowserHostFactory(
            new CsWebUiAdapterOptions(webRoot),
            runtime);
        IBrowserHost host = await factory.CreateAsync(
            new BrowserHostOptions("demo"),
            CancellationToken.None);
        await host.InitializeAsync(CancellationToken.None);
        return host;
    }

    private static async Task<IBrowserWindow> CreateShownWindow(IBrowserHost host)
    {
        IBrowserWindow window = await host.CreateWindowAsync(
            new BrowserWindowOptions("main", "Demo"),
            CancellationToken.None);
        await window.NavigateAsync(new Uri("app://demo/index.html"), CancellationToken.None);
        await window.ShowAsync(CancellationToken.None);
        return window;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= candidate)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, current) != current);
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

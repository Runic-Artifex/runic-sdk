using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// S3 (L3-5): an embedded window opens with the native direction and web view language of its window locale
// (IRunicWindowLocale), and its direction follows a later switch; DesktopWindowOptions that set them win.
internal sealed class WindowLocaleChecks
{
    [Test, NotInParallel(Order = 49)]
    public async Task TheWindowLocaleGivesTheWindowItsDirectionAndLanguage()
    {
        if (OperatingSystem.IsMacOS()) return; // AppKit takes both from the application bundle.
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
        });
        LocaleViewModel.Initial = "ar-EG";
        await using var window = await windows.OpenWindowAsync<LocaleWindow>();
        var options = factory.Hosts[0].Options!;
        Require(options.LayoutDirection == DesktopLayoutDirection.RightToLeft, $"Direction at open: {options.LayoutDirection}.");
        Require(options.Language == "ar-EG", $"Language at open: {options.Language}.");

        window.ViewModel.Locale.Locale = "de";
        await WaitUntilAsync(() => factory.Hosts[0].Directions.Contains(DesktopLayoutDirection.LeftToRight));
        Require(DesktopPlatformServices.GetDesktopWindow(window)!.LayoutDirection == DesktopLayoutDirection.LeftToRight,
            "The window does not report its new direction.");
        window.ViewModel.Locale.Locale = "he";
        await WaitUntilAsync(() => factory.Hosts[0].Directions.Count == 2);
        Require(factory.Hosts[0].Directions.Last() == DesktopLayoutDirection.RightToLeft, "Hebrew did not mirror the window.");
    }

    [Test, NotInParallel(Order = 50)]
    public async Task WindowOptionsThatSetThemWinAndAProfileKeepsThePlatformLanguage()
    {
        if (OperatingSystem.IsMacOS()) return;
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var profile = Path.Combine(Path.GetTempPath(), "runic-window-locale-" + Guid.NewGuid().ToString("N"));
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, LayoutDirection = DesktopLayoutDirection.LeftToRight, ProfilePath = profile,
            },
        });
        LocaleViewModel.Initial = "ar";
        await using var window = await windows.OpenWindowAsync<LocaleWindow>();
        var options = factory.Hosts[0].Options!;
        Require(options.LayoutDirection == DesktopLayoutDirection.LeftToRight, $"Configured direction replaced: {options.LayoutDirection}.");
        Require(options.Language is null, $"A shared profile got a language: {options.Language}.");
        window.ViewModel.Locale.Locale = "he";
        await Task.Delay(100);
        Require(factory.Hosts[0].Directions.IsEmpty, "A configured direction followed the locale.");
    }

    [Test, NotInParallel(Order = 51)]
    public async Task ALocaleSetWhileTheWindowOpensAndLaterChangesApplyInOrderAndNullReturnsToThePlatforms()
    {
        if (OperatingSystem.IsMacOS()) return;
        await using var provider = CreateProvider();
        // The ViewModel sets its locale while the window opens, after the window options took the one it had.
        var factory = new NativeDispatchFactory { Directs = true, Opening = () => LocaleViewModel.Last!.Locale = "ar" };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
        });
        LocaleViewModel.Initial = null;
        await using var window = await windows.OpenWindowAsync<LocaleWindow>();
        var host = factory.Hosts[0];
        var presentation = DesktopPlatformServices.GetDesktopWindow(window)!;
        Require(host.Options!.LayoutDirection == DesktopLayoutDirection.Default, $"Direction at open: {host.Options.LayoutDirection}.");
        await WaitUntilAsync(() => presentation.LayoutDirection == DesktopLayoutDirection.RightToLeft);
        Require(host.Directions.SequenceEqual([DesktopLayoutDirection.RightToLeft]), $"Directions: {string.Join(", ", host.Directions)}.");

        // Changes while one applies take the direction of the locale they find: the window ends with the last one's.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.HoldDirection = hold;
        window.ViewModel.Locale.Locale = "de";
        await WaitUntilAsync(() => host.Directions.Count == 2);
        string[] concurrent = ["he", "fa", "en"];
        var setters = concurrent.Select(locale => Task.Run(() => window.ViewModel.Locale.Locale = locale)).ToArray();
        await Task.WhenAll(setters);
        window.ViewModel.Locale.Locale = "ur";
        hold.SetResult();
        await WaitUntilAsync(() => presentation.LayoutDirection == DesktopLayoutDirection.RightToLeft && host.Directions.Count == 3);
        await Task.Delay(100);
        Require(host.Directions.SequenceEqual([DesktopLayoutDirection.RightToLeft, DesktopLayoutDirection.LeftToRight, DesktopLayoutDirection.RightToLeft]),
            $"Directions: {string.Join(", ", host.Directions)}.");

        // No window locale ("each page keeps its own") returns the window to the platform's direction.
        window.ViewModel.Locale.Locale = null;
        await WaitUntilAsync(() => presentation.LayoutDirection == DesktopLayoutDirection.Default);
        Require(host.Directions.Last() == DesktopLayoutDirection.Default, $"Directions: {string.Join(", ", host.Directions)}.");
    }

    [Test, NotInParallel(Order = 52)]
    public void TheDirectionOfALocaleWithoutCultureDataComesFromItsScriptOrLanguage()
    {
        Require(WindowLocaleDirection.FromTag("ar") == DesktopLayoutDirection.RightToLeft, "ar");
        Require(WindowLocaleDirection.FromTag("fa-IR") == DesktopLayoutDirection.RightToLeft, "fa-IR");
        Require(WindowLocaleDirection.FromTag("ku-Arab") == DesktopLayoutDirection.RightToLeft, "ku-Arab");
        Require(WindowLocaleDirection.FromTag("sd-Deva") == DesktopLayoutDirection.LeftToRight, "sd-Deva");
        Require(WindowLocaleDirection.FromTag("de-CH") == DesktopLayoutDirection.LeftToRight, "de-CH");
        Require(WindowLocaleDirection.Of("he") == DesktopLayoutDirection.RightToLeft, "he");
        Require(WindowLocaleDirection.Of("pt-BR") == DesktopLayoutDirection.LeftToRight, "pt-BR");
        // Extensions and private use subtags are not scripts; a script comes right after the language (and its extlang).
        // FromTag is the direction under InvariantGlobalization; Of uses the culture data where the process has it.
        (string Tag, DesktopLayoutDirection Direction)[] tags =
        [
            ("ar-u-nu-latn", DesktopLayoutDirection.RightToLeft), ("ar-EG-u-nu-latn", DesktopLayoutDirection.RightToLeft),
            ("fa-x-test", DesktopLayoutDirection.RightToLeft), ("en-u-ca-hebr", DesktopLayoutDirection.LeftToRight),
            ("en-x-arab", DesktopLayoutDirection.LeftToRight), ("az-Arab", DesktopLayoutDirection.RightToLeft),
            ("az-Arab-IR", DesktopLayoutDirection.RightToLeft), ("sr-Latn", DesktopLayoutDirection.LeftToRight),
            ("ar-Latn", DesktopLayoutDirection.LeftToRight), ("ar-aeb-Latn", DesktopLayoutDirection.LeftToRight),
            ("ar-aeb", DesktopLayoutDirection.RightToLeft), ("ar-Arab-u-nu-arab", DesktopLayoutDirection.RightToLeft),
            ("x-arab", DesktopLayoutDirection.LeftToRight),
        ];
        foreach (var (tag, direction) in tags)
        {
            Require(WindowLocaleDirection.FromTag(tag) == direction, $"FromTag({tag}): {WindowLocaleDirection.FromTag(tag)}.");
            Require(WindowLocaleDirection.Of(tag) == direction, $"Of({tag}): {WindowLocaleDirection.Of(tag)}.");
        }
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<LocaleViewModel>();
        services.AddScoped<Func<IBridgeTransport, LocaleViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddRunicWindowService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// Sets its window's locale in the constructor, before the window opens.
sealed class LocaleViewModel : INotifyPropertyChanged
{
    internal static string? Initial;
    internal static IRunicWindowLocale? Last;
    public LocaleViewModel(IRunicWindowLocale locale)
    {
        Locale = locale;
        Last = locale;
        locale.Locale = Initial;
    }
    public IRunicWindowLocale Locale { get; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class LocaleWindow : RunicWindow<LocaleViewModel>;

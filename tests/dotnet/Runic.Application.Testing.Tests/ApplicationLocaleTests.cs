using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// IRunicApplicationLocale: one locale for the application, which every Window without one of its own follows.
public sealed class ApplicationLocaleTests
{
    private const string Feature = "translation-text/1";

    private static ServiceProvider Services(bool windowSetsLocale = false)
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddRunicWindowService();
        if (windowSetsLocale)
            services.AddScoped<TranslatedTextViewModel>(provider => new(provider.GetRequiredService<IRunicWindowLocale>()));
        else
            services.AddScoped<TranslatedTextViewModel>(_ => new());
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, TranslatedTextViewModel, IDisposable>>(
            _ => (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static RunicWindowTestHost<TranslatedTextViewModel> Host(TranslatedTextWindow window) =>
        ((TestWindow<TranslatedTextViewModel>)window.Host).Host;

    [Test]
    public async Task OpenAndNewWindowsFollowTheApplicationLocaleUnlessTheySetTheirOwnAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        Require(application.Locale is null, "The application locale is not null by default.");
        var windows = new TestWindowHost(provider);
        await using var first = await windows.OpenWindowAsync<TranslatedTextWindow>();
        await using var second = await windows.OpenWindowAsync<TranslatedTextWindow>();
        Require(Host(first).Content.Locale is null, "A Window took a locale while the application sets none.");
        var firstLocale = first.Services.GetRequiredService<IRunicWindowLocale>();
        var secondLocale = second.Services.GetRequiredService<IRunicWindowLocale>();
        var changes = new List<string?>();
        firstLocale.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Host(first).Transport.HelloWith("page", [Feature]);
        await application.SetLocaleAsync("de");
        Require(Host(first).Content.Locale == "de" && Host(second).Content.Locale == "de" && firstLocale.Locale == "de",
            "An open Window did not follow the application locale.");
        Require(changes.SequenceEqual([nameof(IRunicWindowLocale.Locale)]), "The Window's locale did not raise PropertyChanged once.");
        using (var push = await Host(first).Transport.TakePushAsync("page", "locale", TimeSpan.FromSeconds(5)))
            Require(push.RootElement.GetProperty("locale").GetString() == "de", $"The page was not told: {push.RootElement}");

        // A Window opened later starts in it.
        await using var third = await windows.OpenWindowAsync<TranslatedTextWindow>();
        Require(Host(third).Content.Locale == "de", "A new Window did not start in the application locale.");

        // A Window's own locale wins, and keeps it through switches of the application.
        secondLocale.Locale = "fr";
        await application.SetLocaleAsync("it");
        Require(Host(first).Content.Locale == "it" && Host(second).Content.Locale == "fr" && Host(third).Content.Locale == "it",
            "A Window with its own locale followed the application, or another did not.");
        // null follows the application again.
        secondLocale.Locale = null;
        Require(Host(second).Content.Locale == "it" && secondLocale.Locale == "it", "A Window set to null did not follow the application.");

        // null for the application: the following Windows' pages keep their language.
        await application.SetLocaleAsync(null);
        Require(Host(first).Content.Locale is null && firstLocale.Locale is null, "null did not reach a following Window.");
        Require((await LocalePushesAsync(Host(first).Transport, "page", 2)).SequenceEqual(["it", null]),
            "The switches to it and null were not pushed in order.");
    }

    [Test]
    public async Task AWindowWhoseViewModelSetsALocaleKeepsItAsync()
    {
        await using var provider = Services(windowSetsLocale: true);
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        await application.SetLocaleAsync("fr");
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        Require(Host(window).Content.Locale == "de", "The locale the ViewModel's constructor set was replaced by the application's.");
        await application.SetLocaleAsync("it");
        Require(Host(window).Content.Locale == "de", "The Window's own locale followed the application.");
        // WindowContentSession.Locale set directly is the Window's own as well.
        Host(window).Content.Locale = "pt-BR";
        await application.SetLocaleAsync("es");
        Require(Host(window).Content.Locale == "pt-BR", "A locale set on the session followed the application.");
    }

    [Test]
    public async Task FollowersPrepareTheLocaleBeforeTheWindowsAndLocaleChangedAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        var order = new List<string>();
        using var follower = application.Follow(async (locale, _) =>
        {
            await Task.Yield();
            order.Add($"follower {locale}, window {Host(window).Content.Locale ?? "none"}");
        });
        application.LocaleChanged += (_, args) =>
            order.Add($"changed {args.OldLocale ?? "none"} -> {args.Locale ?? "none"}, window {Host(window).Content.Locale}");
        await application.SetLocaleAsync("de");
        await application.SetLocaleAsync("de");
        Require(order.SequenceEqual(["follower de, window none", "changed none -> de, window de"]),
            $"The switch ran in another order: {string.Join("; ", order)}");

        // null calls no follower.
        order.Clear();
        await application.SetLocaleAsync(null);
        Require(order.SequenceEqual(["changed de -> none, window "]), $"null called a follower: {string.Join("; ", order)}");

        // A follower that throws stops the switch.
        using var failing = application.Follow((locale, _) => locale == "xx" ? throw new InvalidOperationException("no xx") : ValueTask.CompletedTask);
        order.Clear();
        var thrown = false;
        try { await application.SetLocaleAsync("xx"); }
        catch (InvalidOperationException) { thrown = true; }
        Require(thrown && application.Locale is null && Host(window).Content.Locale is null,
            "A failed follower did not stop the switch.");
        Require(order.SequenceEqual(["follower xx, window none"]), $"The switch went on after the failure: {string.Join("; ", order)}");

        // A disposed follower is not called again; switches run one at a time.
        follower.Dispose();
        order.Clear();
        await Task.WhenAll(application.SetLocaleAsync("en").AsTask(), application.SetLocaleAsync("fr").AsTask());
        Require(application.Locale == "fr" && Host(window).Content.Locale == "fr" && order.Count == 2
            && order.All(entry => entry.StartsWith("changed", StringComparison.Ordinal)), $"Concurrent switches: {string.Join("; ", order)}");

        foreach (var invalid in new[] { "", "de_DE", "1de", "de-" })
        {
            var refused = false;
            try { await application.SetLocaleAsync(invalid); } catch (ArgumentException) { refused = true; }
            Require(refused, $"'{invalid}' was accepted as an application locale.");
        }
    }

    [Test]
    public async Task ATestHostFollowsItsApplicationLocaleAsync()
    {
        // A host created with a constructor has a locale of its own.
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(new TranslatedTextViewModel(),
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        host.Transport.HelloWith("page", [Feature]);
        await host.ApplicationLocale.SetLocaleAsync("de");
        Require(host.Content.Locale == "de", "The test host's window did not follow its application locale.");
        using (var push = await host.Transport.TakePushAsync("page", "locale", TimeSpan.FromSeconds(5)))
            Require(push.RootElement.GetProperty("locale").GetString() == "de", $"The page was not told: {push.RootElement}");

        // A host created from services uses the registered one.
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        using var created = RunicWindowTestHost.Create<TranslatedTextViewModel>(scope.ServiceProvider);
        Require(ReferenceEquals(created.ApplicationLocale, provider.GetRequiredService<IRunicApplicationLocale>()),
            "The test host did not use the registered application locale.");
        await created.ApplicationLocale.SetLocaleAsync("fr");
        Require(created.Content.Locale == "fr", "The created test host's window did not follow the application locale.");
    }

    [Test]
    public void AWindowLocaleOutsideAWindowFollowsTheApplication()
    {
        using var provider = new ServiceCollection().AddRunicWindowService().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<IRunicWindowLocale>();
        var changes = 0;
        window.PropertyChanged += (_, _) => changes++;
        provider.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync("de").AsTask().GetAwaiter().GetResult();
        Require(window.Locale == "de" && changes == 1, $"An unopened Window's locale is {window.Locale} after {changes} changes.");
        window.Locale = "fr";
        Require(window.Locale == "fr" && changes == 2, "Its own locale did not win.");
        scope.Dispose();
        // A disposed scope's context no longer follows.
        provider.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync("it").AsTask().GetAwaiter().GetResult();
        Require(changes == 2, "A disposed Window context still followed the application.");
    }

    // The locales of the next locale pushes to a connection, in order.
    private static async Task<IReadOnlyList<string?>> LocalePushesAsync(InMemoryBridgeTransport transport, string connection, int count)
    {
        List<string?> locales = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (locales.Count < count && DateTime.UtcNow < deadline)
        {
            foreach (var message in transport.TakeMessages())
            {
                if (message.ConnectionKey != connection) continue;
                using var push = JsonDocument.Parse(message.Json);
                if (push.RootElement.GetProperty("kind").GetString() == "locale") locales.Add(push.RootElement.GetProperty("locale").GetString());
            }
            if (locales.Count < count) await Task.Delay(10);
        }
        return locales;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

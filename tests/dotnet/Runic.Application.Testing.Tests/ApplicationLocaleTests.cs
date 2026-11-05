using System.ComponentModel;
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
        using var follower = application.Follow(async (locale, token) =>
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
        // The events report one chain of changes, in order, that ends in the last switch's locale.
        Require(application.Locale == "fr" && Host(window).Content.Locale == "fr"
            && (order.SequenceEqual(["changed none -> fr, window fr"])
                || (order is [var first, "changed en -> fr, window fr"] && first.StartsWith("changed none -> en", StringComparison.Ordinal))),
            $"Concurrent switches: {string.Join("; ", order)}");

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
        window.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(IRunicWindowLocale.Locale)) changes++; };
        provider.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync("de").AsTask().GetAwaiter().GetResult();
        Require(window.Locale == "de" && changes == 1, $"An unopened Window's locale is {window.Locale} after {changes} changes.");
        window.Locale = "fr";
        Require(window.Locale == "fr" && window.OwnLocale == "fr" && changes == 2, "Its own locale did not win.");
        scope.Dispose();
        // A disposed scope's context no longer follows.
        provider.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync("it").AsTask().GetAwaiter().GetResult();
        Require(changes == 2, "A disposed Window context still followed the application.");
    }

    [Test]
    public async Task OverlappingSwitchesRaiseTheirEventsInOrderAndDropSupersededOnesAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        application.LocaleChanged += (_, args) =>
        {
            lock (events) events.Add($"{args.OldLocale ?? "none"} -> {args.Locale ?? "none"}");
            if (args.Locale == "de")
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        // de's handler holds the event while fr and it commit: fr's event is superseded by it, which reports de -> it.
        var de = Task.Run(() => application.SetLocaleAsync("de").AsTask());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fr = application.SetLocaleAsync("fr").AsTask();
        await Task.WhenAny(fr, Task.Delay(200));
        Require(!fr.IsCompleted, "A switch returned before the event of an earlier switch was raised.");
        var it = Task.Run(() => application.SetLocaleAsync("it").AsTask());
        await WaitUntilAsync(() => Host(window).Content.Locale == "it");
        release.Set();
        await Task.WhenAll(de, fr, it).WaitAsync(TimeSpan.FromSeconds(10));
        Require(application.Locale == "it" && Host(window).Content.Locale == "it", "The last switch did not win.");
        lock (events)
            Require(events.SequenceEqual(["none -> de", "de -> it"]), $"The events were not ordered and coalesced: {string.Join("; ", events)}");

        // Many overlapping switches: the events form one chain that ends in the final locale.
        events.Clear();
        string[] locales = ["en", "fr", "es", "pt", "nl", "sv"];
        await Task.WhenAll(locales.Select(locale => Task.Run(() => application.SetLocaleAsync(locale).AsTask())));
        var final = application.Locale;
        Require(Host(window).Content.Locale == final, "The Window does not show the final locale.");
        lock (events)
        {
            var previous = "it";
            foreach (var entry in events)
            {
                var parts = entry.Split(" -> ");
                Require(parts[0] == previous, $"An event skipped or repeated a change: {string.Join("; ", events)}");
                previous = parts[1];
            }
            Require(previous == final, $"The last event does not report the final locale {final}: {string.Join("; ", events)}");
        }
    }

    [Test]
    public async Task AFailingWindowDoesNotStopTheSwitchAndARetryUpdatesItAgainAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        var windows = new TestWindowHost(provider);
        await using var first = await windows.OpenWindowAsync<TranslatedTextWindow>();
        await using var second = await windows.OpenWindowAsync<TranslatedTextWindow>();
        var firstLocale = first.Services.GetRequiredService<IRunicWindowLocale>();
        var failing = true;
        var seen = new List<string?>();
        firstLocale.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(IRunicWindowLocale.Locale)) return;
            seen.Add(firstLocale.Locale);
            if (failing) throw new InvalidOperationException("handler failed");
        };
        var raised = new List<string?>();
        application.LocaleChanged += (_, args) => raised.Add(args.Locale);
        AggregateException? thrown = null;
        try { await application.SetLocaleAsync("de"); }
        catch (AggregateException error) { thrown = error; }
        Require(thrown is { InnerExceptions: [InvalidOperationException { Message: "handler failed" }] },
            $"The Window's failure was not reported: {thrown}");
        Require(application.Locale == "de" && Host(second).Content.Locale == "de" && raised.SequenceEqual(["de"]),
            "A failing Window stopped the other Windows or the event.");

        // The same locale again updates the Window that failed, and raises no event.
        failing = false;
        await application.SetLocaleAsync("de");
        Require(seen.SequenceEqual(["de", "de"]) && Host(first).Content.Locale == "de" && raised.SequenceEqual(["de"]),
            $"A retry did not update the failed Window once: {string.Join(", ", seen)}; events {raised.Count}");
        await application.SetLocaleAsync("de");
        Require(seen.Count == 2, "A retry after success updated the Window again.");

        // A LocaleChanged handler that throws is reported after every handler ran.
        var others = 0;
        application.LocaleChanged += (_, _) => throw new FormatException("menu");
        application.LocaleChanged += (_, _) => others++;
        thrown = null;
        try { await application.SetLocaleAsync("fr"); }
        catch (AggregateException error) { thrown = error; }
        Require(thrown is { InnerExceptions: [FormatException] } && others == 1 && application.Locale == "fr",
            $"A failing handler stopped the others: {thrown}");
    }

    [Test]
    public async Task WindowsFollowInTheirModelContextAndHandlersRunWhereTheySubscribedAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        var context = Host(window).Content.ModelContext ?? throw new InvalidOperationException("The Window has no model context.");
        var windowLocale = window.Services.GetRequiredService<IRunicWindowLocale>();
        bool? inTurn = null;
        windowLocale.PropertyChanged += (_, _) => inTurn = context.IsExecuting;
        // A handler subscribed in a turn runs in a turn of that context, and a follower that yields does not change that.
        bool? handlerInTurn = null;
        await context.InvokeAsync(() => application.LocaleChanged += (_, _) => handlerInTurn = context.IsExecuting);
        using var follower = application.Follow(async (_, token) => await Task.Delay(1, token).ConfigureAwait(false));
        await Task.Run(() => application.SetLocaleAsync("de").AsTask());
        Require(inTurn == true, "The Window's PropertyChanged was not raised in its model context.");
        Require(handlerInTurn == true, "LocaleChanged did not run on the context it was subscribed on.");
    }

    [Test]
    public async Task ReentrantSwitchesFromFollowersAndWindowsThrowAndFromHandlersAreQueuedAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        Exception? fromFollower = null;
        var follower = application.Follow(async (locale, token) =>
        {
            await Task.Yield();
            if (locale != "de") return;
            try { await application.SetLocaleAsync("de-AT", token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (Exception error) { fromFollower = error; }
        });
        await application.SetLocaleAsync("de").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Require(fromFollower is InvalidOperationException && application.Locale == "de",
            $"A follower's switch did not throw InvalidOperationException: {fromFollower}");
        follower.Dispose();

        var windowLocale = window.Services.GetRequiredService<IRunicWindowLocale>();
        PropertyChangedEventHandler blocking = (_, _) => application.SetLocaleAsync("en").AsTask().GetAwaiter().GetResult();
        windowLocale.PropertyChanged += blocking;
        AggregateException? thrown = null;
        try { await application.SetLocaleAsync("fr").AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (AggregateException error) { thrown = error; }
        windowLocale.PropertyChanged -= blocking;
        Require(thrown?.InnerExceptions is [InvalidOperationException] && application.Locale == "fr",
            $"A Window handler's switch did not throw: {thrown}");

        // A LocaleChanged handler may switch: its event follows the current handlers.
        var events = new List<string?>();
        EventHandler<RunicApplicationLocaleChangedEventArgs> redirect = (_, args) =>
        {
            events.Add(args.Locale);
            if (args.Locale == "pt") _ = application.SetLocaleAsync("pt-BR").AsTask();
        };
        application.LocaleChanged += redirect;
        await application.SetLocaleAsync("pt").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => events.Count == 2);
        Require(events.SequenceEqual(["pt", "pt-BR"]) && application.Locale == "pt-BR" && Host(window).Content.Locale == "pt-BR",
            $"A handler's switch: {string.Join(", ", events)}");
    }

    [Test]
    public async Task OwnLocaleTellsTheWindowsLocaleFromTheApplicationsAsync()
    {
        await using var provider = Services();
        var application = provider.GetRequiredService<IRunicApplicationLocale>();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<TranslatedTextWindow>();
        var locale = window.Services.GetRequiredService<IRunicWindowLocale>();
        await application.SetLocaleAsync("de");
        Require(locale.Locale == "de" && locale.OwnLocale is null, "A following Window reported an own locale.");
        // Save what you read: the Window keeps following.
        var saved = locale.OwnLocale;
        locale.Locale = saved;
        await application.SetLocaleAsync("fr");
        Require(locale.Locale == "fr", "Setting the saved OwnLocale pinned the Window.");
        var changed = new List<string?>();
        locale.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        locale.Locale = "it";
        Require(locale.OwnLocale == "it" && changed.SequenceEqual([nameof(IRunicWindowLocale.Locale), nameof(IRunicWindowLocale.OwnLocale)]),
            $"Setting an own locale raised {string.Join(", ", changed)}");

        // WindowContentSession.Locale = null directly follows the application again, as the getter documents.
        Host(window).Content.Locale = "es";
        Require(locale.OwnLocale == "es", "A direct set was not the Window's own locale.");
        Host(window).Content.Locale = null;
        Require(locale.OwnLocale is null && locale.Locale == "fr" && Host(window).Content.Locale == "fr",
            $"A direct null did not follow the application: {locale.Locale}");
    }

    [Test]
    public void TheTestHostsApplicationLocaleThrowsAfterDispose()
    {
        var host = new RunicWindowTestHost<TranslatedTextViewModel>(new TranslatedTextViewModel(),
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        host.Dispose();
        var thrown = false;
        try { _ = host.ApplicationLocale; } catch (ObjectDisposedException) { thrown = true; }
        Require(thrown, "ApplicationLocale did not throw ObjectDisposedException after Dispose.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(5);
        }
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

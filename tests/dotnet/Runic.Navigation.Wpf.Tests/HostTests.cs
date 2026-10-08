using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// NavigationHost (W240-001 §8.3).
internal static class HostTests
{
    // M3: a new presenter and view per entry, also for the same type and the same borrowed instance.
    public static void PresenterPerEntry()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var host = new NavigationHost { Region = region, EmptyContent = "empty" };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            Require(Equals(host.Content, "empty"), $"An empty region showed {host.Content}.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("a"))));
            var first = (TextBox)ViewOf(host)!;
            first.Text = "typed";
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("b"))));
            var second = (TextBox)ViewOf(host)!;
            Require(!ReferenceEquals(first, second) && second.Text.Length == 0, "Two entries of one type shared a view.");

            var shared = new DocumentViewModel("shared");
            Pump(region.PushAsync(NavigationTarget.Borrow<object>(shared)));
            var third = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Borrow<object>(shared)));
            var fourth = ViewOf(host);
            Require(third is TextBox && fourth is TextBox && !ReferenceEquals(third, fourth), "One borrowed instance pushed twice shared a view.");
            Require(ReferenceEquals(fourth!.DataContext, shared), "The view's DataContext isn't the content.");

            Pump(region.ClearAsync());
            Require(Equals(host.Content, "empty"), $"A cleared region showed {host.Content}.");
        }
        finally { window.Close(); }
    }

    // The host is removed and re-added; Back gives a new view for the retained model, which initialized once.
    public static void Remount()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(CountingViewModel), typeof(DocumentViewModel)));
        try
        {
            var counting = new CountingViewModel();
            Pump(region.PushAsync(NavigationTarget.Own<object>(counting)));
            var before = ViewOf(host);
            Require(before is TextBox && ReferenceEquals(before.DataContext, counting), "The first view is missing.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("over"))));

            window.Content = null;
            Drain();
            Require(!host.IsLoaded, "The host is still loaded.");
            Pump(region.BackAsync());
            window.Content = host;
            Drain();
            Require(host.IsLoaded && host.PresentedEntry == region.CurrentEntry?.Id, "The remounted host did not read the region again.");
            var after = ViewOf(host);
            Require(after is TextBox && !ReferenceEquals(before, after) && ReferenceEquals(after.DataContext, counting),
                "Back after a remount did not give a new view for the retained model.");
            Require(counting.Initialized == 1, $"The retained model initialized {counting.Initialized} times.");
        }
        finally { window.Close(); }
    }

    // The host's locator, then the registered locator (MapView and the convention), then the implicit template;
    // 1082 once per type when none presents the content.
    public static void LocatorOrder()
    {
        var logs = new LogCapture();
        var provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(logs)
            .AddRunicWpfNavigation(options => options.MapView<MappedViewModel, MappedView>().UseViewNamingConvention())
            .BuildServiceProvider();
        try
        {
            var navigator = provider.GetRequiredService<RunicNavigator>();
            var region = navigator.CreateRegion<object>(new object());
            var hostLocator = new HostLocator();
            var host = new NavigationHost { Region = region, ViewLocator = hostLocator };
            var window = ShowWindow(host, window => AddTemplates(window, typeof(TemplatedViewModel)));
            try
            {
                Pump(region.PushAsync(NavigationTarget.Own<object>(new HostFirstViewModel())));
                Require(ViewOf(host) is HostView, "The host's locator was not asked first.");

                var mapped = new MappedViewModel();
                Pump(region.PushAsync(NavigationTarget.Own<object>(mapped)));
                Require(ViewOf(host) is MappedView { DataContext: var context, Logs: not null } && ReferenceEquals(context, mapped),
                    "MapView did not present its model with a container-built view.");

                hostLocator.TakeMapped = true;
                Pump(region.PushAsync(NavigationTarget.Own<object>(new MappedViewModel())));
                Require(ViewOf(host) is HostView, "The registered locator came before the host's.");
                hostLocator.TakeMapped = false;

                Pump(region.PushAsync(NavigationTarget.Own<object>(new ProbeViewModel())));
                Require(ViewOf(host) is ProbeView, "The naming convention did not find ProbeView.");

                Pump(region.PushAsync(NavigationTarget.Own<object>(new ViewModels.ShelfViewModel())));
                Require(ViewOf(host) is Views.ShelfPage, "The naming convention did not find Views.ShelfPage.");

                var templated = new TemplatedViewModel();
                Pump(region.PushAsync(NavigationTarget.Own<object>(templated)));
                Require(ViewOf(host) is TextBox { DataContext: var data } && ReferenceEquals(data, templated),
                    "The implicit DataTemplate was not used.");

                Pump(region.PushAsync(NavigationTarget.Own<object>(new UnpresentedViewModel())));
                Pump(region.PushAsync(NavigationTarget.Own<object>(new UnpresentedViewModel())));
                Require(logs.Count(1082) == 1 && logs.All(1082).Single().Category == "Runic.Navigation.Wpf",
                    $"Expected one 1082 for the unpresented type, got {logs.Count(1082)}.");
            }
            finally { window.Close(); }
        }
        finally { Pump(provider.DisposeAsync().AsTask(), "provider disposal"); }
    }

    public static void NamingConvention()
    {
        Require(Find(typeof(ProbeViewModel)) == typeof(ProbeView), "FooViewModel did not map to FooView.");
        Require(Find(typeof(SettingsViewModel)) == typeof(SettingsPage), "FooViewModel did not fall back to FooPage.");
        Require(Find(typeof(ViewModels.ShelfViewModel)) == typeof(Views.ShelfPage), "The ViewModels namespace did not map to Views.");
        Require(Find(typeof(TemplatedViewModel)) is null, "A ViewModel without a view matched.");
        Require(Find(typeof(Page)) is null, "A type without the ViewModel suffix matched.");

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test.")]
        static Type? Find(Type type) => NavigationViewLocator.FindByConvention(type);
    }

    // BrowseBack goes back when the region can and isn't transitioning.
    public static void BrowseBack()
    {
        using var fixture = new NavFixture();
        var home = new Page("home");
        var region = fixture.Region(NavigationTarget.Own<object>(home));
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page), typeof(GuardedPage)));
        try
        {
            Require(!NavigationCommands.BrowseBack.CanExecute(null, host), "BrowseBack can execute without history.");
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new GuardedPage("held", () => new ValueTask<bool>(release.Task)))));
            Require(NavigationCommands.BrowseBack.CanExecute(null, host), "BrowseBack can't execute with history.");
            NavigationCommands.BrowseBack.Execute(null, host);
            PumpUntil(() => region.IsTransitioning, "the Back transition");
            Require(host.IsTransitioning && !NavigationCommands.BrowseBack.CanExecute(null, host),
                "BrowseBack can execute while the region is transitioning.");
            release.SetResult(true);
            PumpUntil(() => ReferenceEquals(region.Current, home) && !region.IsTransitioning, "Back to home");
            Require(!host.IsTransitioning, "The host still reports a transition.");

            Pump(region.PushAsync(NavigationTarget.Own<object>(new Page("again"))));
            host.HandlesBrowseBack = false;
            Require(!NavigationCommands.BrowseBack.CanExecute(null, host), "BrowseBack was handled with HandlesBrowseBack off.");
        }
        finally { window.Close(); }
    }

    public static void UnloadedHostIsCollectable()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region(NavigationTarget.Own<object>(new DocumentViewModel("kept")));
        var window = ShowWindow(null, window => window.Resources.Add(new DataTemplateKey(typeof(DocumentViewModel)),
            new DataTemplate(typeof(DocumentViewModel)) { VisualTree = new FrameworkElementFactory(typeof(Border)) }));
        try
        {
            var host = Mount(window, region);
            for (var attempt = 0; attempt < 10 && host.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Drain();
            }
            Require(!host.IsAlive, "An unloaded host was not collected.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("after"))));
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Mount(Window window, INavigationRegion region)
    {
        var host = new NavigationHost { Region = region };
        window.Content = host;
        Drain();
        Require(host.IsLoaded && ViewOf(host) is Border, "The host did not present the region.");
        window.Content = null;
        Drain();
        return new WeakReference(host);
    }

    private sealed class HostLocator : INavigationViewLocator
    {
        public bool TakeMapped { get; set; }

        public FrameworkElement? ResolveView(INavigationEntry entry) =>
            entry.Content is HostFirstViewModel || (TakeMapped && entry.Content is MappedViewModel) ? new HostView() : null;
    }
}

internal sealed class HostFirstViewModel;

internal sealed class HostView : Border;

internal sealed class MappedViewModel;

internal sealed class MappedView(ILoggerFactory logs) : Border
{
    public ILoggerFactory Logs { get; } = logs;
}

internal sealed class TemplatedViewModel;

internal sealed class UnpresentedViewModel;

internal sealed class ProbeViewModel;

internal sealed class ProbeView : Border;

internal sealed class SettingsViewModel;

internal sealed class SettingsPage : Border;

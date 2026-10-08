using System.ComponentModel;
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

                var first = Pump(region.PushAsync(NavigationTarget.Own<object>(new UnpresentedViewModel())));
                var second = Pump(region.PushAsync(NavigationTarget.Own<object>(new UnpresentedViewModel())));
                Require(first is NavigationResult<object>.Committed && second is NavigationResult<object>.Committed,
                    $"The unpresented pushes gave {first} and {second}.");
                Require(logs.Count(1082) == 1 && logs.All(1082).Single().Category == "Runic.Navigation.Wpf",
                    $"Expected one 1082 for the unpresented type, got {logs.Count(1082)}. Presented: {ViewOf(host)?.GetType().FullName ?? "nothing"}; "
                    + $"template: {NavigationPresenters.FindTemplate(host, typeof(UnpresentedViewModel))?.DataType ?? "none"}; "
                    + $"events: {string.Join(", ", logs.Ids())}.");
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
        Require(Find(typeof(Core.Archive.LedgerViewModel)) is null, "A view in an unrelated namespace matched without a view assembly.");
        Require(FindIn(typeof(Core.Archive.LedgerViewModel), typeof(Screens.LedgerView).Assembly) == typeof(Screens.LedgerView),
            "A view assembly did not find the view by name.");

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test.")]
        static Type? Find(Type type) => NavigationViewLocator.FindByConvention(type);

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test.")]
        static Type? FindIn(Type type, System.Reflection.Assembly assembly) => NavigationViewLocator.FindByConvention(type, [assembly]);
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

    // W240-007: a double click on Back, then a Back from code. The second click is ignored because BrowseBack can't
    // execute from the moment the first Back is admitted, before any dispatcher work runs: that is the host's
    // CanExecute, not the engine's join. Only the programmatic BackAsync while the guard is pending exercises the join.
    public static void DoubleBrowseBackThenBackFromCode()
    {
        using var fixture = new NavFixture();
        var home = new Page("home");
        var region = fixture.Region(NavigationTarget.Own<object>(home));
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page), typeof(GuardedPage)));
        try
        {
            var middle = new Page("middle");
            Pump(region.PushAsync(NavigationTarget.Own<object>(middle)));
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guards = 0;
            Pump(region.PushAsync(NavigationTarget.Own<object>(new GuardedPage("held", () =>
            {
                guards++;
                return new ValueTask<bool>(release.Task);
            }))));

            NavigationCommands.BrowseBack.Execute(null, host);
            Require(region.IsTransitioning && !NavigationCommands.BrowseBack.CanExecute(null, host),
                "BrowseBack can still execute right after a Back was admitted.");
            NavigationCommands.BrowseBack.Execute(null, host);
            var joined = region.BackAsync().AsTask();
            PumpUntil(() => guards > 0, "the guard");
            release.SetResult(true);
            Require(Pump(joined) is NavigationResult<object>.Committed { Current.Content: var current } && ReferenceEquals(current, middle),
                "The programmatic Back did not join the pending Back.");
            PumpUntil(() => !region.IsTransitioning, "the end of the Back");
            Require(ReferenceEquals(region.Current, middle) && region.History.Count == 1 && guards == 1,
                $"A double Back popped to {region.Current} after {guards} guard call(s).");
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
            // WPF releases layout, input and command bookkeeping in idle operations; let them run between collections.
            for (var attempt = 0; attempt < 30 && host.IsAlive; attempt++)
            {
                Drain();
                Pump(Task.Delay(10), "a short delay");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Require(!host.IsAlive, "An unloaded host was not collected.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("after"))));
        }
        finally { window.Close(); }
    }

    // A view whose constructor takes the content's type gets the entry's content, never a container instance.
    public static void LocatorPassesTheContent()
    {
        var serviceNotifier = new ServiceNotifier();
        var provider = new ServiceCollection()
            .AddTransient<ContentViewModel>()
            .AddSingleton<INotifyPropertyChanged>(serviceNotifier)
            .AddRunicWpfNavigation(options => options.MapView<ContentViewModel, ContentView>().MapView<PlainContentViewModel, PlainContentView>()
                .MapView<NotifyingViewModel, NotifyingView>().MapView<InterfaceOnlyViewModel, InterfaceOnlyView>().MapView<OverloadedViewModel, OverloadedView>())
            .BuildServiceProvider();
        try
        {
            var region = provider.GetRequiredService<RunicNavigator>().CreateRegion<object>(new object());
            var host = new NavigationHost { Region = region };
            var window = ShowWindow(host);
            try
            {
                var content = new ContentViewModel();
                Pump(region.PushAsync(NavigationTarget.Own<object>(content)));
                Require(ViewOf(host) is ContentView { Model: var model, DataContext: var data } && ReferenceEquals(model, content) && ReferenceEquals(data, content),
                    "The view's constructor did not get the entry's content.");
                Pump(region.PushAsync<ContentViewModel>());
                Require(ViewOf(host) is ContentView { Model: var built } && ReferenceEquals(built, region.Current),
                    "A container-built entry's view got another instance.");
                Pump(region.PushAsync(NavigationTarget.Own<object>(new PlainContentViewModel())));
                Require(ViewOf(host) is PlainContentView { DataContext: PlainContentViewModel }, "A view without a content parameter was not created.");

                var notifying = new NotifyingViewModel();
                Pump(region.PushAsync(NavigationTarget.Own<object>(notifying)));
                Require(ViewOf(host) is NotifyingView { Notifier: var notifier, Model: var baseModel, Tag2: null, Disposable: null }
                        && ReferenceEquals(notifier, serviceNotifier) && ReferenceEquals(baseModel, notifying),
                    "The content went to an interface or object parameter, or not to its base-class parameter.");
                Pump(region.PushAsync(NavigationTarget.Own<object>(new InterfaceOnlyViewModel())));
                Require(ViewOf(host) is InterfaceOnlyView { Notifier: var only } && ReferenceEquals(only, serviceNotifier),
                    "An interface parameter got the content.");
                Require(!NavigationViewLocator.TakesContent(typeof(object), typeof(NotifyingViewModel))
                        && !NavigationViewLocator.TakesContent(typeof(IDisposable), typeof(NotifyingViewModel))
                        && NavigationViewLocator.TakesContent(typeof(NotifyingBase), typeof(NotifyingViewModel)),
                    "The content parameter rule changed.");

                var overloaded = new OverloadedViewModel();
                Pump(region.PushAsync(NavigationTarget.Own<object>(overloaded)));
                Require(ViewOf(host) is OverloadedView { Model: var chosen, Service: null } && ReferenceEquals(chosen, overloaded),
                    "The longest constructor was chosen although its service isn't registered.");
                var keyedFactory = NavigationViewLocator.CreateFactory(typeof(KeyedView), typeof(ContentViewModel));
                Require(Throws<InvalidOperationException>(() => keyedFactory(new UnkeyedProvider(serviceNotifier), [new ContentViewModel()])),
                    "A keyed parameter fell back to the unkeyed service on a provider without keyed services.");
            }
            finally { window.Close(); }
        }
        finally { Pump(provider.DisposeAsync().AsTask(), "provider disposal"); }
    }

    // A failed view leaves no stale view behind, and rebinding to another navigator's region (whose entry ids
    // repeat) presents that region.
    public static void FailedViewAndRebinding()
    {
        using var first = new NavFixture();
        using var second = new NavFixture();
        var one = first.Region(NavigationTarget.Own<object>(new DocumentViewModel("one")));
        var other = second.Region(NavigationTarget.Own<object>(new DocumentViewModel("other")));
        Require(one.CurrentEntry!.Id == other.CurrentEntry!.Id, "The fixtures' entry ids differ; the rebinding check needs equal ids.");
        var locator = new FailingLocator();
        var host = new NavigationHost { Region = one, ViewLocator = locator };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            var before = ViewOf(host);
            Require(before is TextBox { DataContext: DocumentViewModel { Name: "one" } }, "The first region is not presented.");
            host.Region = other;
            Drain();
            Require(ViewOf(host) is TextBox { DataContext: DocumentViewModel { Name: "other" } } after && !ReferenceEquals(before, after),
                "Rebinding to a region with the same entry id kept the old view.");

            locator.Fail = true;
            var pushed = Pump(other.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("broken"))));
            Require(pushed is NavigationResult<object>.Committed, $"The push gave {pushed}.");
            Require(host.Content is null && host.PresentedEntry is null, $"A failed view left {host.Content} presented.");
            locator.Fail = false;
            Pump(other.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("fixed"))));
            Require(ViewOf(host) is TextBox { DataContext: DocumentViewModel { Name: "fixed" } }, "The host did not recover after a failed view.");
        }
        finally { window.Close(); }
    }

    private sealed class FailingLocator : INavigationViewLocator
    {
        public bool Fail { get; set; }

        public FrameworkElement? ResolveView(INavigationEntry entry) => Fail ? throw new InvalidOperationException("No view.") : null;
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

internal sealed class ContentViewModel;

internal sealed class ContentView(ContentViewModel model, ILoggerFactory? logs = null) : Border
{
    public ContentViewModel Model { get; } = model;

    public ILoggerFactory? Logs { get; } = logs;
}

internal sealed class PlainContentViewModel;

internal sealed class PlainContentView : Border;

internal abstract class NotifyingBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

internal sealed class NotifyingViewModel : NotifyingBase, IDisposable
{
    public void Dispose() { }
}

internal sealed class ServiceNotifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// Interface and object parameters come from the container even when the content implements them; the base-class
// parameter gets the content.
internal sealed class NotifyingView(INotifyPropertyChanged notifier, NotifyingBase model, object? tag = null, IDisposable? disposable = null) : Border
{
    public INotifyPropertyChanged Notifier { get; } = notifier;
    public NotifyingBase Model { get; } = model;
    public object? Tag2 { get; } = tag;
    public IDisposable? Disposable { get; } = disposable;
}

internal sealed class InterfaceOnlyViewModel : NotifyingBase;

internal sealed class InterfaceOnlyView(INotifyPropertyChanged? notifier = null) : Border
{
    public INotifyPropertyChanged? Notifier { get; } = notifier;
}

internal interface IUnregisteredService;

internal sealed class OverloadedViewModel;

// The longer constructor needs a service nobody registered: the view is created with the shorter one.
internal sealed class OverloadedView : Border
{
    public OverloadedView(OverloadedViewModel model) => Model = model;

    public OverloadedView(OverloadedViewModel model, IUnregisteredService service)
    {
        Model = model;
        Service = service;
    }

    public OverloadedViewModel Model { get; }

    public IUnregisteredService? Service { get; }
}

internal sealed class KeyedView(ContentViewModel model, [FromKeyedServices("key")] INotifyPropertyChanged notifier) : Border
{
    public ContentViewModel Model { get; } = model;

    public INotifyPropertyChanged Notifier { get; } = notifier;
}

// A provider without keyed services.
internal sealed class UnkeyedProvider(object service) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
}

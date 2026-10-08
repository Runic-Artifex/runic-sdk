using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

internal static class PlainViewHostTests
{
    public static void RegisteredLocationAndBorrowedLifetime()
    {
        var dependency = new ViewDependency();
        var provider = new ServiceCollection().AddSingleton(dependency)
            .AddRunicWpfNavigation(options => options.MapView<PlainContent, PlainView>()).BuildServiceProvider();
        var model = new PlainContent();
        var legacy = new EntryOnlyLocator();
        var host = new ViewHost { Content = model, Services = provider, ViewLocator = legacy };
        var window = ShowWindow(host, scope => AddTemplates(scope, typeof(PlainContent)));
        try
        {
            var navigator = provider.GetRequiredService<RunicNavigator>();
            var first = Presenter(host).Content as PlainView ?? throw new InvalidOperationException("No registered plain view.");
            Require(first.Model == model && first.Dependency == dependency && first.DataContext == model,
                "Plain location did not pass content and services to the registered view.");
            Require(legacy.EntryCalls == 0 && navigator.UnretiredEntryCount == 0,
                "Plain location invented a navigation entry for an entry-specific locator.");
            window.Content = null;
            Drain();
            Require(host.Child is null && model.Disposed == 0, "Unloading kept the presentation or disposed borrowed content.");
            window.Content = host;
            Drain();
            Require(Presenter(host).Content is PlainView second && !ReferenceEquals(first, second),
                "Remount reused the old plain-content presentation.");

            host.Services = null;
            host.UpdateLayout();
            var presenter = Presenter(host);
            presenter.ApplyTemplate();
            presenter.UpdateLayout();
            Require(presenter.Content == model && VisualTreeHelper.GetChild(presenter, 0) is TextBox { DataContext: var data } && data == model,
                "An entry-only locator did not fall back to the implicit template.");
            host.Content = null;
            Require(host.Child is null && model.Disposed == 0, "Clearing plain content changed its ownership.");
        }
        finally { window.Close(); Pump(provider.DisposeAsync().AsTask()); }
    }

    public static void ExplicitLocationAndFailure()
    {
        var model = new PlainContent();
        var locator = new PlainLocator();
        var host = new ViewHost { Content = model, ViewLocator = locator };
        var window = ShowWindow(host);
        try
        {
            Require(Presenter(host).Content is TextBlock { Text: "explicit", DataContext: var data } && data == model,
                "The explicit plain-content locator was not used.");
            locator.Fail = true;
            Require(Throws<InvalidOperationException>(() => host.Content = new PlainContent()), "A view construction failure was hidden.");
            Require(host.Child is null, "A failed plain view left stale content visible.");
            locator.Fail = false;
            host.Content = model;
            Require(host.Child is ContentPresenter, "The host did not recover after a view failure.");
        }
        finally { window.Close(); }
    }

    private static ContentPresenter Presenter(ViewHost host) =>
        host.Child as ContentPresenter ?? throw new InvalidOperationException("No plain-content presentation.");

    private sealed class PlainContent : IDisposable
    {
        public int Disposed { get; private set; }
        public void Dispose() => Disposed++;
    }

    private sealed class ViewDependency;

    private sealed class PlainView : UserControl
    {
        public PlainView(PlainContent model, ViewDependency dependency) { Model = model; Dependency = dependency; }
        public PlainContent Model { get; }
        public ViewDependency Dependency { get; }
    }

    private sealed class EntryOnlyLocator : INavigationViewLocator
    {
        public int EntryCalls { get; private set; }
        public FrameworkElement? ResolveView(INavigationEntry entry) { EntryCalls++; throw new InvalidOperationException("No plain entry exists."); }
    }

    private sealed class PlainLocator : INavigationViewLocator
    {
        public bool Fail { get; set; }
        public FrameworkElement? ResolveView(INavigationEntry entry) => throw new InvalidOperationException("No plain entry exists.");
        public FrameworkElement? ResolveView(object content, IServiceProvider services) =>
            Fail ? throw new InvalidOperationException("View construction failed.") : new TextBlock { Text = "explicit" };
    }
}

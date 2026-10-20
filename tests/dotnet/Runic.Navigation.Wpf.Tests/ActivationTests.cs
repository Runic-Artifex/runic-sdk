using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// W315-020 point 1 (#350): with DispatcherModelContext, an arrival hook runs on the UI thread after the new view was
// laid out and rendered; a tab selected during another tab's activation switches at once and cancels that activation.
[TestExecutor<WpfDispatcherExecutor>]
internal sealed class ActivationTests
{
    [Test, DisplayName("activation after render"), NotInParallel("WpfSuite", Order = 52)]
    public void ActivationAfterRender()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region(NavigationTarget.Own<object>(new Page("home")));
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page), typeof(ActivatedViewModel)));
        try
        {
            string? observed = null;
            var page = new ActivatedViewModel("loading");
            page.Activate = (_, _) =>
            {
                // Read without forcing a layout pass: the view exists and was arranged before the hook's first statement.
                var view = host.Content is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0
                    ? VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement
                    : null;
                observed = $"{host.Dispatcher.CheckAccess()},{view is { IsArrangeValid: true, ActualHeight: > 0 }},{ReferenceEquals(view?.DataContext, page)}";
                return Task.CompletedTask;
            };
            var result = Pump(region.PushAsync(NavigationTarget.Own<object>(page)));
            Require(result is NavigationResult<object>.Committed { Arrival: NavigationArrivalState.Completed } && observed == "True,True,True",
                $"The arrival hook ran before the view was presented: {observed}; {result}.");
        }
        finally { window.Close(); }
    }

    [Test, DisplayName("tab switch during activation"), NotInParallel("WpfSuite", Order = 53)]
    public void TabSwitchDuringActivation()
    {
        using var fixture = new NavFixture();
        var customers = new ActivatedViewModel("customers");
        var reports = new ActivatedViewModel("reports");
        var cancelled = false;
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reports.Activate = async (_, token) =>
        {
            loading.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        };
        var tab = fixture.Navigator.CreateRegion(new object(), NavigationTarget.Borrow<object>(customers));
        var tabs = new TabControl { ItemsSource = new object[] { customers, reports } };
        NavigationSelector.SetRegion(tabs, tab);
        var window = ShowWindow(tabs, window => AddTemplates(window, typeof(ActivatedViewModel)));
        try
        {
            tabs.SelectedItem = reports;
            Pump(loading.Task, "the reports tab's activation");
            Require(ReferenceEquals(tab.Current, reports) && !tab.IsTransitioning && tab.IsBusy, "The reports tab did not commit and start loading.");
            tabs.SelectedItem = customers;
            PumpUntil(() => ReferenceEquals(tab.Current, customers) && cancelled && !tab.IsBusy, "the switch back to the customers tab",
                () => $"current: {tab.Current}, cancelled: {cancelled}, busy: {tab.IsBusy}");
            Pump(NavigationSelector.SelectionCompletion(tabs));
            Require(ReferenceEquals(tabs.SelectedItem, customers), "The selector did not keep the customers tab.");
        }
        finally { window.Close(); }
    }

    // The same with the navigator on a model context of its own thread, a supported setup: the selector reads the region
    // on the UI thread while commits run on the model's thread. Leaving a loading tab cancels its activation, and
    // reselecting the current tab while another selection is pending keeps the current tab without waiting for anything.
    [Test, DisplayName("tab switch during activation on another context"), NotInParallel("WpfSuite", Order = 54)]
    public void TabSwitchDuringActivationOnAnotherContext()
    {
        var model = new RunicModelContext();
        try
        {
            using var fixture = new NavFixture(wrap: _ => model);
            var customers = new ActivatedViewModel("customers");
            var reports = new ActivatedViewModel("reports");
            var activations = 0;
            var cancellations = 0;
            reports.Activate = async (_, token) =>
            {
                Interlocked.Increment(ref activations);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancellations); throw; }
            };
            var tab = fixture.Navigator.CreateRegion(new object(), NavigationTarget.Borrow<object>(customers));
            var tabs = new TabControl { ItemsSource = new object[] { customers, reports } };
            NavigationSelector.SetRegion(tabs, tab);
            var window = ShowWindow(tabs, window => AddTemplates(window, typeof(ActivatedViewModel)));
            try
            {
                tabs.SelectedItem = reports;
                PumpUntil(() => Volatile.Read(ref activations) == 1 && tab.IsBusy, "the reports tab's activation");
                tabs.SelectedItem = customers;
                PumpUntil(() => ReferenceEquals(tab.Current, customers) && Volatile.Read(ref cancellations) == 1 && !tab.IsBusy,
                    "the switch back to the customers tab", () => $"current: {tab.Current}, cancellations: {cancellations}, busy: {tab.IsBusy}");

                // Reselect the current tab at once: whichever way the pending selection ends, the customers tab stays and no
                // activation is left running.
                tabs.SelectedItem = reports;
                tabs.SelectedItem = customers;
                Pump(NavigationSelector.SelectionCompletion(tabs));
                PumpUntil(() => ReferenceEquals(tab.Current, customers) && ReferenceEquals(tabs.SelectedItem, customers) && !tab.IsBusy,
                    "the reselected customers tab", () => $"current: {tab.Current}, selected: {tabs.SelectedItem}, busy: {tab.IsBusy}");
                Require(Volatile.Read(ref activations) == Volatile.Read(ref cancellations), "A reports activation was left running.");
            }
            finally { window.Close(); }
        }
        finally { Pump(model.DisposeAsync().AsTask(), "the model context's disposal"); }
    }

    internal sealed class ActivatedViewModel(string name) : Page(name), INavigationArrived
    {
        public Func<NavigationEntryContext, CancellationToken, Task>? Activate { get; set; }

        public async ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, CancellationToken cancellationToken)
        {
            if (Activate is { } activate) await activate(entry, cancellationToken);
        }
    }
}

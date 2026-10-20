using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// A navigator on a DispatcherModelContext of the calling thread, with captured logs. The hosts log through the
// navigator's services, so they get the same LogCapture.
internal sealed class NavFixture : IDisposable
{
    public NavFixture(DispatcherPriority priority = DispatcherPriority.Normal, Func<DispatcherModelContext, IRunicModelContext>? wrap = null)
    {
        Context = new DispatcherModelContext(Dispatcher.CurrentDispatcher, priority, Logs.CreateLogger<DispatcherModelContext>());
        Services = new ServiceCollection().AddSingleton<ILoggerFactory>(Logs).BuildServiceProvider();
        Navigator = new RunicNavigator(new RunicNavigatorOptions
        {
            ModelContext = wrap?.Invoke(Context) ?? Context,
            LoggerFactory = Logs,
            Services = Services,
        });
    }

    public LogCapture Logs { get; } = new();
    public DispatcherModelContext Context { get; }
    public ServiceProvider Services { get; }
    public RunicNavigator Navigator { get; }

    public NavigationRegion<object> Region(INavigationTarget<object>? initial = null) => Navigator.CreateRegion(new object(), initial);

    public void Dispose()
    {
        if (!Context.Dispatcher.HasShutdownStarted) Pump(Navigator.DisposeAsync().AsTask(), "navigator disposal");
        else Require(Navigator.DisposeAsync().AsTask().Wait(WaitLimit), "Navigator disposal timed out.");
        Context.Dispose();
        Services.Dispose();
    }
}

internal class Page(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;
}

internal sealed class DocumentViewModel(string name) : Page(name);

internal sealed class CountingViewModel() : Page("counting"), INavigationInitialize
{
    public int Initialized { get; private set; }

    public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Initialized++;
        return default;
    }
}

// A page whose guard runs a test delegate.
internal sealed class GuardedPage(string name, Func<ValueTask<bool>> guard) : Page(name), INavigationDepartureGuard
{
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) => guard();
}

// A page whose guard runs a test delegate with the departure and the guard's token.
internal sealed class DeparturePage(string name, Func<NavigationDeparture, CancellationToken, ValueTask<bool>> guard)
    : Page(name), INavigationDepartureGuard
{
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        guard(departure, cancellationToken);
}

// A tab that holds a page region, as the Customers tab of a TabControl shell does.
internal sealed class PagesTab
{
    // A tab that refers to a region of another owner, such as the root.
    public PagesTab(string name, NavigationRegion<object> pages)
    {
        Name = name;
        Pages = pages;
    }

    // A tab model that creates its page region with itself as the owner.
    public PagesTab(string name, RunicNavigator navigator, INavigationTarget<object> initial)
    {
        Name = name;
        Pages = navigator.CreateRegion(this, initial);
    }

    public string Name { get; }
    public NavigationRegion<object> Pages { get; }

    public override string ToString() => Name;
}

// Changes a DispatcherObject in its hooks, which throws off the UI thread, and records where they ran.
internal sealed class AffinePage(DispatcherModelContext context) : Page("affine"), INavigationInitialize, INavigationDepartureGuard, IDisposable
{
    private readonly TextBlock _block = new();

    public bool InitializedOnUi { get; private set; }
    public bool GuardedOnUi { get; private set; }
    public int DisposeTurnDepth { get; private set; } = -1;
    public bool DisposedOnUi { get; private set; }
    public bool Disposed { get; private set; }
    public string Text => _block.Text;

    public async ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        _block.Text = "initialized";
        await Task.Yield();
        _block.Text += " and resumed";
        InitializedOnUi = context.Dispatcher.CheckAccess() && context.TurnDepth == 0;
    }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
    {
        _block.Text = "departing";
        GuardedOnUi = context.Dispatcher.CheckAccess() && context.TurnDepth == 0;
        return ValueTask.FromResult(true);
    }

    public void Dispose()
    {
        DisposedOnUi = context.Dispatcher.CheckAccess();
        if (DisposedOnUi)
        {
            DisposeTurnDepth = context.TurnDepth;
            _block.Text = "disposed";
        }
        Disposed = true;
    }
}

// Records the thread that disposed it.
internal sealed class OwnedProbe() : Page("owned"), IDisposable
{
    public Thread? DisposedOn { get; private set; }

    public void Dispose() => DisposedOn = Thread.CurrentThread;
}

// A dialog: captures its entry context, and its guard asks Allow.
internal sealed class DialogViewModel(string name) : Page(name), INavigationInitialize, INavigationDepartureGuard
{
    public NavigationEntryContext? Entry { get; private set; }
    public Func<bool> Allow { get; set; } = () => true;
    public int Guards { get; private set; }

    public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Entry = entry;
        return default;
    }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
    {
        Guards++;
        return ValueTask.FromResult(Allow());
    }
}

// A dialog without hooks.
internal sealed class PlainDialog() : Page("plain");

// An editor whose guard asks through a dialog region.
internal sealed class EditorViewModel() : Page("editor"), INavigationDepartureGuard
{
    public LeaveConfirmation? Leave { get; set; }
    public bool Dirty { get; set; } = true;
    public bool Discarded { get; set; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        Leave!.CanDepartAsync(departure, cancellationToken);
}

// The owner window with one dialog host for one dialog region.
internal sealed class DialogScene : IDisposable
{
    public DialogScene(Action<NavigationDialogHost>? configure = null, NavFixture? fixture = null, NavigationRegionOptions? options = null)
    {
        Fixture = fixture ?? new NavFixture();
        Region = Fixture.Navigator.CreateRegion<object>(new object(), null, options);
        Host = new NavigationDialogHost { Region = Region };
        configure?.Invoke(Host);
        Owner = ShowWindow(new Grid { Children = { Host } },
            window => AddTemplates(window, typeof(DialogViewModel), typeof(PlainDialog), typeof(DeparturePage)));
    }

    public NavFixture Fixture { get; }
    public NavigationRegion<object> Region { get; }
    public NavigationDialogHost Host { get; }
    public Window Owner { get; }
    public IReadOnlyList<Window> Windows => Host.DialogWindows;

    public Window WaitForWindows(int count)
    {
        PumpUntil(() => Windows.Count == count && Windows.All(window => window.IsVisible), $"{count} dialog window(s)");
        return Windows[^1];
    }

    public void Dispose()
    {
        if (!Owner.Dispatcher.HasShutdownStarted)
        {
            if (IsOpen(Owner)) Owner.Close();
            Drain();
        }
        Fixture.Dispose();
    }
}

// Forwards a DispatcherModelContext and signals when a turn is requested off the UI thread.
internal sealed class SignallingContext(DispatcherModelContext inner) : IRunicModelContext, IRunicModelHookScheduler, IRunicModelContextLifetime
{
    public ManualResetEventSlim OffThreadTurn { get; } = new(false);

    public CancellationToken Closed => inner.Closed;

    public bool IsExecuting => inner.IsExecuting;

    public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException
    {
        add => inner.UnhandledTurnException += value;
        remove => inner.UnhandledTurnException -= value;
    }

    public bool TryPost(Action turn) => inner.TryPost(turn);

    public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
    {
        var armed = ArmedOffThread();
        var result = inner.InvokeAsync(turn, cancellationToken);
        if (armed) OffThreadTurn.Set();
        return result;
    }

    public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
    {
        var armed = ArmedOffThread();
        var result = inner.InvokeAsync(turn, cancellationToken);
        if (armed) OffThreadTurn.Set();
        return result;
    }

    public Task<T> RunHookAsync<T>(Func<Task<T>> hook, CancellationToken cancellationToken) => ((IRunicModelHookScheduler)inner).RunHookAsync(hook, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    // Only a turn requested after Arm signals (#340). Reading the switch after queuing the turn let the push's own commit
    // request, from a pool thread descheduled between queuing and signalling, report the clear's turn as queued.
    private bool ArmedOffThread() => Volatile.Read(ref _armed) && !inner.Dispatcher.CheckAccess();

    private bool _armed;

    public void Arm() => Volatile.Write(ref _armed, true);
}

// An InitializeAsync that awaits a gate, recording where it resumed.
internal sealed class GatedInitializePage(Task gate) : Page("gated"), INavigationInitialize
{
    public bool Started { get; private set; }

    public Thread? ResumedOn { get; private set; }

    public async ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Started = true;
        await gate;
        ResumedOn = Thread.CurrentThread;
    }
}

// A dialog whose template has an IsDefault button bound to AcceptCommand and an IsCancel button bound to CancelCommand.
internal sealed class ButtonsDialog() : Page("buttons"), INavigationInitialize
{
    public NavigationEntryContext? Entry { get; private set; }
    // Read once by the template binding.
    public ICommand AcceptCommand => new RelayCommand(() => _ = Entry!.CompleteAsync(true).AsTask());
    public ICommand? CancelCommand { get; init; }

    public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Entry = entry;
        return default;
    }

    public static DataTemplate Template()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.AppendChild(Create("Accept", Button.IsDefaultProperty, nameof(AcceptCommand)));
        panel.AppendChild(Create("Cancel", Button.IsCancelProperty, nameof(CancelCommand)));
        var template = new DataTemplate(typeof(ButtonsDialog)) { VisualTree = panel };
        template.Seal();
        return template;

        static FrameworkElementFactory Create(string content, DependencyProperty role, string command)
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(ContentControl.ContentProperty, content);
            button.SetValue(role, true);
            button.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding(command));
            return button;
        }
    }

    public static Button Find(DependencyObject root, string content) =>
        TryFind(root, content) ?? throw new InvalidOperationException($"No {content} button.");

    public static Button? TryFind(DependencyObject node, string content)
    {
        if (node is Button { Content: string text } button && text == content) return button;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); index++)
            if (TryFind(System.Windows.Media.VisualTreeHelper.GetChild(node, index), content) is { } found) return found;
        return null;
    }

    // What a failed key press needs to be diagnosed: the window, its presenter and every button WPF could target.
    public static string Describe(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var lines = new List<string>
        {
            $"window: visible={window.IsVisible} loaded={window.IsLoaded} active={window.IsActive} source={source is not null} " +
                $"content={window.Content?.GetType().Name ?? "null"} dataContext={window.DataContext?.GetType().Name ?? "null"} " +
                $"focused={Keyboard.FocusedElement?.GetType().Name ?? "null"}",
        };
        if (window.Content is ContentPresenter presenter)
            lines.Add($"presenter: template={presenter.ContentTemplate is not null} children={System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter)}");
        foreach (var button in Buttons(window))
            lines.Add($"button '{button.Content}': default={button.IsDefault} defaulted={button.IsDefaulted} cancel={button.IsCancel} " +
                $"visible={button.IsVisible} enabled={button.IsEnabled} loaded={button.IsLoaded} command={button.Command?.GetType().Name ?? "null"} " +
                $"canExecute={button.Command?.CanExecute(button.CommandParameter)} sameSource={ReferenceEquals(PresentationSource.FromVisual(button), source)}");
        if (lines.Count == (window.Content is ContentPresenter ? 2 : 1)) lines.Add("no buttons in the visual tree");
        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        if (node is Button button) yield return button;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var nested in Buttons(System.Windows.Media.VisualTreeHelper.GetChild(node, index))) yield return nested;
    }
}

internal sealed class RelayCommand(Action execute) : ICommand
{
    public int Runs { get; private set; }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        Runs++;
        execute();
    }
}

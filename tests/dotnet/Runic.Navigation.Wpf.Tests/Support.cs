using System.Windows;
using System.Windows.Controls;
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
    public DialogScene(Action<NavigationDialogHost>? configure = null, NavFixture? fixture = null)
    {
        Fixture = fixture ?? new NavFixture();
        Region = Fixture.Region();
        Host = new NavigationDialogHost { Region = Region };
        configure?.Invoke(Host);
        Owner = ShowWindow(new Grid { Children = { Host } }, window => AddTemplates(window, typeof(DialogViewModel), typeof(PlainDialog)));
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

    public event Action<Exception>? UnhandledTurnException
    {
        add => inner.UnhandledTurnException += value;
        remove => inner.UnhandledTurnException -= value;
    }

    public bool TryPost(Action turn) => inner.TryPost(turn);

    public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
    {
        var result = inner.InvokeAsync(turn, cancellationToken);
        Signal();
        return result;
    }

    public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
    {
        var result = inner.InvokeAsync(turn, cancellationToken);
        Signal();
        return result;
    }

    public Task<T> RunHookAsync<T>(Func<Task<T>> hook, CancellationToken cancellationToken) => inner.RunHookAsync(hook, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Signal()
    {
        if (Volatile.Read(ref _armed) && !inner.Dispatcher.CheckAccess()) OffThreadTurn.Set();
    }

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

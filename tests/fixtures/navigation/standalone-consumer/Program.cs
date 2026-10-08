using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using StandaloneConsumer;

// A consumer of the packed Runic.Navigation alone: no Runic.Application, Bridge
// generator, Node or UI host. The same program runs with JIT and as a NativeAOT
// publish (package-smoke.mjs). Scenarios use the API of W240-003: target-form
// push, PushForResult, Back, and DisposeAsync that retires every entry.
var log = new WarningLog();
var context = new RunicModelContext(log.CreateLogger<RunicModelContext>());
var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context, LoggerFactory = log });
var home = new HomePage();
var main = navigator.CreateRegion<IPage>(home, NavigationTarget.Borrow<IPage>(home));
Require(main.Current == home && !main.CanGoBack, "The initial target is not current.");

// Push an owned page that a factory creates when the transition prepares.
DetailPage? detail = null;
var pushed = await main.PushAsync(NavigationTarget.Create<IPage>(_ => detail = new DetailPage("detail")));
Require(pushed is NavigationResult<IPage>.Committed { Current.Content.Title: "detail" } && main.CanGoBack,
    $"The push did not commit: {pushed}");
Require(RunicModelContextRegistry.Shared.TryGet(detail!, out var bound) && bound == context,
    "The owned page was not bound to the navigator's model context.");

// Push a dialog for a result; it completes with true, which goes back from it.
var dialog = new ConfirmPage();
var request = main.PushForResult<bool>(NavigationTarget.Own<IPage>(dialog));
Require(await request.Transition is NavigationResult<IPage>.Committed && main.Current == dialog,
    "The result push did not commit.");
var completed = await dialog.Entry!.CompleteAsync(true);
Require(completed is NavigationResult<object>.Committed, $"CompleteAsync did not go back: {completed}");
Require(await request.Completion is NavigationCompletion<bool>.Completed { Value: true },
    "The result request did not complete with true.");
Require(main.Current == detail && dialog.Disposed, "Completing did not retire the owned dialog.");

// Back retires and disposes the owned page; the borrowed home resumes.
var back = await main.BackAsync();
Require(back is NavigationResult<IPage>.Committed && main.Current == home && detail!.Disposed && home.Resumed == 1,
    $"Back did not retire the owned page: {back}");
Require(!RunicModelContextRegistry.Shared.TryGet(detail!, out _), "Back did not release the page's lease.");

// Disposal retires the entry that is still current and leaves nothing unretired.
var last = new DetailPage("last");
Require(await main.PushAsync(NavigationTarget.Own<IPage>(last)) is NavigationResult<IPage>.Committed, "The last push did not commit.");
await navigator.DisposeAsync();
Require(last.Disposed && !home.Disposed, "Disposal did not retire the owned page, or disposed borrowed content.");
Require(navigator.UnretiredEntryCount == 0, $"{navigator.UnretiredEntryCount} entries were not retired.");
await context.DisposeAsync();

Require(typeof(RunicNavigator).Assembly.GetName().Name == "Runic.Navigation", "The navigator is not from Runic.Navigation.");
Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name?.StartsWith("Runic.Application", StringComparison.Ordinal) == true),
    "A Runic.Application assembly was loaded.");
Require(log.Entries.Count == 0, $"Warnings or errors were logged: {string.Join("; ", log.Entries)}");
Console.WriteLine("STANDALONE_NAVIGATION_OK");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

namespace StandaloneConsumer
{
    public interface IPage : INotifyPropertyChanged
    {
        string Title { get; }
    }

    // Borrowed: the navigator never disposes it.
    public sealed class HomePage : IPage, INavigationResume, IDisposable
    {
        public string Title => "home";
        public int Resumed { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask ResumeAsync(NavigationResume resume, CancellationToken cancellationToken)
        {
            Resumed++;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    // Owned: retiring its entry disposes it.
    public sealed class DetailPage(string title) : IPage, IDisposable
    {
        public string Title { get; } = title;
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    // Captures its entry to answer the result request.
    public sealed class ConfirmPage : IPage, INavigationInitialize, IDisposable
    {
        public string Title => "confirm";
        public NavigationEntryContext? Entry { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
        {
            Entry = entry;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    internal sealed class WarningLog : ILoggerFactory
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(WarningLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                lock (owner.Entries) owner.Entries.Add($"{category} {eventId.Id} {formatter(state, exception)} {exception}");
            }
        }
    }
}

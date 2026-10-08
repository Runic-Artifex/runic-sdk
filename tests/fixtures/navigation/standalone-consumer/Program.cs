using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using StandaloneConsumer;

// A consumer of the packed Runic.Navigation alone: no Runic.Application, Bridge
// generator, Node or UI host. The same program runs with JIT and as a NativeAOT
// publish (package-smoke.mjs). Scenarios use the API of W240-003 (target-form
// push, PushForResult, Back, and DisposeAsync that retires every entry) and of
// W240-004 (container-resolved targets, entry scopes and LeaveConfirmation).
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

// Container-resolved targets (W240-004): ActivatorUtilities builds each page in its own
// entry scope, a LeaveConfirmation asks in a dialog region and discards on commit, and
// a container-built dialog answers a PushForResult. NativeAOT runs this too.
var services = new ServiceCollection();
services.AddSingleton<Clock>();
services.AddScoped<Draft>();
await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
{
    var scopedContext = new RunicModelContext(log.CreateLogger<RunicModelContext>());
    var scoped = new RunicNavigator(new RunicNavigatorOptions
    {
        ModelContext = scopedContext,
        LoggerFactory = log,
        Services = provider,
        CreateEntryScopes = true,
    });
    var shell = new object();
    var pages = scoped.CreateRegion<IPage>(shell, NavigationTarget.Borrow<IPage>(home));
    var dialogs = scoped.CreateRegion<IPage>(shell);

    var opened = await pages.PushAsync<EditorPage>();
    Require(opened is NavigationResult<IPage>.Committed && pages.Current is EditorPage { Clock: var clock } && clock == provider.GetRequiredService<Clock>(),
        $"PushAsync<EditorPage>() did not build the page from the container: {opened}");
    var editor = (EditorPage)pages.Current!;
    Require(ReferenceEquals(pages.CurrentEntry!.Services.GetRequiredService<Draft>(), editor.Draft), "The editor did not get its own entry scope.");
    editor.Leave = LeaveConfirmation.InDialog(dialogs, NavigationTarget.Create<ConfirmPage>, () => editor.Draft.Dirty, editor.Draft.Discard);
    editor.Draft.Dirty = true;

    // A covering push with input retains the editor, so nothing asks.
    Require(await pages.PushAsync<InputPage, string>("input") is NavigationResult<IPage>.Committed && pages.Current is InputPage { Title: "input" },
        "PushAsync<InputPage, string>() did not initialize with the input.");
    Require(await pages.BackAsync() is NavigationResult<IPage>.Committed && pages.Current == editor, "Back to the editor did not commit.");

    // Dismissing the question keeps the editor and its draft.
    var vetoed = pages.BackAsync().AsTask();
    var question = await ShownAsync(dialogs);
    Require(await question.Entry!.DismissAsync() is NavigationResult<object>.Committed, "Dismissing the question did not leave the dialog.");
    Require(await vetoed is NavigationResult<IPage>.Rejected { Reason: NavigationRejection.Guard } && pages.Current == editor
        && editor.Draft is { Dirty: true, Discarded: 0 }, "A dismissed question did not keep the editor.");

    // Confirming leaves, discards once in the commit turn, and retires the editor's scope.
    var leaving = pages.BackAsync().AsTask();
    question = await ShownAsync(dialogs);
    Require(await question.Entry!.CompleteAsync(true) is NavigationResult<object>.Committed, "Confirming did not leave the dialog.");
    Require(await leaving is NavigationResult<IPage>.Committed && pages.Current == home && editor.Draft.Discarded == 1,
        "A confirmed question did not leave and discard.");
    await scoped.WhenIdleAsync();
    Require(editor.Disposed && editor.Draft.Disposed && question.Disposed, "Retirement did not dispose the pages and their scopes.");

    // A container-built dialog answers a result request.
    var answer = pages.PushForResult<ConfirmPage, bool>();
    Require(await answer.Transition is NavigationResult<IPage>.Committed && pages.Current is ConfirmPage, "PushForResult<ConfirmPage, bool>() did not push.");
    await ((ConfirmPage)pages.Current!).Entry!.CompleteAsync(true);
    Require(await answer.Completion is NavigationCompletion<bool>.Completed { Value: true }, "The container-built dialog did not complete.");

    await scoped.DisposeAsync();
    Require(scoped.UnretiredEntryCount == 0 && !home.Disposed, "Disposing the scoped navigator left entries or disposed borrowed content.");
    await scopedContext.DisposeAsync();
}

Require(typeof(RunicNavigator).Assembly.GetName().Name == "Runic.Navigation", "The navigator is not from Runic.Navigation.");
Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name?.StartsWith("Runic.Application", StringComparison.Ordinal) == true),
    "A Runic.Application assembly was loaded.");
Require(log.Entries.Count == 0, $"Warnings or errors were logged: {string.Join("; ", log.Entries)}");
Console.WriteLine("STANDALONE_NAVIGATION_OK");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// The question a LeaveConfirmation pushed, once its push committed.
static async Task<ConfirmPage> ShownAsync(NavigationRegion<IPage> dialogs)
{
    for (var deadline = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < deadline; await Task.Delay(5))
        if (dialogs.Current is ConfirmPage { Entry: not null } page && !dialogs.IsTransitioning) return page;
    throw new TimeoutException("The leave confirmation did not show its dialog.");
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

    public sealed class Clock;

    // Scoped to the entry that resolved it; its scope is disposed after the entry's content.
    public sealed class Draft : IDisposable
    {
        public bool Dirty { get; set; }
        public int Discarded { get; private set; }
        public bool Disposed { get; private set; }

        public void Discard()
        {
            Dirty = false;
            Discarded++;
        }

        public void Dispose() => Disposed = true;
    }

    // Built by ActivatorUtilities from the entry scope; its guard forwards to a LeaveConfirmation.
    public sealed class EditorPage(Clock clock, Draft draft) : IPage, INavigationDepartureGuard, IDisposable
    {
        public string Title => "editor";
        public Clock Clock { get; } = clock;
        public Draft Draft { get; } = draft;
        public LeaveConfirmation? Leave { get; set; }
        public bool Disposed { get; private set; }

        public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
            Leave?.CanDepartAsync(departure, cancellationToken) ?? ValueTask.FromResult(true);

        public void Dispose() => Disposed = true;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    // Built by ActivatorUtilities and initialized with its input.
    public sealed class InputPage(Clock clock) : IPage, INavigationInitialize<string>
    {
        public Clock Clock { get; } = clock;
        public string Title { get; private set; } = "";

        public ValueTask InitializeAsync(NavigationEntryContext entry, string input, CancellationToken cancellationToken)
        {
            Title = input;
            return ValueTask.CompletedTask;
        }

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

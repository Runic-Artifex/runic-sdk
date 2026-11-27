using System.Collections.Immutable;
using System.Windows.Input;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

// The test application of the Runic host protocol corpus (specs/desktop/host-conformance/README.md, Test application),
// which HostConformanceAdapter runs as the guest for the conformance driver.
sealed class ConformanceMainWindow : RunicWindow<OrderedCollectionModel>;

sealed class ConformanceGuardedWindow : RunicWindow<OrderedCollectionModel>
{
    private static readonly object s_gate = new();
    // What the guard answers (the corpus's answerGuard action), and the decision it waits for after holdGuard; the
    // conformance cases run one at a time.
    private static bool s_answer = true;
    private static TaskCompletionSource<bool>? s_held;

    public ConformanceGuardedWindow() => GuardClose = true;

    public static void Reset() => Decide(true);

    public static void Hold()
    {
        lock (s_gate) s_held ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Decide(bool allow)
    {
        TaskCompletionSource<bool>? held;
        lock (s_gate)
        {
            s_answer = allow;
            (held, s_held) = (s_held, null);
        }
        held?.TrySetResult(allow);
    }

    protected override ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        lock (s_gate) return s_held is { } held ? new ValueTask<bool>(held.Task) : ValueTask.FromResult(s_answer);
    }
}

// The corpus's test application's menus (specs/desktop/host-conformance/README.md, Test application): its menu bar in
// English or German, the application's commands of its items, which record each command that runs, and its context menu.
internal sealed class ConformanceMenus
{
    private readonly List<string> _invoked = [];

    public ConformanceMenus()
    {
        foreach (var id in (string[])["file.open", "file.save", "file.recent", "view.grid"])
            _commands[id] = new ConformanceCommand(() => { lock (_invoked) _invoked.Add(id); });
    }

    private readonly Dictionary<string, ConformanceCommand> _commands = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ICommand> Commands => _commands.ToDictionary(static pair => pair.Key, static pair => (ICommand)pair.Value, StringComparer.Ordinal);

    public ConformanceCommand Command(string id) => _commands[id];

    // The commands that ran, in order.
    public IReadOnlyList<string> Invoked
    {
        get { lock (_invoked) return [.. _invoked]; }
    }

    public static IReadOnlyList<DesktopMenu> Bar(bool german) =>
    [
        new DesktopMenu(german ? "Datei" : "File",
        [
            new DesktopMenuItem(german ? "Öffnen" : "Open", "file.open") { Shortcut = new DesktopShortcut("O") },
            new DesktopMenuItem(german ? "Sichern" : "Save", "file.save") { Shortcut = new DesktopShortcut("S") },
            new DesktopMenuSeparator(),
            new DesktopMenu(german ? "Zuletzt geöffnet" : "Recent", [new DesktopMenuItem("notes.txt", "file.recent")]),
        ]) { Role = DesktopMenuRole.File },
        new DesktopMenu(german ? "Ansicht" : "View", [new DesktopMenuItem(german ? "Raster" : "Grid", "view.grid") { IsChecked = false }]),
    ];

    public static ImmutableArray<DesktopMenuEntry> ContextMenu => HostedContextMenu.Entries;
}

// A command of the test application: it records each run, can be disabled (setCommandEnabled), and its next run can be
// held (holdCommand) until releaseCommand: blocking in Execute, or running on after Execute returned, unable to run.
internal sealed class ConformanceCommand(Action execute) : ICommand
{
    private readonly object _gate = new();
    private volatile bool _enabled = true;
    private volatile bool _running;
    private bool? _holdBlocking;
    private TaskCompletionSource? _release;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _enabled && !_running;

    public void Execute(object? parameter)
    {
        bool? blocking;
        Task? released;
        lock (_gate)
        {
            (blocking, _holdBlocking) = (_holdBlocking, null);
            released = blocking is null ? null : _release?.Task;
        }
        if (released is null)
        {
            execute();
            return;
        }
        if (blocking == true)
        {
            // A long synchronous command: recorded when its Execute returns. A runner that never releases it fails its
            // step after its timeout; the wait ends then too.
            _ = released.Wait(TimeSpan.FromSeconds(60));
            execute();
            return;
        }
        execute();
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        _ = released.ContinueWith(_ =>
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Hold(bool blocking)
    {
        lock (_gate)
        {
            _holdBlocking = blocking;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Release()
    {
        TaskCompletionSource? release;
        lock (_gate) (release, _release) = (_release, null);
        release?.TrySetResult();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

// The test application's dialogs (README, Test application).
internal static class ConformanceDialogs
{
    public static OpenFileOptions Open => new() { Title = "Open", Filters = [new FileDialogFilter("Text", ["txt"])] };

    public static SaveFileOptions Save => new("notes.txt") { Title = "Save" };

    public static OpenDirectoryOptions Folder => new() { Title = "Choose a folder" };

    public static DesktopMessageBox MessageBox => new("Save changes?")
    {
        Title = "Conformance",
        Kind = DesktopMessageBoxKind.Warning,
        Buttons =
        [
            new DesktopMessageBoxButton("save", "Save") { Role = DesktopMessageBoxButtonRole.Default },
            new DesktopMessageBoxButton("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive },
            new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel },
        ],
    };
}

sealed class ConformanceDropWindow : RunicWindow<ConformanceDropModel>;

internal sealed class ConformanceDropModel : IRunicFileDropTarget, System.ComponentModel.INotifyPropertyChanged
{
    private static readonly List<string> s_dropped = [];

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    // The names of the files dropped since the scenario started, in order.
    public static IReadOnlyList<string> Dropped
    {
        get { lock (s_dropped) return [.. s_dropped]; }
    }

    public static void Reset()
    {
        lock (s_dropped) s_dropped.Clear();
    }

    public ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken)
    {
        lock (s_dropped) s_dropped.AddRange(drop.Files.Select(static file => Path.GetFileName(file.Path)));
        return ValueTask.CompletedTask;
    }
}

internal sealed class ConformanceDropBridge(IBridgeTransport transport, ConformanceDropModel model)
    : ViewModelBridge(transport, model, typeof(ConformanceDropModel), "conformance-drop", [], [], [], []);

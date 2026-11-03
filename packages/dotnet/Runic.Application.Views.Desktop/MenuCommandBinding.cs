using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using Runic.Platform;

namespace Runic.Application.Views.Menus;

// Binds the menu bar's command ids to ICommands: those of a Window's root ViewModel (IRunicMenuCommandSource), or the
// application's (RunicDesktopHost.UseApplicationMenu). AppKit asks CanExecute on its main thread while it shows a menu or
// matches a key, so the availability of each command is cached per command id and parameter and updated from
// CanExecuteChanged. Resolving and running a command happens in the model turn of the ViewModel, when there is one.
// An item's CommandParameter is the command's parameter. The check marks of check and radio items come from the root
// ViewModel's IRunicMenuCheckedSource, cached the same way and read again in the model turn whenever the ViewModel raises
// PropertyChanged and whenever the bar changes. A Window's command that the page's in-page bar invoked runs in a command
// invocation (RunicInteractionInvocation) with that page's client and connection, so its interactions and view requests
// reach only that page's presentation; one that the native bar or a host shortcut ran has no page, and runs in the
// window scope, which reaches the Window's one page presentation that handles them, as during a close decision. Shared
// with Runic.Application.Views.CsWebUi, like
// MenuBarPageRoutes; on Runic Desktop it is also the window's IDesktopMenuCommandTarget (MenuCommandBinding.Desktop.cs).
internal sealed partial class MenuCommandBinding : IDisposable
{
    private readonly IRunicModelContext? _context;
    // The Window whose root ViewModel's commands these are; null for the application's commands.
    private readonly WindowContentSession? _window;
    private readonly Func<string, ICommand?> _resolve;
    private readonly ILogger _logger;
    private readonly IMenuBarSource _host;
    private readonly Func<string, string?, bool?>? _isChecked;
    private readonly INotifyPropertyChanged? _notifier;
    private readonly object _gate = new();
    private Dictionary<MenuCommandKey, Entry> _entries = [];
    // The check marks the ViewModel answered, by command id and parameter; a key without an answer keeps the declaration.
    private Dictionary<MenuCommandKey, bool> _checked = [];
    private HashSet<MenuCommandKey> _toggles = [];
    private int _checkRefresh;
    private object? _owner;
    private bool _disposed;

    private MenuCommandBinding(IMenuBarSource host, IRunicModelContext? context, Func<string, ICommand?> resolve, ILogger logger,
        Func<string, string?, bool?>? isChecked = null, INotifyPropertyChanged? notifier = null, WindowContentSession? window = null)
    {
        _host = host;
        _context = context;
        _window = window;
        _resolve = resolve;
        _logger = logger;
        _isChecked = isChecked;
        _notifier = isChecked is null ? null : notifier;
    }

    // Binds application commands, which have no model context: the menu bar's command ids resolve before this
    // returns, and again whenever the bar changes.
    internal static MenuCommandBinding Bind(IMenuBarSource host, Func<string, ICommand?> resolve, ILogger logger)
    {
        var binding = new MenuCommandBinding(host, context: null, resolve, logger);
        host.MenuBarChanged += binding.OnMenuBarChanged;
        _ = binding.RebindAsync();
        return binding;
    }

    // Binds every command id of the host's menu bar now, and again whenever the bar changes. isChecked answers the check
    // marks (IRunicMenuCheckedSource), read again when the notifier raises PropertyChanged.
    internal static async ValueTask<MenuCommandBinding> BindAsync(IMenuBarSource host, IRunicModelContext? context,
        Func<string, ICommand?> resolve, ILogger logger, Func<string, string?, bool?>? isChecked = null, INotifyPropertyChanged? notifier = null,
        WindowContentSession? window = null)
    {
        var binding = new MenuCommandBinding(host, context, resolve, logger, isChecked, notifier, window);
        host.MenuBarChanged += binding.OnMenuBarChanged;
        if (binding._notifier is { } source) source.PropertyChanged += binding.OnPropertyChanged;
        await binding.RebindAsync().ConfigureAwait(false);
        return binding;
    }

    // Binds a Window's root ViewModel: its commands (IRunicMenuCommandSource) and check marks (IRunicMenuCheckedSource).
    // window is the Window's session, whose interaction scopes its commands run in.
    internal static async ValueTask<MenuCommandBinding> BindViewModelAsync(IMenuBarSource host, IRunicModelContext? context, object viewModel,
        ILogger logger, WindowContentSession? window = null)
    {
        Func<string, ICommand?> resolve = viewModel is IRunicMenuCommandSource commands ? commands.GetMenuCommand : static _ => null;
        Func<string, string?, bool?>? isChecked = viewModel is IRunicMenuCheckedSource checks ? checks.IsMenuItemChecked : null;
        var binding = await BindAsync(host, context, resolve, logger, isChecked, viewModel as INotifyPropertyChanged, window).ConfigureAwait(false);
        if (isChecked is not null)
        {
            // RunicMenu.Refresh(viewModel) and RefreshAll reach the binding until it is disposed.
            binding._owner = viewModel;
            RunicMenu.Register(viewModel, binding.RefreshChecked);
        }
        return binding;
    }

    // Whether a Window's root ViewModel takes part in the menu bar.
    internal static bool Binds(object? viewModel) => viewModel is IRunicMenuCommandSource or IRunicMenuCheckedSource;

    // The check mark of a check or radio item: the ViewModel's answer, or null when it has none, so the next target or
    // the declaration decides (MenuCommandChain).
    public bool? IsChecked(DesktopMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsChecked is null) return null;
        lock (_gate) return !_disposed && _checked.TryGetValue(MenuCommandKey.Of(item), out var isChecked) ? isChecked : null;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) => RefreshChecked();

    // Reads the check marks again in the next model turn (RunicMenu.Refresh and PropertyChanged). Requests before that
    // read starts join it, so a turn that raises PropertyChanged many times costs one read.
    internal void RefreshChecked()
    {
        if (_isChecked is null || Interlocked.Exchange(ref _checkRefresh, 1) == 1) return;
        if (_context is { } context)
        {
            if (!context.TryPost(ReadCheckedTurn)) Volatile.Write(ref _checkRefresh, 0);
        }
        else
        {
            _ = Task.Run(ReadCheckedTurn);
        }
    }

    private void ReadCheckedTurn()
    {
        try
        {
            Volatile.Write(ref _checkRefresh, 0);
            HashSet<MenuCommandKey> toggles;
            lock (_gate)
            {
                if (_disposed) return;
                toggles = _toggles;
            }
            var answers = ReadChecked(toggles);
            bool changed, rebound;
            lock (_gate)
            {
                if (_disposed) return;
                // The bar was bound again meanwhile: read once more for its items.
                rebound = !ReferenceEquals(toggles, _toggles);
                changed = !rebound && !SameAnswers(_checked, answers);
                if (changed) _checked = answers;
            }
            if (rebound) RefreshChecked();
            else if (changed) RaiseChanged();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _checkRefresh, 0);
            MenuBarLog.CommandFailed(_logger, error, "*");
        }
    }

    private static bool SameAnswers(Dictionary<MenuCommandKey, bool> before, Dictionary<MenuCommandKey, bool> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value);

    // The ViewModel's check marks, in its model turn. A failing answer keeps the declaration and is logged.
    private Dictionary<MenuCommandKey, bool> ReadChecked(HashSet<MenuCommandKey> toggles)
    {
        Dictionary<MenuCommandKey, bool> answers = [];
        if (_isChecked is not { } isChecked) return answers;
        foreach (var key in toggles)
        {
            try
            {
                if (isChecked(key.CommandId, key.Parameter) is { } answer) answers[key] = answer;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                MenuBarLog.CommandFailed(_logger, error, key.CommandId);
            }
        }
        return answers;
    }

    // Raised, on any thread, after the availability of a bound command changed or the bar's commands were bound again,
    // so an in-page menu bar can follow it (MenuBarPageRoutes).
    internal event EventHandler? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MenuBarLog.CommandFailed(_logger, error, "*");
        }
    }

    public bool CanExecute(DesktopMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate) return !_disposed && _entries.TryGetValue(MenuCommandKey.Of(item), out var entry) && entry.CanExecute;
    }

    public void Execute(DesktopMenuItem item) => Execute(item, invoker: null);

    // An item that the page whose connection is invoker chose (MenuBarPageRoutes), or the native bar's (null).
    internal void Execute(DesktopMenuItem item, BridgeConnection? invoker)
    {
        ArgumentNullException.ThrowIfNull(item);
        Entry? entry;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(MenuCommandKey.Of(item), out entry)) return;
        }
        _ = RunAsync(entry, invoker);
    }

    private async Task RunAsync(Entry entry, BridgeConnection? invoker)
    {
        try
        {
            if (_context is { IsExecuting: false } context)
                await context.InvokeAsync(() => ExecuteInWindow(entry, invoker)).ConfigureAwait(false);
            else
                await Task.Run(() => ExecuteInWindow(entry, invoker)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            MenuBarLog.CommandFailed(_logger, error, entry.CommandId);
        }
    }

    // The command runs in the invoking page's command invocation, or without a page in the Window's scope, which names no
    // page and flows with the command's asynchronous work. A page's invocation ends when Execute returns, unless the
    // command is a CommunityToolkit IAsyncRelayCommand that started a task: then it ends with that task, so the command
    // asks the clicking page also after its first await. The page's identity is the one its transport authenticated,
    // never one the request claims.
    private void ExecuteInWindow(Entry entry, BridgeConnection? invoker)
    {
        using var scope = _window is null ? null
            : invoker is not null
                ? RunicInteractionInvocation.Enter(_window, MenuBarPageRoutes.Route, invoker.ClientKey, invoker.Key, entry.CommandId)
                : RunicInteractionInvocation.EnterWindowScope(_window, CancellationToken.None);
        var before = invoker is null ? null : ExecutionTask(entry.Command);
        if (!entry.Command.CanExecute(entry.Parameter)) return;
        entry.Command.Execute(entry.Parameter);
        // A task that this Execute started: another reference than before, still running.
        if (invoker is not null && scope is not null && ExecutionTask(entry.Command) is { IsCompleted: false } task
            && !ReferenceEquals(task, before))
        {
            // The command reports its own failure; the continuation only observes it.
            _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            scope.EndAfter(task);
        }
    }

    // CommunityToolkit.Mvvm's IAsyncRelayCommand.ExecutionTask, read by name because Runic references no MVVM library. A
    // trimmed or AOT application whose command lost the property's metadata reads null, and the invocation ends when
    // Execute returns, as for any other command.
    private static readonly ConditionalWeakTable<Type, PropertyInfoHolder> ExecutionTaskProperties = new();

    private sealed class PropertyInfoHolder(System.Reflection.PropertyInfo? property)
    {
        public System.Reflection.PropertyInfo? Property { get; } = property;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Without the interface's metadata the property reads null, and the command keeps the behaviour of other commands.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Without the interface's metadata the property reads null, and the command keeps the behaviour of other commands.")]
    private static Task? ExecutionTask(ICommand command)
    {
        var holder = ExecutionTaskProperties.GetValue(command.GetType(), static type =>
            new PropertyInfoHolder(type.GetInterface("CommunityToolkit.Mvvm.Input.IAsyncRelayCommand")?.GetProperty("ExecutionTask")));
        if (holder.Property is not { } property) return null;
        try { return property.GetValue(command) as Task; }
        catch (Exception) { return null; }
    }

    private void OnMenuBarChanged(object? sender, EventArgs eventArgs) => _ = RebindAsync();

    private async Task RebindAsync()
    {
        try
        {
            var menus = _host.MenuBar;
            var ids = CommandIds(menus);
            var toggles = ToggleKeys(menus);
            var (resolved, answers) = _context is { IsExecuting: false } context
                ? await context.InvokeAsync(() => (Resolve(ids), ReadChecked(toggles))).ConfigureAwait(false)
                : (Resolve(ids), ReadChecked(toggles));
            Dictionary<MenuCommandKey, Entry> previous;
            lock (_gate)
            {
                previous = _entries;
                if (_disposed)
                {
                    previous = resolved;
                }
                else
                {
                    _entries = resolved;
                    _toggles = toggles;
                    _checked = answers;
                }
            }
            foreach (var entry in previous.Values) entry.Dispose();
            RaiseChanged();
        }
        catch (Exception error)
        {
            MenuBarLog.CommandFailed(_logger, error, "*");
        }
    }

    private Dictionary<MenuCommandKey, Entry> Resolve(IEnumerable<MenuCommandKey> keys)
    {
        Dictionary<MenuCommandKey, Entry> entries = [];
        Dictionary<string, ICommand?> commands = new(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!commands.TryGetValue(key.CommandId, out var command)) commands[key.CommandId] = command = _resolve(key.CommandId);
            if (command is not null) entries[key] = new Entry(key.CommandId, key.Parameter, command, RaiseChanged);
        }
        return entries;
    }

    // Every command id of the bar, with each parameter its items pass.
    internal static HashSet<MenuCommandKey> CommandIds(IReadOnlyList<DesktopMenu> menus)
    {
        HashSet<MenuCommandKey> ids = [];
        void Collect(IEnumerable<DesktopMenuEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is DesktopMenuItem item) ids.Add(MenuCommandKey.Of(item));
                else if (entry is DesktopMenu submenu) Collect(submenu.Items);
            }
        }
        foreach (var menu in menus) Collect(menu.Items);
        return ids;
    }

    // The command id and parameter of every check and radio item of the bar.
    internal static HashSet<MenuCommandKey> ToggleKeys(IReadOnlyList<DesktopMenu> menus)
    {
        HashSet<MenuCommandKey> keys = [];
        void Collect(IEnumerable<DesktopMenuEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is DesktopMenuItem { IsChecked: not null } item) keys.Add(MenuCommandKey.Of(item));
                else if (entry is DesktopMenu submenu) Collect(submenu.Items);
            }
        }
        foreach (var menu in menus) Collect(menu.Items);
        return keys;
    }

    public void Dispose()
    {
        _host.MenuBarChanged -= OnMenuBarChanged;
        if (_notifier is { } notifier) notifier.PropertyChanged -= OnPropertyChanged;
        if (_owner is { } owner) RunicMenu.Unregister(owner, RefreshChecked);
        Dictionary<MenuCommandKey, Entry> entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries;
            _entries = [];
        }
        foreach (var entry in entries.Values) entry.Dispose();
    }

    // A command id and the parameter an item passes to it.
    internal readonly record struct MenuCommandKey(string CommandId, string? Parameter)
    {
        internal static MenuCommandKey Of(DesktopMenuItem item) => new(item.CommandId, item.CommandParameter);
    }

    // One command and its cached availability for one parameter, read with the parameter the items pass.
    private sealed class Entry : IDisposable
    {
        private readonly Action _changed;
        private volatile bool _canExecute;

        internal Entry(string commandId, string? parameter, ICommand command, Action changed)
        {
            CommandId = commandId;
            Parameter = parameter;
            Command = command;
            _changed = changed;
            command.CanExecuteChanged += OnCanExecuteChanged;
            Refresh();
        }

        internal string CommandId { get; }
        internal string? Parameter { get; }
        internal ICommand Command { get; }
        internal bool CanExecute => _canExecute;

        private void OnCanExecuteChanged(object? sender, EventArgs eventArgs)
        {
            var before = _canExecute;
            Refresh();
            if (before != _canExecute) _changed();
        }

        private void Refresh()
        {
            try { _canExecute = Command.CanExecute(Parameter); }
            catch (Exception error) when (error is not OutOfMemoryException) { _canExecute = false; }
        }

        public void Dispose() => Command.CanExecuteChanged -= OnCanExecuteChanged;
    }
}

using System.Collections.Concurrent;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using Runic.Platform;

namespace Runic.Application.Views.Menus;

// Binds the menu bar's command ids to ICommands: those of a Window's root ViewModel (IRunicMenuCommandSource), or the
// application's (RunicDesktopHost.UseApplicationMenu). AppKit asks CanExecute on its main thread while it shows a menu or
// matches a key, so the availability of each command is cached per command id and parameter and updated from
// CanExecuteChanged. Resolving and running a command happens in the model turn of the ViewModel, when there is one.
// An item's CommandParameter is the command's parameter. Shared with Runic.Application.Views.CsWebUi, like
// MenuBarPageRoutes; on Runic Desktop it is also the window's IDesktopMenuCommandTarget (MenuCommandBinding.Desktop.cs).
internal sealed partial class MenuCommandBinding : IDisposable
{
    private readonly IRunicModelContext? _context;
    private readonly Func<string, ICommand?> _resolve;
    private readonly ILogger _logger;
    private readonly IMenuBarSource _host;
    private readonly object _gate = new();
    private Dictionary<MenuCommandKey, Entry> _entries = [];
    private bool _disposed;

    private MenuCommandBinding(IMenuBarSource host, IRunicModelContext? context, Func<string, ICommand?> resolve, ILogger logger)
    {
        _host = host;
        _context = context;
        _resolve = resolve;
        _logger = logger;
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

    // Binds every command id of the host's menu bar now, and again whenever the bar changes.
    internal static async ValueTask<MenuCommandBinding> BindAsync(IMenuBarSource host, IRunicModelContext? context,
        Func<string, ICommand?> resolve, ILogger logger)
    {
        var binding = new MenuCommandBinding(host, context, resolve, logger);
        host.MenuBarChanged += binding.OnMenuBarChanged;
        await binding.RebindAsync().ConfigureAwait(false);
        return binding;
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

    public void Execute(DesktopMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Entry? entry;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(MenuCommandKey.Of(item), out entry)) return;
        }
        _ = RunAsync(entry);
    }

    private async Task RunAsync(Entry entry)
    {
        try
        {
            if (_context is { IsExecuting: false } context)
                await context.InvokeAsync(() => ExecuteNow(entry.Command, entry.Parameter)).ConfigureAwait(false);
            else
                await Task.Run(() => ExecuteNow(entry.Command, entry.Parameter)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            MenuBarLog.CommandFailed(_logger, error, entry.CommandId);
        }
    }

    private static void ExecuteNow(ICommand command, string? parameter)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }

    private void OnMenuBarChanged(object? sender, EventArgs eventArgs) => _ = RebindAsync();

    private async Task RebindAsync()
    {
        try
        {
            var ids = CommandIds(_host.MenuBar);
            var resolved = _context is { IsExecuting: false } context
                ? await context.InvokeAsync(() => Resolve(ids)).ConfigureAwait(false)
                : Resolve(ids);
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

    public void Dispose()
    {
        _host.MenuBarChanged -= OnMenuBarChanged;
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

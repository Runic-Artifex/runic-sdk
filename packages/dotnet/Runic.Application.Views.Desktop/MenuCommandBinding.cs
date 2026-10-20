using System.Collections.Concurrent;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Navigation;

namespace Runic.Application.Views.Desktop;

// Binds the menu bar's command ids to ICommands: those of a Window's root ViewModel (IRunicMenuCommandSource), or the
// application's (RunicDesktopHost.UseApplicationMenu). AppKit asks CanExecute on its main thread while it shows a menu or
// matches a key, so the availability of each command is cached and updated from CanExecuteChanged. Resolving and
// running a command happens in the model turn of the ViewModel, when there is one.
internal sealed class MenuCommandBinding : IDesktopMenuCommandTarget, IDisposable
{
    private readonly IRunicModelContext? _context;
    private readonly Func<string, ICommand?> _resolve;
    private readonly ILogger _logger;
    private readonly DesktopHost _host;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    private MenuCommandBinding(DesktopHost host, IRunicModelContext? context, Func<string, ICommand?> resolve, ILogger logger)
    {
        _host = host;
        _context = context;
        _resolve = resolve;
        _logger = logger;
    }

    // Binds application commands, which have no model context: the menu bar's command ids resolve before this
    // returns, and again whenever the bar changes.
    internal static MenuCommandBinding Bind(DesktopHost host, Func<string, ICommand?> resolve, ILogger logger)
    {
        var binding = new MenuCommandBinding(host, context: null, resolve, logger);
        host.MenuBarChanged += binding.OnMenuBarChanged;
        _ = binding.RebindAsync();
        return binding;
    }

    // Binds every command id of the host's menu bar now, and again whenever the bar changes.
    internal static async ValueTask<MenuCommandBinding> BindAsync(DesktopHost host, IRunicModelContext? context,
        Func<string, ICommand?> resolve, ILogger logger)
    {
        var binding = new MenuCommandBinding(host, context, resolve, logger);
        host.MenuBarChanged += binding.OnMenuBarChanged;
        await binding.RebindAsync().ConfigureAwait(false);
        return binding;
    }

    public bool CanExecute(string commandId)
    {
        lock (_gate) return !_disposed && _entries.TryGetValue(commandId, out var entry) && entry.CanExecute;
    }

    public void Execute(string commandId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(commandId, out entry)) return;
        }
        _ = RunAsync(entry);
    }

    private async Task RunAsync(Entry entry)
    {
        try
        {
            if (_context is { IsExecuting: false } context)
                await context.InvokeAsync(() => ExecuteNow(entry.Command)).ConfigureAwait(false);
            else
                await Task.Run(() => ExecuteNow(entry.Command)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            DesktopLog.MenuCommandFailed(_logger, error, entry.CommandId, error.GetType().FullName ?? error.GetType().Name);
        }
    }

    private static void ExecuteNow(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
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
            Dictionary<string, Entry> previous;
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
        }
        catch (Exception error)
        {
            DesktopLog.MenuCommandFailed(_logger, error, "*", error.GetType().FullName ?? error.GetType().Name);
        }
    }

    private Dictionary<string, Entry> Resolve(IEnumerable<string> ids)
    {
        Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (_resolve(id) is { } command) entries[id] = new Entry(id, command);
        }
        return entries;
    }

    internal static HashSet<string> CommandIds(IReadOnlyList<DesktopMenu> menus)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        void Collect(IEnumerable<DesktopMenuEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is DesktopMenuBarItem item) ids.Add(item.CommandId);
                else if (entry is DesktopMenu submenu) Collect(submenu.Items);
            }
        }
        foreach (var menu in menus) Collect(menu.Items);
        return ids;
    }

    public void Dispose()
    {
        _host.MenuBarChanged -= OnMenuBarChanged;
        Dictionary<string, Entry> entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries;
            _entries = new(StringComparer.Ordinal);
        }
        foreach (var entry in entries.Values) entry.Dispose();
    }

    // One command and its cached availability, read with a null parameter as a menu item runs it.
    private sealed class Entry : IDisposable
    {
        private volatile bool _canExecute;

        internal Entry(string commandId, ICommand command)
        {
            CommandId = commandId;
            Command = command;
            command.CanExecuteChanged += OnCanExecuteChanged;
            Refresh();
        }

        internal string CommandId { get; }
        internal ICommand Command { get; }
        internal bool CanExecute => _canExecute;

        private void OnCanExecuteChanged(object? sender, EventArgs eventArgs) => Refresh();

        private void Refresh()
        {
            try { _canExecute = Command.CanExecute(null); }
            catch (Exception error) when (error is not OutOfMemoryException) { _canExecute = false; }
        }

        public void Dispose() => Command.CanExecuteChanged -= OnCanExecuteChanged;
    }
}

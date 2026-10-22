using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The one command path of a menu item, for the native bar, a global menu, shortcuts, the page and
// DesktopWindow.TryExecuteMenuItemAsync: the declaration, then the window's target, then the application's, each asked
// CanExecute before Execute. A target that throws is logged (event 3014) and counts as unable to run the item.
// Callers run it on the window's UI thread.
internal sealed class MenuCommandChain(Func<IDesktopMenuCommandTarget?> application, ILogger? logger) : IDesktopMenuCommandTarget
{
    private IDesktopMenuCommandTarget? _window;

    // The window's own target, DesktopWindow.MenuCommands.
    internal IDesktopMenuCommandTarget? Window
    {
        get => Volatile.Read(ref _window);
        set => Volatile.Write(ref _window, value);
    }

    public bool CanExecute(DesktopMenuItem item) => CanExecute(item, Window, application(), logger);

    public void Execute(DesktopMenuItem item) => _ = TryExecute(item, Window, application(), logger);

    internal bool TryExecute(DesktopMenuItem item) => TryExecute(item, Window, application(), logger);

    internal static bool CanExecute(DesktopMenuItem item, IDesktopMenuCommandTarget? window, IDesktopMenuCommandTarget? application,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsEnabled) return false;
        return (window is not null && SafeCanExecute(window, item, logger))
            || (application is not null && !ReferenceEquals(application, window) && SafeCanExecute(application, item, logger));
    }

    // Runs the item on the first target that can run it; false when none ran it.
    internal static bool TryExecute(DesktopMenuItem item, IDesktopMenuCommandTarget? window, IDesktopMenuCommandTarget? application,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsEnabled) return false;
        foreach (var target in (ReadOnlySpan<IDesktopMenuCommandTarget?>)[window, ReferenceEquals(application, window) ? null : application])
        {
            if (target is null || !SafeCanExecute(target, item, logger)) continue;
            try
            {
                target.Execute(item);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Report(logger, error, "Execute", item);
            }
            return true;
        }
        return false;
    }

    private static bool SafeCanExecute(IDesktopMenuCommandTarget target, DesktopMenuItem item, ILogger? logger)
    {
        try
        {
            return target.CanExecute(item);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Report(logger, error, "CanExecute", item);
            return false;
        }
    }

    private static void Report(ILogger? logger, Exception error, string member, DesktopMenuItem item)
    {
        if (logger is not null)
            DesktopLog.MenuCommandFailed(logger, error, member, item.CommandId, DesktopLog.ErrorType(error));
        else
            Trace.TraceError($"A menu command target's {member} failed for {item.CommandId} with {DesktopLog.ErrorType(error)}.");
    }
}

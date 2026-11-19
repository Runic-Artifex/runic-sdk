using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// The page's developer tools on request (#1188): DesktopWindow.OpenDevToolsAsync and the menu item of
// DesktopMenuItem.OpenDevToolsCommandId show WebKit's Web Inspector, as Inspect Element from the page's context menu does,
// while the window's developer extras are on (DesktopWebContentPolicy.DevTools).
internal sealed partial class Gtk4WindowHost : IDesktopDevToolsWindowHost
{
    // GTK thread.
    public bool CanOpenDevTools => IsOpen && _webView is not null && _webContent is { Rules.DevToolsEnabled: true };

    public ValueTask<bool> OpenDevToolsAsync(CancellationToken cancellationToken)
    {
        if (!IsOpen) return ValueTask.FromResult(false);
        var opened = false;
        var work = Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (!CanOpenDevTools || _webView is not { } view) return;
            view.GetInspector().Show();
            opened = true;
        }, cancellationToken);
        return work.IsCompletedSuccessfully ? ValueTask.FromResult(opened) : AwaitAsync(work);

        async ValueTask<bool> AwaitAsync(Task pending)
        {
            await pending.ConfigureAwait(false);
            return opened;
        }
    }
}

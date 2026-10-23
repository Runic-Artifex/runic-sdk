using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.Wpf;

// A context menu of a WPF window: a WPF ContextMenu built from the entries with the Windows menu rules (access keys,
// shortcut hints, disabled menus), at the pointer or at a page point of a RunicWebView. One menu per window: a new menu
// closes the open one, which returns null.
internal static class WpfContextMenu
{
    private static readonly ConditionalWeakTable<DependencyObject, Session> Open = new();

    // Each menu once it is open, on its dispatcher; the native WPF checks drive it from here.
    internal static Action<ContextMenu>? Opened { get; set; }

    private sealed class Session(ContextMenu menu, CancellationToken cancellationToken)
    {
        internal ContextMenu Menu { get; } = menu;
        internal DesktopMenuItem? Chosen { get; set; }
        internal TaskCompletionSource<DesktopMenuItem?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Complete()
        {
            if (Chosen is null && cancellationToken.IsCancellationRequested) Result.TrySetCanceled(cancellationToken);
            else Result.TrySetResult(Chosen);
        }
    }

    // On target's dispatcher. pageZoom gives the page's zoom for a page point, or null when the target shows no page.
    internal static Task<DesktopMenuItem?> Show(FrameworkElement target, ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        Func<double>? pageZoom, CancellationToken cancellationToken)
    {
        target.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        var owner = (DependencyObject?)Window.GetWindow(target) ?? target;
        if (Open.TryGetValue(owner, out var previous)) previous.Menu.IsOpen = false;

        var menu = new ContextMenu { PlacementTarget = target, FlowDirection = target.FlowDirection };
        var session = new Session(menu, cancellationToken);
        foreach (var entry in DesktopMenuPlanner.PlanEntries(items, MenuPlatform.Windows))
            if (Build(entry, session) is { } built) menu.Items.Add(built);
        if (options is { X: { } x, Y: { } y } && pageZoom is not null)
        {
            // CSS pixels times the page zoom are WPF's device-independent pixels. The page is laid out left to right
            // also in a right-to-left window, where WPF measures the offset from the target's right edge.
            var zoom = pageZoom();
            if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
            var left = Math.Clamp(x * zoom, 0, target.ActualWidth);
            menu.Placement = PlacementMode.Relative;
            menu.HorizontalOffset = target.FlowDirection == FlowDirection.RightToLeft ? target.ActualWidth - left : left;
            menu.VerticalOffset = Math.Clamp(y * zoom, 0, target.ActualHeight);
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }
        CancellationTokenRegistration registration = default;
        menu.Closed += (_, _) =>
        {
            registration.Dispose();
            if (Open.TryGetValue(owner, out var current) && ReferenceEquals(current, session)) Open.Remove(owner);
            session.Complete();
        };
        Open.AddOrUpdate(owner, session);
        menu.IsOpen = true;
        if (!menu.IsOpen)
        {
            // WPF did not open it, for example for a target that is not shown.
            Open.Remove(owner);
            session.Complete();
            return session.Result.Task;
        }
        registration = cancellationToken.Register(() => target.Dispatcher.BeginInvoke(() => menu.IsOpen = false));
        Opened?.Invoke(menu);
        return session.Result.Task;
    }

    private static Control? Build(PlannedMenuEntry entry, Session session)
    {
        switch (entry.Kind)
        {
            case PlannedMenuKind.Separator:
                return new Separator();
            case PlannedMenuKind.Menu:
                var submenu = new MenuItem { Header = entry.MnemonicLabel, IsEnabled = entry.Enabled };
                foreach (var child in entry.Children)
                    if (Build(child, session) is { } built) submenu.Items.Add(built);
                return submenu;
            default:
                if (entry.Item is not { } item) return null;
                // A shortcut is a hint: the menu registers none. A radio item shows the check mark, as the Win32 menu does.
                var menuItem = new MenuItem
                {
                    Header = entry.MnemonicLabel,
                    IsEnabled = entry.Enabled,
                    IsChecked = item.IsChecked == true,
                    InputGestureText = entry.Shortcut?.DisplayText ?? "",
                };
                menuItem.Click += (_, _) => session.Chosen = item;
                return menuItem;
        }
    }
}

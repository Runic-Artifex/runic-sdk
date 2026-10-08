using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Runic.Navigation.Wpf;

/// <summary>
/// Shows each entry of a dialog region in its own owned window: a zero-size element to place in the owner window.
/// </summary>
/// <remarks>
/// <para>
/// The host shows one window per entry in the region's stack, each owned by the window below it and the bottom one by
/// the host's window. Windows are shown with <see cref="Window.Show"/>, never <see cref="Window.ShowDialog"/>, so the
/// host never runs a nested message pump. It emulates modality instead: see <see cref="Modality"/>.
/// </para>
/// <para>
/// A dialog completes through its entry (<c>CompleteAsync</c> or <c>DismissAsync</c>), never through
/// <see cref="Window.DialogResult"/>, which throws on a window shown with <see cref="Window.Show"/>, and never through
/// <c>IsCancel</c> or <c>IsDefault</c> buttons. Closing a dialog window (the close button, Alt+F4 or, with
/// <see cref="CloseOnEscape"/>, Esc) goes back in the region, or clears it from its bottom entry; a guard may veto it,
/// and the window stays open. The host never cancels its owner's close: the dialogs close with the owner, and the host
/// clears the region once.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public class NavigationDialogHost : FrameworkElement
{
    /// <summary>Identifies the <see cref="Region"/> dependency property.</summary>
    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(nameof(Region), typeof(INavigationRegion),
        typeof(NavigationDialogHost), new PropertyMetadata(null, static (d, _) => ((NavigationDialogHost)d).OnRegionChanged()));

    /// <summary>Identifies the <see cref="ViewLocator"/> dependency property.</summary>
    public static readonly DependencyProperty ViewLocatorProperty = DependencyProperty.Register(nameof(ViewLocator),
        typeof(INavigationViewLocator), typeof(NavigationDialogHost), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="WindowStyle"/> dependency property.</summary>
    public static readonly DependencyProperty WindowStyleProperty = DependencyProperty.Register(nameof(WindowStyle), typeof(Style),
        typeof(NavigationDialogHost), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Modality"/> dependency property.</summary>
    public static readonly DependencyProperty ModalityProperty = DependencyProperty.Register(nameof(Modality),
        typeof(NavigationDialogModality), typeof(NavigationDialogHost),
        new PropertyMetadata(NavigationDialogModality.Application));

    /// <summary>Identifies the <see cref="CloseOnEscape"/> dependency property.</summary>
    public static readonly DependencyProperty CloseOnEscapeProperty = DependencyProperty.Register(nameof(CloseOnEscape), typeof(bool),
        typeof(NavigationDialogHost), new PropertyMetadata(true));

    private readonly PropertyChangedEventHandler _regionChanged;
    private readonly List<DialogWindow> _windows = [];
    private readonly Dictionary<nint, DialogModalityTable.Hold> _disabled = [];
    private INavigationRegion? _attached;
    private INavigationRegion? _lastRegion;
    private Window? _owner;
    private bool _noOwnerLogged;
    private bool _ownerClosed;
    private bool _modal;
    private int _reconcilePosted;
    // The stack whose window failed to show; the host doesn't show it again until the stack changes or it is loaded again.
    private NavigationEntryId[]? _failedStack;

    /// <summary>Creates a dialog host.</summary>
    public NavigationDialogHost()
    {
        _regionChanged = OnRegionPropertyChanged;
        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => OnUnloaded();
    }

    /// <summary>Gets or sets the dialog region.</summary>
    public INavigationRegion? Region
    {
        get => (INavigationRegion?)GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    /// <summary>Gets or sets the locator asked first for each entry's view.</summary>
    public INavigationViewLocator? ViewLocator
    {
        get => (INavigationViewLocator?)GetValue(ViewLocatorProperty);
        set => SetValue(ViewLocatorProperty, value);
    }

    /// <summary>
    /// Gets or sets the style of each dialog window, for its title and chrome. Unless the style sets them, a dialog window
    /// sizes to its content and isn't shown in the taskbar. Dialog windows are centered on their owner.
    /// </summary>
    public Style? WindowStyle
    {
        get => (Style?)GetValue(WindowStyleProperty);
        set => SetValue(WindowStyleProperty, value);
    }

    /// <summary>
    /// Gets or sets what the host disables while the region has dialog windows. The default,
    /// <see cref="NavigationDialogModality.Application"/>, disables every other top-level window of the UI thread.
    /// </summary>
    /// <remarks>
    /// Each dialog window disables what it finds when it opens, through a refcount shared by all dialog hosts of the thread:
    /// a window that something else disabled is left alone and stays disabled. The windows are re-enabled before the
    /// dialog above them closes, so the owner is activated next. A change applies to dialogs opened afterwards.
    /// </remarks>
    public NavigationDialogModality Modality
    {
        get => (NavigationDialogModality)GetValue(ModalityProperty);
        set => SetValue(ModalityProperty, value);
    }

    /// <summary>Gets or sets whether Esc in a dialog window closes it like its close button. The default is <see langword="true"/>.</summary>
    public bool CloseOnEscape
    {
        get => (bool)GetValue(CloseOnEscapeProperty);
        set => SetValue(CloseOnEscapeProperty, value);
    }

    // The open dialog windows, bottom to top, for tests.
    internal IReadOnlyList<Window> DialogWindows => [.. _windows.Select(dialog => dialog.Window)];

    private void OnLoaded()
    {
        _failedStack = null;
        var owner = Window.GetWindow(this);
        if (!ReferenceEquals(owner, _owner))
        {
            DetachOwner();
            _owner = owner;
            if (owner is not null) owner.Closed += OnOwnerClosed;
        }
        if (owner is null && !_noOwnerLogged)
        {
            // Dialogs open only under an owner window: in a Popup or an ElementHost there is none.
            _noOwnerLogged = true;
            WpfNavigationLog.DialogHostWithoutWindow(WpfNavigationLog.For(Region?.Navigator.Services));
        }
        _ownerClosed = false;
        AttachRegion();
        ScheduleReconcile();
    }

    private void OnUnloaded()
    {
        // The host closes its own windows; the entries stay.
        if (!_ownerClosed) CloseFrom(0);
        DetachRegion();
        UpdateModality();
        // An owner that is closing raises Closed after its content unloads; keep the subscription until then.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!IsLoaded) DetachOwner();
        });
    }

    private void OnRegionChanged()
    {
        if (!IsLoaded) return;
        CloseFrom(0);
        DetachRegion();
        UpdateModality();
        _failedStack = null;
        AttachRegion();
        ScheduleReconcile();
    }

    private void AttachRegion()
    {
        var region = Region;
        if (ReferenceEquals(region, _attached)) return;
        DetachRegion();
        if (region is not null) region.PropertyChanged += _regionChanged;
        _attached = region;
        _lastRegion = region;
    }

    private void DetachRegion()
    {
        if (_attached is not null) _attached.PropertyChanged -= _regionChanged;
        _attached = null;
    }

    private void DetachOwner()
    {
        if (_owner is not null) _owner.Closed -= OnOwnerClosed;
        _owner = null;
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(INavigationRegion.CurrentEntry) or nameof(INavigationRegion.History) or null or "")
            ScheduleReconcile();
    }

    // Posted, never in the commit turn; changes before it runs coalesce into one reconcile.
    private void ScheduleReconcile()
    {
        if (Interlocked.Exchange(ref _reconcilePosted, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, Reconcile);
    }

    private void Reconcile()
    {
        Volatile.Write(ref _reconcilePosted, 0);
        var region = _attached;
        if (region is null || !IsLoaded || _ownerClosed || _owner is not { } owner || PresentationSource.FromVisual(owner) is null)
            return;
        // Read the stack again, by entry ID, now.
        List<INavigationEntry> stack = [.. region.History];
        if (region.CurrentEntry is { } current) stack.Add(current);
        var keep = 0;
        while (keep < _windows.Count && keep < stack.Count && _windows[keep].Id == stack[keep].Id) keep++;
        CloseFrom(keep);
        NavigationEntryId[] ids = [.. stack.Select(entry => entry.Id)];
        if (_failedStack is not null)
        {
            if (_failedStack.AsSpan().SequenceEqual(ids))
            {
                UpdateModality();
                return;
            }
            _failedStack = null;
        }
        for (var index = keep; index < stack.Count; index++)
            if (!Open(region, stack[index], ids)) break;
        UpdateModality();
    }

    // Closes the windows from `index` up, top-down, after re-enabling the windows they disabled.
    private void CloseFrom(int index)
    {
        if (index >= _windows.Count) return;
        var closing = _windows.GetRange(index, _windows.Count - index);
        foreach (var dialog in closing) dialog.HostClosing = true;
        UpdateModality();
        for (var i = closing.Count - 1; i >= 0; i--)
        {
            var dialog = closing[i];
            _windows.Remove(dialog);
            dialog.Window.Closing -= OnDialogClosing;
            dialog.Window.Closed -= OnDialogClosed;
            dialog.Window.Close();
        }
    }

    private bool Open(INavigationRegion region, INavigationEntry entry, NavigationEntryId[] stack)
    {
        var owner = _windows.Count > 0 ? _windows[^1].Window : _owner!;
        var window = new Window();
        var dialog = new DialogWindow(entry.Id, window);
        try
        {
            if (WindowStyle is { } style) window.Style = style;
            SetUnlessStyled(window, Window.SizeToContentProperty, SizeToContent.WidthAndHeight);
            SetUnlessStyled(window, Window.ShowInTaskbarProperty, false);
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Owner = owner;
            window.DataContext = entry.Content;
            window.Content = NavigationPresenters.Create(this, region, entry, ViewLocator, explicitTemplate: true);
            window.InputBindings.Add(new KeyBinding(new EscapeCommand(this, entry.Id), Key.Escape, ModifierKeys.None));
            window.Closing += OnDialogClosing;
            window.Closed += OnDialogClosed;
            _windows.Add(dialog);
            window.Show();
            dialog.Level = ModalLevel(window);
            return true;
        }
        catch (Exception error)
        {
            _windows.Remove(dialog);
            window.Closing -= OnDialogClosing;
            window.Closed -= OnDialogClosed;
            dialog.HostClosing = true;
            // Unregisters the window from its owner; a window that failed to show may fail to close too.
            try { window.Close(); }
            catch (Exception) { }
            WpfNavigationLog.DialogShowFailed(WpfNavigationLog.For(region.Navigator.Services), error,
                WpfNavigationLog.TypeName(entry.Content.GetType()), WpfNavigationLog.ErrorType(error));
            // One attempt per stack: dismiss the whole dialog stack, so each result request ends Dismissed.
            _failedStack = stack;
            ClearOnce(region);
            return false;
        }
    }

    private static void SetUnlessStyled(Window window, DependencyProperty property, object value)
    {
        if (DependencyPropertyHelper.GetValueSource(window, property).BaseValueSource == BaseValueSource.Default)
            window.SetValue(property, value);
    }

    // A close the host didn't start (close button, Alt+F4, Esc, code): cancel it and dismiss through the region. When the
    // owner closes or the app shuts down, WPF ignores the cancel and the window closes; the posted dismiss then finds the
    // owner closed and does nothing.
    private void OnDialogClosing(object? sender, CancelEventArgs e)
    {
        var dialog = Find(sender);
        if (dialog is null || dialog.HostClosing) return;
        e.Cancel = true;
        PostDismiss(dialog.Id);
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        var dialog = Find(sender);
        if (dialog is null) return;
        _windows.Remove(dialog);
        UpdateModality();
        // The window closed anyway; a reconcile shows it again if its entry stays.
        if (!_ownerClosed) ScheduleReconcile();
    }

    private void PostDismiss(NavigationEntryId id) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (_ownerClosed || _attached is not { } region || region.CurrentEntry?.Id != id) return;
            // A plain Back from a single entry is NoHistory, so the bottom dialog clears.
            var options = new NavigationRequestOptions(id);
            if (region.CanGoBack) _ = region.BackAsync(options).AsTask();
            else _ = region.ClearAsync(options).AsTask();
        });

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _owner)) return;
        // Owner-closing: WPF closed the dialogs with their owner. Detach, then clear once, best effort.
        _ownerClosed = true;
        foreach (var dialog in _windows)
        {
            dialog.Window.Closing -= OnDialogClosing;
            dialog.Window.Closed -= OnDialogClosed;
        }
        _windows.Clear();
        UpdateModality();
        DetachOwner();
        var region = _attached ?? _lastRegion;
        DetachRegion();
        if (region is not null) ClearOnce(region);
    }

    private static void ClearOnce(INavigationRegion region)
    {
        if (region.CurrentEntry is not { } current) return;
        try { _ = region.ClearAsync(new NavigationRequestOptions(current.Id)).AsTask(); }
        catch (ObjectDisposedException) { }
    }

    private DialogWindow? Find(object? window)
    {
        foreach (var dialog in _windows)
            if (ReferenceEquals(dialog.Window, window)) return dialog;
        return null;
    }

    // Disables what the open dialogs make modal and re-enables what they no longer do, through the shared refcount.
    private void UpdateModality()
    {
        List<DialogWindow> open = [.. _windows.Where(dialog => !dialog.HostClosing)];
        if (open.Count == 0 || _ownerClosed)
        {
            foreach (var hold in _disabled.Values) DialogModalityTable.Release(hold);
            _disabled.Clear();
            if (_modal)
            {
                _modal = false;
                ComponentDispatcher.PopModal();
            }
            return;
        }
        if (!_modal)
        {
            _modal = true;
            ComponentDispatcher.PushModal();
        }
        // Each dialog disables what it found when it opened, which never includes the dialogs above it.
        HashSet<nint> required = [];
        foreach (var dialog in open) required.UnionWith(dialog.Level);
        foreach (var hwnd in _disabled.Keys.Where(hwnd => !required.Contains(hwnd)).ToList())
        {
            DialogModalityTable.Release(_disabled[hwnd]);
            _disabled.Remove(hwnd);
        }
        foreach (var hwnd in required)
        {
            // A dead hold stays: its window was destroyed, and a new window with that handle isn't this dialog's to disable.
            if (_disabled.ContainsKey(hwnd)) continue;
            if (DialogModalityTable.TryDisable(hwnd) is { } hold) _disabled[hwnd] = hold;
        }
    }

    // What a dialog shown now makes modal: with Application, every other visible top-level window of the thread, as
    // ShowDialog; with Owner, the owner chain and the lower dialogs.
    private HashSet<nint> ModalLevel(Window window)
    {
        HashSet<nint> level = [];
        if (Modality == NavigationDialogModality.Application) level.UnionWith(DialogModalityTable.ThreadWindows());
        else
        {
            for (var owner = _owner; owner is not null; owner = owner.Owner) level.Add(HandleOf(owner));
            foreach (var dialog in _windows) level.Add(HandleOf(dialog.Window));
        }
        level.Remove(HandleOf(window));
        level.Remove(0);
        return level;
    }

    private static nint HandleOf(Window window) => new WindowInteropHelper(window).Handle;

    private sealed class DialogWindow(NavigationEntryId id, Window window)
    {
        public NavigationEntryId Id { get; } = id;
        public Window Window { get; } = window;
        public bool HostClosing { get; set; }
        public HashSet<nint> Level { get; set; } = [];
    }

    private sealed class EscapeCommand(NavigationDialogHost host, NavigationEntryId id) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => host.CloseOnEscape;

        public void Execute(object? parameter)
        {
            if (host.CloseOnEscape) host.PostDismiss(id);
        }
    }
}

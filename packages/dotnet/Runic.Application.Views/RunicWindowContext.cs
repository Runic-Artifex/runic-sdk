using System.ComponentModel;
using Runic.Navigation;

namespace Runic.Application.Views;

/// <summary>
/// The Window whose DI scope resolved the service, for a ViewModel that closes its own Window. Take it in a
/// constructor and close from a command:
/// <code>
/// [RelayCommand]
/// private async Task QuitAsync() =&gt; await _window.RequestCloseAsync();
/// </code>
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope.
/// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> binds it to the Window it opens before it resolves
/// the Window's ViewModel, on every host, so the ViewModel and the services it shares that scope with need no holder
/// of the Window. <see cref="IRunicWindowService"/> opens other Windows; this service acts on the caller's own. In
/// tests, register a fake that records the request and returns the answer the test needs.
/// </remarks>
public interface IRunicWindowContext
{
    /// <summary>
    /// Asks to close this Window as its close button does, and returns whether it closed, as
    /// <see cref="RunicWindow{TViewModel}.RequestCloseAsync"/> does: with <see cref="RunicWindow{TViewModel}.GuardClose"/>
    /// it first asks <c>CanCloseAsync</c> and the navigation guards, and returns <see langword="false"/> with the
    /// Window open when they keep it.
    /// </summary>
    /// <param name="cancellationToken">Stops this caller's wait; the decision continues for other requests.</param>
    /// <returns><see langword="true"/> when the Window closed; <see langword="false"/> when it stays open.</returns>
    /// <remarks>
    /// In the scope of a window the application opened itself, which <c>DesktopPlatformServices.AttachWindowServices</c>
    /// attached (Runic.Application.Views.Desktop), it asks the window's own close confirmation as its close button
    /// does, and <see cref="Window"/> stays <see langword="null"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The service was resolved outside the scope of a Window that <c>OpenWindowAsync</c> opened or of an attached
    /// window, the Window has not opened yet, for example when the ViewModel's constructor calls it, or the attached
    /// window closed.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The Window of this scope, for example as the <see cref="RunicWindowOptions.Owner"/> of a dialog it opens, or
    /// <see langword="null"/> outside a Window's scope.
    /// </summary>
    /// <remarks>
    /// It is set before the ViewModel is resolved, so a constructor may keep it, but the Window opens only after the
    /// constructor returned. The default implementation, for a test fake, returns <see langword="null"/>.
    /// </remarks>
    IRunicWindow? Window => null;
}

/// <summary>
/// The locale of the Window whose scope resolved this service: the language in which its pages show text references,
/// such as ViewModel status text and validation messages (feature <c>translation-text/1</c>). Take it in the constructor
/// of a ViewModel that <c>OpenWindowAsync</c> resolves, or of a scoped service that ViewModel uses.
/// </summary>
/// <remarks>
/// <para>
/// The application owns the locale: it sets it from its settings, the operating system's language or a language switch
/// of the page, which calls a command. The window's pages follow it without a call per string, and set the locale of their
/// Translations runtime, so the page's own messages switch too.
/// </para>
/// <para>
/// A Window that sets no locale of its own follows the application's <see cref="IRunicApplicationLocale"/>, and switches
/// with it. Setting <see cref="Locale"/> gives the Window its own locale; setting <see langword="null"/> makes it follow
/// the application again. While neither sets one, the default, each page keeps its own language.
/// </para>
/// <para>
/// Set it in the ViewModel's constructor, before the window opens, and the first page starts in that language. The
/// service raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for <see cref="Locale"/> each time the Window's
/// locale changes: on the setting thread when it is set here or when the window's
/// <see cref="WindowContentSession.Locale"/> is set directly, which counts as the Window's own locale; and in a turn of
/// the Window's model context when the application's locale changes while the Window follows it. It raises it for
/// <see cref="OwnLocale"/> when the Window's own locale changes. Outside a Window's scope, as in a unit test that
/// constructs the ViewModel, it keeps the value without pushing it anywhere.
/// </para>
/// <para>
/// Since 0.17, <see cref="Locale"/> returns the application's locale while the Window has none of its own. Code that
/// saves the Window's language and sets it back later saves <see cref="OwnLocale"/>, so a Window that followed the
/// application follows it again instead of keeping the language it showed.
/// </para>
/// </remarks>
public interface IRunicWindowLocale : INotifyPropertyChanged
{
    /// <summary>
    /// The window's locale, a BCP 47 tag such as <c>de</c> or <c>pt-BR</c>: its own, or the application's while it sets
    /// none. <see langword="null"/> while neither sets one and each page keeps its own.
    /// </summary>
    /// <remarks>
    /// Setting a tag gives the Window its own locale. Setting <see langword="null"/> makes it follow the application's
    /// locale again, so reading it afterwards returns the application's locale when there is one.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not 1 to 64 letters, digits and <c>-</c>, starting with a letter.</exception>
    string? Locale { get; set; }

    /// <summary>
    /// The Window's own locale, or <see langword="null"/> while it follows the application's (since 0.17). Save this one
    /// and set it back through <see cref="Locale"/>: <see langword="null"/> follows the application again.
    /// </summary>
    /// <remarks>The default implementation, for a test fake, returns <see cref="Locale"/>.</remarks>
    string? OwnLocale => Locale;
}

internal sealed class RunicWindowContext : IRunicWindowContext, IRunicWindowLocale, IDisposable
{
    private static readonly PropertyChangedEventArgs LocaleArgs = new(nameof(Locale));
    private static readonly PropertyChangedEventArgs OwnLocaleArgs = new(nameof(OwnLocale));
    private readonly object _localeGate = new();
    private readonly IRunicApplicationLocale? _application;
    private IRunicWindow? _window;
    private WindowContentSession? _content;
    // The Window's own locale; null while it follows the application.
    private string? _own;
    // The locale this context last told a session-less listener about, so it raises PropertyChanged once per change.
    private string? _reported;
    private bool _disposed;
    // The context whose Apply sets its session's locale on this thread: that change is not a direct set.
    [ThreadStatic] private static RunicWindowContext? s_applying;

    internal RunicWindowContext(IRunicApplicationLocale? application = null)
    {
        _application = application;
        if (application is RunicApplicationLocale own) _reported = own.Attach(this);
        else if (application is not null)
        {
            application.LocaleChanged += OnApplicationLocaleChanged;
            _reported = application.Locale;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // The locale the Window shows: its own, else the application's.
    private string? Target
    {
        get
        {
            lock (_localeGate) return _own ?? _application?.Locale;
        }
    }

    public string? Locale
    {
        get
        {
            lock (_localeGate) return _content is { } content ? content.Locale : _own ?? _application?.Locale;
        }
        set
        {
            if (value is not null && !BridgeDispatcher.IsLocale(value))
                throw new ArgumentException($"'{value}' is not a locale tag, such as \"de\" or \"pt-BR\".", nameof(value));
            lock (_localeGate)
            {
                if (string.Equals(_own, value, StringComparison.Ordinal)) return;
                _own = value;
            }
            Apply();
            PropertyChanged?.Invoke(this, OwnLocaleArgs);
        }
    }

    public string? OwnLocale
    {
        get
        {
            lock (_localeGate) return _own;
        }
    }

    // Called when the Window attached its content session: the session takes the locale set before the window opened,
    // or the application's, and its changes raise PropertyChanged from then on.
    internal void AttachContent(WindowContentSession content)
    {
        lock (_localeGate)
        {
            if (_content is not null) return;
            _content = content;
            // A locale the session was given directly before it attached is the Window's own.
            _own ??= content.Locale;
        }
        content.LocaleChanged += OnContentLocaleChanged;
        Apply();
    }

    // The application's switch reaches this Window: in a turn of the Window's model context, as other changes reach its
    // ViewModel, or inline without one. A retry raises PropertyChanged again although the session already shows the
    // locale, because the last update failed in a handler.
    internal ValueTask FollowApplicationAsync(RunicApplicationLocale owner, bool retry)
    {
        WindowContentSession? content;
        lock (_localeGate) content = _content;
        if (content?.ModelContext is not { IsExecuting: false } context)
        {
            try
            {
                RunicApplicationLocale.UpdateWindow(owner, () => Apply(retry));
                return ValueTask.CompletedTask;
            }
            catch (Exception error) { return ValueTask.FromException(error); }
        }
        return FollowInTurnAsync(context, owner, retry);
    }

    private async ValueTask FollowInTurnAsync(IRunicModelContext context, RunicApplicationLocale owner, bool retry)
    {
        var started = 0;
        try
        {
            await context.InvokeAsync(() =>
            {
                Volatile.Write(ref started, 1);
                RunicApplicationLocale.UpdateWindow(owner, () => Apply(retry));
            }).ConfigureAwait(false);
        }
        // A Window whose context closed is closing: its session still takes the locale, so it reads consistently.
        catch (ObjectDisposedException) when (Volatile.Read(ref started) == 0)
        {
            RunicApplicationLocale.UpdateWindow(owner, () => Apply(retry));
        }
    }

    // Another IRunicApplicationLocale's LocaleChanged: follow in a turn of the Window's model context when there is one.
    private void OnApplicationLocaleChanged(object? sender, RunicApplicationLocaleChangedEventArgs e)
    {
        WindowContentSession? content;
        lock (_localeGate) content = _content;
        if (content?.ModelContext is { IsExecuting: false } context && context.TryPost(() => Apply())) return;
        Apply();
    }

    // Brings the session, or the listeners of a Window without one, to the locale the Window shows. Two threads that apply
    // at once converge: each reads the target again after it set the session. With force, PropertyChanged is raised also
    // when the locale is unchanged.
    private void Apply(bool force = false)
    {
        WindowContentSession? content;
        lock (_localeGate) content = _content;
        if (content is null)
        {
            string? target;
            lock (_localeGate)
            {
                target = _own ?? _application?.Locale;
                if (!force && string.Equals(_reported, target, StringComparison.Ordinal)) return;
                _reported = target;
            }
            PropertyChanged?.Invoke(this, LocaleArgs);
            return;
        }
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var target = Target;
            if (string.Equals(content.Locale, target, StringComparison.Ordinal))
            {
                if (force) PropertyChanged?.Invoke(this, LocaleArgs);
                force = false;
            }
            else
            {
                force = false;
                // The session raises LocaleChanged, which raises PropertyChanged.
                var outer = s_applying;
                s_applying = this;
                try { content.Locale = target; }
                finally { s_applying = outer; }
            }
            if (string.Equals(Target, target, StringComparison.Ordinal)) return;
        }
    }

    // A change of the session's locale. One this context did not make, because WindowContentSession.Locale was set
    // directly, becomes the Window's own locale; null makes it follow the application again, so the session then shows
    // the application's locale, as IRunicWindowLocale.Locale documents.
    private void OnContentLocaleChanged(object? sender, EventArgs e)
    {
        if (sender is WindowContentSession content && !ReferenceEquals(s_applying, this))
        {
            var shown = content.Locale;
            bool ownChanged;
            bool follow;
            lock (_localeGate)
            {
                ownChanged = !string.Equals(_own, shown, StringComparison.Ordinal);
                _own = shown;
                follow = shown is null && _application?.Locale is not null;
            }
            // Apply sets the application's locale, whose change raises PropertyChanged for Locale.
            if (follow) Apply();
            else PropertyChanged?.Invoke(this, LocaleArgs);
            if (ownChanged) PropertyChanged?.Invoke(this, OwnLocaleArgs);
            return;
        }
        PropertyChanged?.Invoke(this, LocaleArgs);
    }

    public void Dispose()
    {
        lock (_localeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_application is RunicApplicationLocale own) own.Detach(this);
        else if (_application is not null) _application.LocaleChanged -= OnApplicationLocaleChanged;
    }
    // The presentation of a scope that no Window opened: it reports OwnerUnavailable.
    private RunicWindowPresentation? _unbound;

    // IRunicWindowPresentation of this scope: the Window's, once OpenWindowAsync bound it.
    internal IRunicWindowPresentation Presentation =>
        Volatile.Read(ref _window)?.Presentation
        ?? Volatile.Read(ref _unbound)
        ?? Interlocked.CompareExchange(ref _unbound, new RunicWindowPresentation(), null)
        ?? _unbound!;

    // Called by OpenWindowAsync in the Window's new scope, before it resolves the ViewModel.
    internal void Bind(IRunicWindow window)
    {
        lock (_externalGate)
        {
            if (_external is not null || _externalEnded)
                throw new InvalidOperationException("The window context is already attached to a window the application opened.");
            if (Interlocked.CompareExchange(ref _window, window, null) is { } bound && !ReferenceEquals(bound, window))
                throw new InvalidOperationException("The window context is already bound to another Window.");
        }
    }

    // A window the application opened itself, without an IRunicWindow (DesktopPlatformServices.AttachWindowServices):
    // its close request, while it is attached.
    private readonly object _externalGate = new();
    private Func<CancellationToken, ValueTask<bool>>? _external;
    private bool _externalEnded;
    private WindowContentSession? _externalContent;

    // Attaches this scope to a window that no OpenWindowAsync opened: the scope's presentation forwards to presentation
    // and raises its changes in the content session's model turns, the locale follows content, and RequestCloseAsync
    // calls requestClose. Window stays null. Returns the scope's presentation, which DetachExternal closes. Throws when
    // the scope is bound to a Window or was attached before.
    internal RunicWindowPresentation AttachExternal(IRunicWindowPresentation presentation, WindowContentSession? content,
        Func<CancellationToken, ValueTask<bool>> requestClose)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(requestClose);
        lock (_externalGate)
        {
            if (Volatile.Read(ref _window) is not null)
                throw new InvalidOperationException("The window context is already bound to a Window that OpenWindowAsync opened.");
            if (_external is not null || _externalEnded)
                throw new InvalidOperationException("The window context is already attached to a window.");
            _external = requestClose;
            _externalContent = content;
        }
        var own = (RunicWindowPresentation)Presentation;
        own.Attach(presentation, () => content?.ModelContext, () => content?.Logger ?? TraceFallbackLogger.Instance);
        if (content is not null) AttachContent(content);
        return own;
    }

    // The window closed or the application detached it: the presentation reports OwnerClosed, the locale no longer
    // follows the session, and RequestCloseAsync throws. The scope cannot be attached again.
    internal void DetachExternal()
    {
        WindowContentSession? content;
        lock (_externalGate)
        {
            if (_external is null || _externalEnded) return;
            _externalEnded = true;
            _external = null;
            content = _externalContent;
            _externalContent = null;
        }
        (Volatile.Read(ref _unbound))?.Close();
        if (content is null) return;
        content.LocaleChanged -= OnContentLocaleChanged;
        lock (_localeGate)
        {
            if (ReferenceEquals(_content, content)) _content = null;
        }
    }

    public IRunicWindow? Window => Volatile.Read(ref _window);

    public ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _window) is { } window) return window.RequestCloseAsync(cancellationToken);
        Func<CancellationToken, ValueTask<bool>>? external;
        bool ended;
        lock (_externalGate) (external, ended) = (_external, _externalEnded);
        if (external is not null) return external(cancellationToken);
        if (ended)
            throw new InvalidOperationException("The window this scope was attached to has closed or was detached (AttachWindowServices).");
        throw new InvalidOperationException(
            "IRunicWindowContext was resolved outside a Window's scope, so it has no Window to close. Take it in the " +
            "constructor of a ViewModel that OpenWindowAsync resolves, or of a scoped service that ViewModel uses, or " +
            "attach the scope to a window the application opened (DesktopPlatformServices.AttachWindowServices).");
    }
}

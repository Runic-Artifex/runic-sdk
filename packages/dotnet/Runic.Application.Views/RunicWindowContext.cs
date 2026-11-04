using System.ComponentModel;

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
    /// <exception cref="InvalidOperationException">
    /// The service was resolved outside the scope of a Window that <c>OpenWindowAsync</c> opened, or the Window has
    /// not opened yet, for example when the ViewModel's constructor calls it.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default);
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
/// Translations runtime, so the page's own messages switch too. <see langword="null"/>, the default, leaves each page in
/// its own language.
/// </para>
/// <para>
/// Set it in the ViewModel's constructor, before the window opens, and the first page starts in that language. The
/// service raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for <see cref="Locale"/> on the setting thread,
/// also when the window's <see cref="WindowContentSession.Locale"/> is set directly. Outside a Window's scope, as in a
/// unit test that constructs the ViewModel, it keeps the value without pushing it anywhere.
/// </para>
/// </remarks>
public interface IRunicWindowLocale : INotifyPropertyChanged
{
    /// <summary>
    /// The window's locale, a BCP 47 tag such as <c>de</c> or <c>pt-BR</c>, or <see langword="null"/> while each page
    /// keeps its own.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not 1 to 64 letters, digits and <c>-</c>, starting with a letter.</exception>
    string? Locale { get; set; }
}

internal sealed class RunicWindowContext : IRunicWindowContext, IRunicWindowLocale
{
    private static readonly PropertyChangedEventArgs LocaleArgs = new(nameof(Locale));
    private readonly object _localeGate = new();
    private IRunicWindow? _window;
    private WindowContentSession? _content;
    private string? _locale;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Locale
    {
        get
        {
            lock (_localeGate) return _content is { } content ? content.Locale : _locale;
        }
        set
        {
            WindowContentSession? content;
            lock (_localeGate)
            {
                content = _content;
                if (content is null)
                {
                    if (value is not null && !BridgeDispatcher.IsLocale(value))
                        throw new ArgumentException($"'{value}' is not a locale tag, such as \"de\" or \"pt-BR\".", nameof(value));
                    if (string.Equals(_locale, value, StringComparison.Ordinal)) return;
                    _locale = value;
                }
            }
            // The session raises LocaleChanged, which raises PropertyChanged.
            if (content is not null) content.Locale = value;
            else PropertyChanged?.Invoke(this, LocaleArgs);
        }
    }

    // Called when the Window attached its content session: the session takes the locale set before the window opened,
    // and its changes raise PropertyChanged from then on.
    internal void AttachContent(WindowContentSession content)
    {
        string? locale;
        lock (_localeGate)
        {
            if (_content is not null) return;
            _content = content;
            locale = _locale;
        }
        if (locale is not null) content.Locale = locale;
        content.LocaleChanged += (_, _) => PropertyChanged?.Invoke(this, LocaleArgs);
    }

    // Called by OpenWindowAsync in the Window's new scope, before it resolves the ViewModel.
    internal void Bind(IRunicWindow window)
    {
        if (Interlocked.CompareExchange(ref _window, window, null) is { } bound && !ReferenceEquals(bound, window))
            throw new InvalidOperationException("The window context is already bound to another Window.");
    }

    public ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        var window = Volatile.Read(ref _window) ?? throw new InvalidOperationException(
            "IRunicWindowContext was resolved outside a Window's scope, so it has no Window to close. Take it in the " +
            "constructor of a ViewModel that OpenWindowAsync resolves, or of a scoped service that ViewModel uses.");
        return window.RequestCloseAsync(cancellationToken);
    }
}

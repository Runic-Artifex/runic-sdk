namespace Runic.Application.Views;

/// <summary>
/// The application's locale: the language that every Window shows unless it sets its own with
/// <see cref="IRunicWindowLocale"/>, and that menu bars, status icons and notifications are built in. One
/// <see cref="SetLocaleAsync"/> reaches every open Window and every Window opened later.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it as a singleton. Its locale is
/// <see langword="null"/> until the application sets one, and each page then keeps its own language, as before
/// application locales existed.
/// </para>
/// <para>
/// A switch runs in this order: the followers that <see cref="Follow"/> registered, such as the application's
/// Translations context, prepare the new locale; then the locale changes, every Window that sets no locale of its own
/// follows it (its pages switch, and its <see cref="IRunicWindowLocale"/> raises <c>PropertyChanged</c>); then
/// <see cref="LocaleChanged"/> is raised, so a handler that rebuilds the menu bar or a status icon reads text that is
/// already in the new language. A typical start:
/// </para>
/// <code>
/// var locale = services.GetRequiredService&lt;IRunicApplicationLocale&gt;();
/// locale.Follow(translations.SetLocaleAsync);           // a Runic Translations TranslationContext
/// locale.LocaleChanged += (_, _) =&gt; RebuildMenuBar();
/// await locale.SetLocaleAsync(settings.Language ?? Negotiate(DesktopLanguages.Preferred));
/// </code>
/// <para>
/// Applications and tests may implement this interface. A minor release adds members to it only with a default
/// implementation.
/// </para>
/// </remarks>
public interface IRunicApplicationLocale
{
    /// <summary>
    /// The application's locale, a BCP 47 tag such as <c>de</c> or <c>pt-BR</c>, or <see langword="null"/> while the
    /// application sets none.
    /// </summary>
    string? Locale { get; }

    /// <summary>
    /// Changes the application's locale: its followers first, then every Window without a locale of its own, then
    /// <see cref="LocaleChanged"/>. Switches run one at a time, in the order they were requested; setting the current
    /// locale does nothing.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> calls no follower, which keeps its locale, and tells the Windows that follow the
    /// application that it sets none: their pages keep the language they show. A follower that throws stops the switch:
    /// the locale, the Windows and the followers after it keep the old locale, followers before it keep the new one, and
    /// the exception is rethrown. An exception of a <see cref="LocaleChanged"/> handler is rethrown after every handler
    /// ran; the locale has changed.
    /// </remarks>
    /// <param name="locale">The new locale, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting for an earlier switch, and is passed to the followers.</param>
    /// <exception cref="ArgumentException">The value is not 1 to 64 letters, digits and <c>-</c>, starting with a letter.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the locale changed.</exception>
    ValueTask SetLocaleAsync(string? locale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a follower that prepares each new locale before the Windows and <see cref="LocaleChanged"/> see it, such
    /// as <c>TranslationContext.SetLocaleAsync</c> of Runic Translations: <c>locale.Follow(context.SetLocaleAsync)</c>.
    /// </summary>
    /// <remarks>
    /// Followers run in the order they were registered, each awaited before the next, and only for a locale that is not
    /// <see langword="null"/>. Registering one does not call it: register it before the first
    /// <see cref="SetLocaleAsync"/>, or set its locale yourself.
    /// </remarks>
    /// <param name="follower">Receives the new locale and the switch's cancellation token.</param>
    /// <returns>A registration; dispose it to stop following.</returns>
    IDisposable Follow(Func<string, CancellationToken, ValueTask> follower);

    /// <summary>
    /// Raised after the locale changed and the Windows followed it, on the thread that completed the switch. Rebuild the
    /// application's own text here: the menu bar, status icons and notifications.
    /// </summary>
    event EventHandler<RunicApplicationLocaleChangedEventArgs>? LocaleChanged;
}

/// <summary>The change that <see cref="IRunicApplicationLocale.LocaleChanged"/> reports.</summary>
/// <param name="oldLocale">The locale before the change.</param>
/// <param name="locale">The new locale.</param>
public sealed class RunicApplicationLocaleChangedEventArgs(string? oldLocale, string? locale) : EventArgs
{
    /// <summary>The locale before the change, or <see langword="null"/>.</summary>
    public string? OldLocale { get; } = oldLocale;

    /// <summary>The new locale, or <see langword="null"/> when the application sets none.</summary>
    public string? Locale { get; } = locale;
}

// The IRunicApplicationLocale that AddRunicWindowService registers. Window contexts follow it through Attach rather than
// the public event, so every Window has followed before the application's own handlers run.
internal sealed class RunicApplicationLocale : IRunicApplicationLocale, IDisposable
{
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _gate = new();
    private Func<string, CancellationToken, ValueTask>[] _followers = [];
    private RunicWindowContext[] _windows = [];
    private string? _locale;

    public string? Locale => Volatile.Read(ref _locale);

    public event EventHandler<RunicApplicationLocaleChangedEventArgs>? LocaleChanged;

    public async ValueTask SetLocaleAsync(string? locale, CancellationToken cancellationToken = default)
    {
        if (locale is not null && !BridgeDispatcher.IsLocale(locale))
            throw new ArgumentException($"'{locale}' is not a locale tag, such as \"de\" or \"pt-BR\".", nameof(locale));
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        RunicApplicationLocaleChangedEventArgs args;
        try
        {
            var old = Volatile.Read(ref _locale);
            if (string.Equals(old, locale, StringComparison.Ordinal)) return;
            if (locale is not null)
                foreach (var follower in Volatile.Read(ref _followers))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await follower(locale, cancellationToken).ConfigureAwait(false);
                }
            RunicWindowContext[] windows;
            lock (_gate)
            {
                Volatile.Write(ref _locale, locale);
                windows = _windows;
            }
            foreach (var window in windows) window.ApplicationLocaleChanged();
            args = new RunicApplicationLocaleChangedEventArgs(old, locale);
        }
        finally
        {
            _switching.Release();
        }
        RaiseLocaleChanged(args);
    }

    private void RaiseLocaleChanged(RunicApplicationLocaleChangedEventArgs args)
    {
        if (LocaleChanged is not { } handlers) return;
        List<Exception>? errors = null;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((EventHandler<RunicApplicationLocaleChangedEventArgs>)handler)(this, args); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is [var single]) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(single);
        if (errors is not null) throw new AggregateException(errors);
    }

    public IDisposable Follow(Func<string, CancellationToken, ValueTask> follower)
    {
        ArgumentNullException.ThrowIfNull(follower);
        lock (_gate) Volatile.Write(ref _followers, [.. _followers, follower]);
        return new Registration(this, follower);
    }

    // A Window context that follows the application from now on; returns the locale it follows. Under the gate, so a
    // switch that races it either reaches it or is the locale it returns.
    internal string? Attach(RunicWindowContext window)
    {
        lock (_gate)
        {
            if (!_windows.Contains(window)) _windows = [.. _windows, window];
            return _locale;
        }
    }

    internal void Detach(RunicWindowContext window)
    {
        lock (_gate) _windows = [.. _windows.Where(item => !ReferenceEquals(item, window))];
    }

    // The service provider disposes the singleton with the application; a switch after that throws ObjectDisposedException.
    public void Dispose() => _switching.Dispose();

    private sealed class Registration(RunicApplicationLocale owner, Func<string, CancellationToken, ValueTask> follower) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
            {
                var followers = owner._followers.ToList();
                followers.Remove(follower);
                Volatile.Write(ref owner._followers, [.. followers]);
            }
        }
    }
}

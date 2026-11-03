using System.Text.Json;
using Runic.Application.Views.Generated;

namespace Runic.Application.Views;

/// <summary>
/// Where a <see cref="ViewRequest{T}"/> goes (Views protocol 4, feature <c>view-requests/1</c>, "View requests").
/// </summary>
/// <remarks>
/// <see cref="Drop"/> and <see cref="NextMounted"/> select one presentation by the scope of the code that raises the
/// request: inside a command that a page invoked, the presentation of that page that handles the request; in the
/// window scope (a close decision, a menu-bar command, <see cref="RunicInteractionInvocation.EnterWindow"/>) or outside
/// any scope, the one presentation of the window that handles it.
/// </remarks>
public enum ViewRequestDelivery
{
    /// <summary>Sends the request to the selected presentation, and drops it when there is none, or several.</summary>
    Drop = 0,

    /// <summary>
    /// Sends the request to the selected presentation; when there is none, or several, keeps the latest request and
    /// sends it once to the next presentation of the window that mounts a handler for it.
    /// </summary>
    NextMounted = 1,

    /// <summary>
    /// Sends the request to every presentation of the window that handles it, on every connection of the window, also
    /// when a command raised it: a window-wide announcement, such as a toast in every pane.
    /// </summary>
    Broadcast = 2,
}

/// <summary>
/// A one-way request of a ViewModel to its web View, such as to focus a field, scroll a row into view, scroll to the
/// top or show a toast. The ViewModel raises it without waiting for an answer; the page handles it with the generated
/// <c>client.requests.&lt;name&gt;.handle((input, { signal }) =&gt; …)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Expose it as a get-only property; the generated Bridge sends it to the pages that present the ViewModel, in both
/// MVVM flavors (ReactiveUI and CommunityToolkit):
/// </para>
/// <code>
/// public ViewRequest&lt;FocusTarget&gt; Focus { get; } = new();
/// public ViewRequest&lt;Toast&gt; Notify { get; } = new(ViewRequestDelivery.Broadcast);
///
/// private void Add() { Items.Add(item); Focus.Raise(new FocusTarget(item.Id)); }
/// </code>
/// <para>
/// A request is not state: a snapshot or a reconnect never repeats it, and it never reaches another window.
/// <see cref="ViewRequestDelivery.Drop"/> and <see cref="ViewRequestDelivery.NextMounted"/> requests that a command
/// raises reach only the invoking page's connection; a <see cref="ViewRequestDelivery.Broadcast"/> reaches every
/// connection of the window. The page handles it once it has rendered the state that includes everything the ViewModel changed before
/// raising it, so a handler finds the new row in the page. A page or host of an earlier release ignores requests.
/// </para>
/// </remarks>
/// <typeparam name="T">The request's input, a bridge value; use an empty record for a request without data.</typeparam>
public sealed class ViewRequest<T>
{
    private readonly object _gate = new();
    private readonly List<ViewRequestBinding<T>> _bindings = [];
    // NextMounted: a request raised while no route of any window presented the ViewModel, kept for the first window that
    // presents it. It holds no window: a request kept for a window is the window's (BridgeInteractionRouter).
    private (T Input, bool Kept) _unattached;

    /// <summary>Creates a view request.</summary>
    /// <param name="delivery">Where the request goes; by default <see cref="ViewRequestDelivery.Drop"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delivery"/> is not a defined value.</exception>
    public ViewRequest(ViewRequestDelivery delivery = ViewRequestDelivery.Drop)
    {
        if (delivery is not (ViewRequestDelivery.Drop or ViewRequestDelivery.NextMounted or ViewRequestDelivery.Broadcast))
            throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "Unknown view request delivery.");
        Delivery = delivery;
    }

    /// <summary>Where the request goes.</summary>
    public ViewRequestDelivery Delivery { get; }

    /// <summary>Sends the request to the pages that present the ViewModel, by its <see cref="Delivery"/>, and returns at once.</summary>
    /// <remarks>
    /// Call it from any thread, usually in a command. What the ViewModel changed before the call is published first, so
    /// the page handles the request after it rendered those changes.
    /// </remarks>
    /// <param name="input">The request's input.</param>
    /// <exception cref="ArgumentException"><paramref name="input"/> cannot be encoded or exceeds the window's input limit.</exception>
    public void Raise(T input)
    {
        var invocation = RunicInteractionInvocation.Current;
        ViewRequestBinding<T>[] bindings;
        lock (_gate)
        {
            bindings = [.. _bindings];
            // No window presents the ViewModel yet: the first one that does receives it.
            if (bindings.Length == 0)
            {
                if (Delivery is ViewRequestDelivery.NextMounted) _unattached = (input, true);
                return;
            }
        }
        // The request belongs to the window of the invoking scope, or to the one window that presents the ViewModel.
        WindowContentSession window;
        if (invocation is not null) window = invocation.Session;
        else
        {
            var windows = bindings.Select(binding => binding.Session).Distinct().ToArray();
            if (windows.Length > 1)
            {
                ViewsLog.ViewRequestDropped(windows[0].Logger, bindings[0].Name, "several windows present the ViewModel");
                return;
            }
            window = windows[0];
        }
        var inWindow = bindings.Where(binding => ReferenceEquals(binding.Session, window)).ToArray();
        if (inWindow.Length == 0)
        {
            // The invoking window does not present the ViewModel now: a NextMounted request waits in that window, and
            // goes with it when it closes.
            if (Delivery is ViewRequestDelivery.NextMounted)
                window.Interactions.KeepViewRequest(this, bindings[0].Name, bindings[0].Contract, bindings[0].Encode(input, window), invocation);
            return;
        }
        var first = inWindow[0];
        var json = first.Encode(input, first.Session);
        lock (_gate) _unattached = default;
        first.Session.Interactions.RaiseViewRequest(this, inWindow.Select(binding => binding.Route).ToArray(), first.Name,
            first.Contract, json, Delivery, invocation);
    }

    // The generated Bridge of a route that presents the ViewModel attaches the request to its window.
    internal IDisposable Attach(ViewRequestBinding<T> binding)
    {
        // Registered first: a window that is closing throws here, with no binding added and the kept request kept.
        var lease = binding.Session.Interactions.RegisterViewRequest(this, binding.Route, binding.Name, binding.Contract);
        (T Input, bool Kept) unattached;
        lock (_gate)
        {
            _bindings.Add(binding);
            unattached = _unattached;
            _unattached = default;
        }
        if (unattached.Kept)
        {
            try
            {
                binding.Session.Interactions.RaiseViewRequest(this, [binding.Route], binding.Name, binding.Contract,
                    binding.Encode(unattached.Input, binding.Session), ViewRequestDelivery.NextMounted, invocation: null);
            }
            // An input over this window's limit is dropped, as Raise would have refused it.
            catch (ArgumentException) { }
        }
        return new Lease(this, binding, lease);
    }

    private void Detach(ViewRequestBinding<T> binding)
    {
        lock (_gate) _bindings.Remove(binding);
    }

    private sealed class Lease(ViewRequest<T> owner, ViewRequestBinding<T> binding, IDisposable registration) : IDisposable
    {
        private ViewRequest<T>? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } request) return;
            request.Detach(binding);
            registration.Dispose();
        }
    }
}

// One route of a window that presents the ViewModel of a ViewRequest.
internal sealed record ViewRequestBinding<T>(WindowContentSession Session, string Route, string Name, string Contract,
    Func<T, string> EncodeInput)
{
    // The input's wire JSON, checked against the window's input limit.
    internal string Encode(T input, WindowContentSession session)
    {
        string json;
        try { json = EncodeInput(input); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            throw new ArgumentException("The view request input cannot be encoded.", nameof(input), error);
        }
        if (!BridgeInputLimit.Fits(json, session.MaximumInputBytes))
            throw new ArgumentException("The view request input exceeds the window's input limit.", nameof(input));
        return json;
    }
}

/// <summary>
/// A question a ViewModel asks its web View and waits for, such as a confirmation, in either MVVM flavor. It is the
/// flavor-neutral counterpart of a ReactiveUI <c>Interaction&lt;TInput, TOutput&gt;</c>: on the page it is the generated
/// <c>client.interactions.&lt;name&gt;.handle((input, { signal }) =&gt; answer)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Expose it as a get-only property. <see cref="AskAsync"/> asks the page in the same scopes as an interaction: inside
/// a command that a page invoked, that page's presentation; in the window scope (a close decision, a menu-bar command,
/// <see cref="RunicInteractionInvocation.EnterWindow"/>), the one presentation of the window that handles it. When no
/// page can answer, it runs the fallback, or fails with <see cref="RunicInteractionUnhandledException"/>.
/// </para>
/// <code>
/// public ViewQuestion&lt;DiscardRequest, bool&gt; ConfirmDiscard { get; } = new((_, _) =&gt; ValueTask.FromResult(false));
///
/// [RelayCommand]
/// private async Task DiscardAsync(CancellationToken token)
/// {
///     if (await ConfirmDiscard.AskAsync(new DiscardRequest(Title), token)) Reset();
/// }
/// </code>
/// </remarks>
/// <typeparam name="TInput">The question, a bridge value.</typeparam>
/// <typeparam name="TOutput">The answer, a bridge value.</typeparam>
public sealed class ViewQuestion<TInput, TOutput>
{
    private readonly object _gate = new();
    private readonly List<ViewQuestionBinding<TInput, TOutput>> _bindings = [];
    private readonly Func<TInput, CancellationToken, ValueTask<TOutput>>? _fallback;

    /// <summary>Creates a view question.</summary>
    /// <param name="fallback">Answers when no page can; without one, an unanswered question fails.</param>
    /// <param name="timeout">How long a page may take to answer; at most ten minutes, by default two.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or exceeds ten minutes.</exception>
    public ViewQuestion(Func<TInput, CancellationToken, ValueTask<TOutput>>? fallback = null, TimeSpan? timeout = null)
    {
        if (timeout is { } value && (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10)))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Interaction timeouts must be between zero and ten minutes.");
        _fallback = fallback;
        Timeout = timeout;
    }

    /// <summary>How long a page may take to answer, or <see langword="null"/> for the default of two minutes.</summary>
    public TimeSpan? Timeout { get; }

    /// <summary>Asks the page and returns its answer, or the fallback's when no page can answer.</summary>
    /// <param name="input">The question.</param>
    /// <param name="cancellationToken">Cancels the question; the page's handler is aborted.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="RunicInteractionUnhandledException">No page can answer and there is no fallback.</exception>
    /// <exception cref="RunicInteractionCancelledException">The page cancelled, or its presentation closed.</exception>
    /// <exception cref="RunicInteractionTimeoutException">The page did not answer in time.</exception>
    /// <exception cref="RunicInteractionOutputException">The page's answer does not match the contract.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<TOutput> AskAsync(TInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RunicInteractionInvocation.Current is { } invocation)
        {
            ViewQuestionBinding<TInput, TOutput>[] bindings;
            lock (_gate) bindings = [.. _bindings.Where(binding => ReferenceEquals(binding.Session, invocation.Session))];
            // The router selects among every route of the window that presents the ViewModel, so the window scope asks
            // only when exactly one presentation of the window handles the question.
            foreach (var group in bindings.GroupBy(binding => (binding.Name, binding.Contract)))
            {
                var binding = group.First();
                var request = binding.Session.Interactions.TryRequest(group.Select(member => member.Route).ToArray(), binding.Name,
                    binding.Contract, input, binding.EncodeInput, binding.DecodeOutput, Timeout, cancellationToken);
                if (request is not null) return await request.ConfigureAwait(false);
            }
        }
        if (_fallback is { } fallback) return await fallback(input, cancellationToken).ConfigureAwait(false);
        throw new RunicInteractionUnhandledException("No page handles the question, and it has no fallback.");
    }

    internal IDisposable Attach(ViewQuestionBinding<TInput, TOutput> binding)
    {
        var lease = binding.Session.Interactions.Register(binding.Route, binding.Name, binding.Contract, binding.EncodeInput,
            binding.DecodeOutput, timeout: Timeout);
        lock (_gate) _bindings.Add(binding);
        return new Lease(this, binding, lease);
    }

    private sealed class Lease(ViewQuestion<TInput, TOutput> owner, ViewQuestionBinding<TInput, TOutput> binding, IDisposable registration)
        : IDisposable
    {
        private ViewQuestion<TInput, TOutput>? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } question) return;
            lock (question._gate) question._bindings.Remove(binding);
            registration.Dispose();
        }
    }
}

internal sealed record ViewQuestionBinding<TInput, TOutput>(WindowContentSession Session, string Route, string Name,
    string Contract, Func<TInput, string> EncodeInput, Func<JsonElement, TOutput> DecodeOutput);

/// <summary>Raised when no page can answer a <see cref="ViewQuestion{TInput, TOutput}"/> that has no fallback.</summary>
public sealed class RunicInteractionUnhandledException : InvalidOperationException
{
    /// <summary>Creates the exception with a default message.</summary>
    public RunicInteractionUnhandledException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RunicInteractionUnhandledException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the exception that caused it.</summary>
    public RunicInteractionUnhandledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

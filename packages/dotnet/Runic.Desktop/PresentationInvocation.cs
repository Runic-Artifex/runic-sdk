using System.Globalization;
using System.Text;

namespace Runic.Desktop;

/// <summary>Identifies a low-level presentation event delivered to a registered capability.</summary>
public enum PresentationEventKind
{
    /// <summary>A presentation session disconnected from the surface.</summary>
    Disconnected,

    /// <summary>A presentation session authenticated and connected to the surface.</summary>
    Connected,

    /// <summary>The page reported a click on an element; <see cref="PresentationInvocation.Capability"/> names the element.</summary>
    Click,

    /// <summary>The page reported a navigation; the first argument is the target URL.</summary>
    Navigation,

    /// <summary>The page invoked a registered capability with arguments and awaits its result.</summary>
    Invocation,
}

/// <summary>Handles one presentation capability invocation.</summary>
public delegate ValueTask<PresentationResult> PresentationCapabilityHandler(
    PresentationInvocation invocation,
    CancellationToken cancellationToken);

/// <summary>Represents one active presentation invocation.</summary>
/// <remarks>
/// The invocation is active only while its handler runs. After the handler returns or its task completes,
/// reading arguments and the session operations on this object throw <see cref="ObjectDisposedException"/>;
/// keep <see cref="Session"/> to address the session later.
/// </remarks>
public sealed class PresentationInvocation
{
    private readonly WebUiEvent _event;

    internal PresentationInvocation(DesktopSurface surface, WebUiEvent webUiEvent)
    {
        Surface = surface;
        _event = webUiEvent;
        Session = new PresentationSession(webUiEvent.Window, webUiEvent.SessionIdentifier, webUiEvent.ConnectionId);
        CorrelationId = Guid.NewGuid().ToString("N");
    }

    /// <summary>Gets the surface that received the invocation.</summary>
    public DesktopSurface Surface { get; }

    /// <summary>Gets the presentation session that raised the invocation.</summary>
    public PresentationSession Session { get; }

    /// <summary>Gets the kind of event being delivered.</summary>
    public PresentationEventKind Kind => (PresentationEventKind)_event.EventType;

    /// <summary>Gets the capability or element name the event was dispatched to; empty for connection and navigation events.</summary>
    public string Capability => _event.Element;

    /// <summary>Gets the event number, taken from one counter per surface that all of its sessions share.</summary>
    public ulong InvocationId => _event.EventNumber;

    /// <summary>Gets the opaque identity shared with any redacted diagnostic for this invocation.</summary>
    public string CorrelationId { get; }

    /// <summary>Gets the opaque identifier of the raising session; the same value as <see cref="PresentationSession.Id"/>.</summary>
    public ulong SessionId => _event.ConnectionId;

    /// <summary>Gets the number of arguments supplied by the page.</summary>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    public int ArgumentCount => checked((int)_event.ArgumentCount);

    /// <summary>Gets the raw bytes of an argument.</summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <returns>The argument bytes as sent by the page.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not less than <see cref="ArgumentCount"/>.</exception>
    /// <exception cref="OverflowException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    public ReadOnlyMemory<byte> GetBytes(int index = 0) => _event.GetMemory(checked((nuint)index));

    /// <summary>Gets an argument decoded as UTF-8 text.</summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <returns>The decoded argument.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not less than <see cref="ArgumentCount"/>.</exception>
    /// <exception cref="OverflowException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    public string GetString(int index = 0) => Encoding.UTF8.GetString(GetBytes(index).Span);

    /// <summary>Gets an argument parsed as a signed 64-bit integer using the invariant culture.</summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <returns>The parsed argument.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not less than <see cref="ArgumentCount"/>.</exception>
    /// <exception cref="FormatException">The argument is not an integer.</exception>
    /// <exception cref="OverflowException"><paramref name="index"/> is negative, or the argument is outside the <see cref="long"/> range.</exception>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    public long GetInt64(int index = 0) =>
        long.Parse(GetString(index), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>Gets an argument parsed as a double-precision number using the invariant culture.</summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <returns>The parsed argument.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not less than <see cref="ArgumentCount"/>.</exception>
    /// <exception cref="OverflowException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="FormatException">The argument is not a number.</exception>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    public double GetDouble(int index = 0) =>
        double.Parse(GetString(index), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Gets an argument as a Boolean, accepting <c>1</c>, <c>0</c>, <c>true</c> and <c>false</c> (case-insensitive).</summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <returns>The parsed argument.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not less than <see cref="ArgumentCount"/>.</exception>
    /// <exception cref="OverflowException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="FormatException">The argument is not a Boolean value.</exception>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    public bool GetBoolean(int index = 0) => _event.GetBoolean(checked((nuint)index));

    /// <summary>Runs JavaScript in the raising session only.</summary>
    /// <param name="script">The script to run.</param>
    /// <param name="cancellationToken">Cancels sending the script.</param>
    /// <returns>A task that completes when the script has been sent.</returns>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    /// <exception cref="InvalidOperationException">The session is no longer connected.</exception>
    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _event.RunJavaScriptAsync(script, cancellationToken);

    /// <summary>Navigates the raising session only.</summary>
    /// <param name="url">The URL to navigate to.</param>
    /// <param name="cancellationToken">Cancels sending the navigation.</param>
    /// <returns>A task that completes when the navigation has been sent.</returns>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    /// <exception cref="InvalidOperationException">The session is no longer connected.</exception>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        _event.NavigateAsync(url, cancellationToken);

    /// <summary>Sends opaque bytes to a page function in the raising session only.</summary>
    /// <param name="function">The name of the page function that receives the bytes.</param>
    /// <param name="data">The opaque payload.</param>
    /// <param name="cancellationToken">Cancels sending the payload.</param>
    /// <returns>A task that completes when the payload has been sent.</returns>
    /// <remarks>An empty payload sends nothing.</remarks>
    /// <exception cref="ArgumentException"><paramref name="function"/> is <see langword="null"/>, empty or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    /// <exception cref="InvalidOperationException">The session is no longer connected.</exception>
    public Task SendAsync(
        string function,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        _event.SendAsync(function, data, cancellationToken);

    /// <summary>Closes the raising session.</summary>
    /// <param name="cancellationToken">Cancels the close request.</param>
    /// <returns>A task that completes when the session close has been requested.</returns>
    /// <exception cref="ObjectDisposedException">The invocation is no longer active.</exception>
    /// <exception cref="InvalidOperationException">The session is no longer connected.</exception>
    public Task CloseSessionAsync(CancellationToken cancellationToken = default) =>
        _event.CloseSessionAsync(cancellationToken);
}

/// <summary>Represents one authenticated presentation transport session.</summary>
public sealed class PresentationSession
{
    private readonly WebUiWindow _engine;
    private readonly Guid _sessionId;

    internal PresentationSession(WebUiWindow engine, Guid sessionId, nuint connectionId)
    {
        _engine = engine;
        _sessionId = sessionId;
        Id = connectionId;
    }

    /// <summary>Gets the opaque session identifier for diagnostics.</summary>
    public ulong Id { get; }

    /// <summary>Runs JavaScript in this presentation session.</summary>
    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _engine.RunJavaScriptForSessionAsync(_sessionId, script, cancellationToken);

    /// <summary>Navigates this presentation session.</summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        _engine.NavigateSessionAsync(_sessionId, url, cancellationToken);

    /// <summary>Sends opaque bytes to this presentation session.</summary>
    public Task SendAsync(
        string function,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        _engine.SendRawToSessionAsync(_sessionId, function, data, cancellationToken);

    /// <summary>Closes this presentation session.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        _engine.CloseSessionAsync(_sessionId, cancellationToken);
}

/// <summary>Represents a primitive result returned to a presentation caller.</summary>
public readonly struct PresentationResult
{
    private readonly WebUiResult _value;

    private PresentationResult(WebUiResult value) => _value = value;

    /// <summary>Gets a result that returns no value to the caller.</summary>
    public static PresentationResult None { get; } = new(WebUiResult.None);

    /// <summary>Creates a signed 64-bit integer result.</summary>
    /// <param name="value">The value returned to the caller.</param>
    /// <returns>The result.</returns>
    public static PresentationResult FromInt64(long value) => new(WebUiResult.FromInt64(value));

    /// <summary>Creates a double-precision floating-point result.</summary>
    /// <param name="value">The value returned to the caller.</param>
    /// <returns>The result.</returns>
    public static PresentationResult FromDouble(double value) => new(WebUiResult.FromDouble(value));

    /// <summary>Creates a Boolean result.</summary>
    /// <param name="value">The value returned to the caller.</param>
    /// <returns>The result.</returns>
    public static PresentationResult FromBoolean(bool value) => new(WebUiResult.FromBoolean(value));

    /// <summary>Creates a string result; <see langword="null"/> returns an empty string.</summary>
    /// <param name="value">The value returned to the caller.</param>
    /// <returns>The result.</returns>
    public static PresentationResult FromString(string? value) => new(WebUiResult.FromString(value));

    /// <summary>Converts a string to a result with <see cref="FromString"/>.</summary>
    /// <param name="value">The value returned to the caller.</param>
    public static implicit operator PresentationResult(string value) => FromString(value);

    /// <summary>Converts a signed 64-bit integer to a result with <see cref="FromInt64"/>.</summary>
    /// <param name="value">The value returned to the caller.</param>
    public static implicit operator PresentationResult(long value) => FromInt64(value);

    /// <summary>Converts a double-precision number to a result with <see cref="FromDouble"/>.</summary>
    /// <param name="value">The value returned to the caller.</param>
    public static implicit operator PresentationResult(double value) => FromDouble(value);

    /// <summary>Converts a Boolean to a result with <see cref="FromBoolean"/>.</summary>
    /// <param name="value">The value returned to the caller.</param>
    public static implicit operator PresentationResult(bool value) => FromBoolean(value);

    internal WebUiResult ToCompatibilityResult() => _value;
}

/// <summary>Owns one capability registration until disposed.</summary>
public sealed class PresentationCapabilityRegistration : IDisposable
{
    private WebUiBinding? _binding;

    internal PresentationCapabilityRegistration(WebUiBinding binding) => _binding = binding;

    /// <summary>Gets the registered capability name, or an empty string after disposal.</summary>
    public string Capability => _binding?.Element ?? string.Empty;

    /// <summary>Unregisters the capability. Later invocations of the name are no longer dispatched to its handler.</summary>
    /// <remarks>Disposing more than once has no effect. A newer registration that replaced this one is not removed.</remarks>
    public void Dispose() => Interlocked.Exchange(ref _binding, null)?.Dispose();
}

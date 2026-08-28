using System.Globalization;
using System.Text;

namespace Runic.Desktop;

/// <summary>Identifies a low-level presentation event delivered to a registered capability.</summary>
public enum PresentationEventKind
{
    Disconnected,
    Connected,
    Click,
    Navigation,
    Invocation,
}

/// <summary>Handles one presentation capability invocation.</summary>
public delegate ValueTask<PresentationResult> PresentationCapabilityHandler(
    PresentationInvocation invocation,
    CancellationToken cancellationToken);

/// <summary>Represents one active presentation invocation.</summary>
public sealed class PresentationInvocation
{
    private readonly WebUiEvent _event;

    internal PresentationInvocation(DesktopSurface surface, WebUiEvent webUiEvent)
    {
        Surface = surface;
        _event = webUiEvent;
        Session = new PresentationSession(webUiEvent.Window, webUiEvent.SessionIdentifier, webUiEvent.ConnectionId);
    }

    public DesktopSurface Surface { get; }
    public PresentationSession Session { get; }
    public PresentationEventKind Kind => (PresentationEventKind)_event.EventType;
    public string Capability => _event.Element;
    public ulong InvocationId => _event.EventNumber;
    public ulong SessionId => _event.ConnectionId;
    public int ArgumentCount => checked((int)_event.ArgumentCount);

    public ReadOnlyMemory<byte> GetBytes(int index = 0) => _event.GetMemory(checked((nuint)index));

    public string GetString(int index = 0) => Encoding.UTF8.GetString(GetBytes(index).Span);

    public long GetInt64(int index = 0) =>
        long.Parse(GetString(index), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public double GetDouble(int index = 0) =>
        double.Parse(GetString(index), NumberStyles.Float, CultureInfo.InvariantCulture);

    public bool GetBoolean(int index = 0) => _event.GetBoolean(checked((nuint)index));

    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _event.RunJavaScriptAsync(script, cancellationToken);

    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        _event.NavigateAsync(url, cancellationToken);

    public Task SendAsync(
        string function,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        _event.SendAsync(function, data, cancellationToken);

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

    public static PresentationResult None { get; } = new(WebUiResult.None);
    public static PresentationResult FromInt64(long value) => new(WebUiResult.FromInt64(value));
    public static PresentationResult FromDouble(double value) => new(WebUiResult.FromDouble(value));
    public static PresentationResult FromBoolean(bool value) => new(WebUiResult.FromBoolean(value));
    public static PresentationResult FromString(string? value) => new(WebUiResult.FromString(value));
    public static implicit operator PresentationResult(string value) => FromString(value);
    public static implicit operator PresentationResult(long value) => FromInt64(value);
    public static implicit operator PresentationResult(double value) => FromDouble(value);
    public static implicit operator PresentationResult(bool value) => FromBoolean(value);

    internal WebUiResult ToCompatibilityResult() => _value;
}

/// <summary>Owns one capability registration until disposed.</summary>
public sealed class PresentationCapabilityRegistration : IDisposable
{
    private WebUiBinding? _binding;

    internal PresentationCapabilityRegistration(WebUiBinding binding) => _binding = binding;

    public string Capability => _binding?.Element ?? string.Empty;

    public void Dispose() => Interlocked.Exchange(ref _binding, null)?.Dispose();
}

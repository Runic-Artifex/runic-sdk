using System.Globalization;
using System.Text;

namespace Runic.Desktop;

/// <summary>Represents one browser event while its managed binding is executing.</summary>
public sealed class WebUiEvent
{
    private readonly byte[][] _arguments;
    private readonly Guid _sessionId;
    private int _active = 1;

    internal WebUiEvent(
        WebUiWindow window,
        Guid sessionId,
        WebUiEventType eventType,
        string element,
        byte[][] arguments,
        nuint eventNumber,
        nuint bindingId,
        nuint clientId,
        nuint connectionId,
        string cookies)
    {
        Window = window;
        _sessionId = sessionId;
        EventType = eventType;
        Element = element;
        _arguments = arguments;
        EventNumber = eventNumber;
        BindingId = bindingId;
        ClientId = clientId;
        ConnectionId = connectionId;
        Cookies = cookies;
    }

    /// <summary>Gets the window that received this event.</summary>
    public WebUiWindow Window { get; }

    /// <summary>Gets the event's category.</summary>
    public WebUiEventType EventType { get; }

    /// <summary>Gets the bound element or JavaScript object name.</summary>
    public string Element { get; }

    /// <summary>Gets this event's identifier.</summary>
    public nuint EventNumber { get; }

    /// <summary>Gets the binding identifier used to dispatch this event.</summary>
    public nuint BindingId { get; }

    /// <summary>Gets the browser client identifier.</summary>
    public nuint ClientId { get; }

    /// <summary>Gets the current browser connection identifier.</summary>
    public nuint ConnectionId { get; }

    /// <summary>Gets the WebSocket request's complete cookie header.</summary>
    public string Cookies { get; }

    /// <summary>Gets the number of arguments supplied by JavaScript.</summary>
    public nuint ArgumentCount
    {
        get
        {
            ThrowIfInactive();
            return (nuint)_arguments.Length;
        }
    }

    /// <summary>Gets an argument as a signed 64-bit integer.</summary>
    public long GetInt64(nuint index = 0)
    {
        if (long.TryParse(GetString(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new FormatException("The JavaScript argument is not a signed 64-bit integer.");
    }

    /// <summary>Gets an argument as a double-precision floating-point number.</summary>
    public double GetDouble(nuint index = 0)
    {
        if (double.TryParse(GetString(index), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new FormatException("The JavaScript argument is not a double-precision number.");
    }

    /// <summary>Gets an argument as a Boolean value.</summary>
    public bool GetBoolean(nuint index = 0)
    {
        var argument = GetString(index);
        if (argument == "1")
        {
            return true;
        }

        if (argument == "0")
        {
            return false;
        }

        if (bool.TryParse(argument, out var value))
        {
            return value;
        }

        throw new FormatException("The JavaScript argument is not a Boolean value.");
    }

    /// <summary>Gets an argument as a UTF-8 string.</summary>
    public string GetString(nuint index = 0) => Encoding.UTF8.GetString(GetArgument(index));

    /// <summary>Copies an argument's raw bytes into managed memory.</summary>
    public byte[] GetBytes(nuint index = 0) => GetArgument(index).ToArray();

    /// <summary>Sends JavaScript to only the client that raised this event.</summary>
    public void RunJavaScript(string script)
    {
        ThrowIfInactive();
        Window.RunJavaScriptForSessionAsync(_sessionId, script, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Navigates only the client that raised this event.</summary>
    public void Navigate(string url)
    {
        ThrowIfInactive();
        Window.NavigateSessionAsync(_sessionId, url, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Sends raw bytes to only the client that raised this event.</summary>
    public void SendRaw(string function, ReadOnlySpan<byte> data)
    {
        ThrowIfInactive();
        Window.SendRawToSessionAsync(_sessionId, function, data.ToArray(), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Closes only the client that raised this event.</summary>
    public void CloseClient()
    {
        ThrowIfInactive();
        Window.CloseSessionAsync(_sessionId, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal void Invalidate() => Volatile.Write(ref _active, 0);

    private byte[] GetArgument(nuint index)
    {
        ThrowIfInactive();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, (nuint)_arguments.Length, nameof(index));
        return _arguments[checked((int)index)];
    }

    private void ThrowIfInactive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _active) == 0, this);
    }
}

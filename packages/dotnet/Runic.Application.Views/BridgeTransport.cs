namespace Runic.Application.Views;

// The Bridge needs only callable handlers, change delivery, and typed input.
// Window ownership, native dialogs, and close policy stay with a host adapter.
/// <summary>Positional arguments of one browser call, read in declaration order.</summary>
public interface IBridgeArguments
{
    /// <summary>Reads the next argument as a 64-bit integer.</summary>
    long GetInt64();
    /// <summary>Reads the next argument as a Boolean.</summary>
    bool GetBoolean();
    /// <summary>Reads the next argument as a string.</summary>
    string GetString();
    /// <summary>The host-verified browser client identity, if the host provides one.</summary>
    string? ClientKey => null;
    /// <summary>The host-verified transport connection identity, if the host provides one.</summary>
    string? ConnectionKey => null;
}

/// <summary>The host adapter surface used by generated bridges to bind routes and publish state.</summary>
public interface IBridgeTransport
{
    /// <summary>Binds a synchronous browser route.</summary>
    /// <returns>A lease that unbinds the route.</returns>
    IDisposable Bind(string name, Func<IBridgeArguments, string> handler);
    /// <summary>Binds an asynchronous browser route.</summary>
    /// <returns>A lease that unbinds the route.</returns>
    IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler);
    /// <summary>Delivers an encoded state snapshot to the browser listener for <paramref name="name"/>.</summary>
    void Publish(string name, string stateJson);
}

/// <summary>A host that can acknowledge state delivery before the next dependent frame is sent.</summary>
public interface IAsyncBridgeTransport : IBridgeTransport
{
    /// <summary>Delivers one frame without coalescing it with other frames.</summary>
    /// <remarks>Generated bridges await this outside the model context. Slow delivery is bounded by the bridge's recovery-snapshot queue.</remarks>
    ValueTask PublishAsync(string name, string stateJson);
}

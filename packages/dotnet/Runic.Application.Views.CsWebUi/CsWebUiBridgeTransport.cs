using System.Globalization;
using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

/// <summary>Creates Bridge transports for CS-WebUI windows.</summary>
public static class CsWebUiBridgeExtensions
{
    /// <summary>Creates a transport whose routes outlive individual bridge attachments.</summary>
    /// <remarks>
    /// CS-WebUI cannot remove a native route before its window closes, so the transport keeps each route and lets
    /// Bridge attachments replace its handler; a call to a released route gets the disconnected reply. The returned
    /// object is also <see cref="IDisposable"/>: dispose it before disposing <paramref name="window"/>.
    /// </remarks>
    public static IBridgeTransport CreateBridgeSession(this WebUiWindow window) =>
        new RebindableBridgeTransport(new CsWebUiBridgeTransport(window));
}

internal sealed class CsWebUiBridgeTransport(WebUiWindow window) : IBridgeTransport
{
    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) =>
        window.Bind(name, e => WebUiResult.FromString(handler(new WebUiBridgeArguments(e))));

    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
        window.BindAsync(name, async (e, token) =>
            WebUiResult.FromString(await handler(new WebUiBridgeArguments(e), token).ConfigureAwait(false)));

    // One uncoalesced WebSocket write per call to every connected client, so delta frames
    // keep their order. WebUI blocks under its process-wide send lock until each socket
    // write completes: TCP-level back-pressure, not acknowledgement. See IAsyncBridgeTransport.
    public void Publish(string name, string stateJson) =>
        window.RunJavaScript($"window.__{name}Changed?.({stateJson});");
}

internal sealed class WebUiBridgeArguments(WebUiEvent value) : IBridgeArguments
{
    public string ClientKey => value.ClientId.ToString(CultureInfo.InvariantCulture);
    public string ConnectionKey => $"{value.ClientId}:{value.ConnectionId}";
    public long GetInt64() => Read(value.GetInt64);
    public bool GetBoolean() => Read(value.GetBoolean);
    public string GetString() => Read(value.GetString);

    // CS-WebUI reads each argument with the length WebUI received and throws InvalidDataException for a call whose
    // argument claims more than WebUI's message limit. The Bridge routes reject an ArgumentException as an invalid
    // request, as they do malformed JSON.
    private static T Read<T>(Func<nuint, T> read)
    {
        try { return read(0); }
        catch (InvalidDataException error) { throw new ArgumentException(error.Message, error); }
    }
}

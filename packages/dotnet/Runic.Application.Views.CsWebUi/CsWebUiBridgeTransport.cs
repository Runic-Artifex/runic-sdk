using System.Globalization;
using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

/// <summary>Attaches generated bridges to CS-WebUI windows.</summary>
public static class CsWebUiBridgeExtensions
{
    /// <summary>Attaches the generated bridge for <paramref name="viewModel"/> to <paramref name="window"/>.</summary>
    /// <returns>A lease that detaches the bridge.</returns>
    public static IDisposable AttachBridge<T>(this WebUiWindow window, T viewModel) where T : class =>
        Bridge.Attach(new CsWebUiBridgeTransport(window), viewModel);

    /// <summary>Creates a transport whose routes outlive individual bridge attachments.</summary>
    public static RebindableBridgeTransport CreateBridgeSession(this WebUiWindow window) =>
        new(new CsWebUiBridgeTransport(window));
}

internal sealed class CsWebUiBridgeTransport(WebUiWindow window) : IBridgeTransport
{
    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler) =>
        window.Bind(name, e => WebUiResult.FromString(handler(new WebUiBridgeArguments(e))));

    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
        window.BindAsync(name, async (e, token) =>
            WebUiResult.FromString(await handler(new WebUiBridgeArguments(e), token).ConfigureAwait(false)));

    public void Publish(string name, string stateJson) =>
        window.RunJavaScript($"window.__{name}Changed?.({stateJson});");
}

internal sealed class WebUiBridgeArguments(WebUiEvent value) : IBridgeArguments
{
    public string ClientKey => value.ClientId.ToString(CultureInfo.InvariantCulture);
    public string ConnectionKey => $"{value.ClientId}:{value.ConnectionId}";
    public long GetInt64() => value.GetInt64();
    public bool GetBoolean() => value.GetBoolean();
    public string GetString() => value.GetString();
}

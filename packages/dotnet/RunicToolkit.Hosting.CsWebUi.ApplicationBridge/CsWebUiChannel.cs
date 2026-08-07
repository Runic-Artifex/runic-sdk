using CsWebUi;

namespace RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

internal interface IApplicationBridgeWindow
{
    IDisposable Bind(string name, Func<IApplicationBridgeEvent, CancellationToken, ValueTask<byte[]?>> callback);
    void SendRaw(string functionName, ReadOnlySpan<byte> data);
}

internal interface IApplicationBridgeEvent
{
    ulong ClientId { get; }
    ulong ConnectionId { get; }
    int ArgumentCount { get; }
    byte[] GetBytes(int index);
    void CloseClient();
}

internal sealed class NativeApplicationBridgeWindow(WebUiWindow window) : IApplicationBridgeWindow
{
    private readonly WebUiWindow _window = window ?? throw new ArgumentNullException(nameof(window));

    public IDisposable Bind(string name, Func<IApplicationBridgeEvent, CancellationToken, ValueTask<byte[]?>> callback) =>
        _window.BindAsync(name, async (webUiEvent, cancellationToken) =>
        {
            byte[]? response = await callback(
                new NativeApplicationBridgeEvent(webUiEvent),
                cancellationToken).ConfigureAwait(false);
            return response is null
                ? WebUiResult.None
                : WebUiResult.FromString(System.Text.Encoding.UTF8.GetString(response));
        });

    public void SendRaw(string functionName, ReadOnlySpan<byte> data) => _window.SendRaw(functionName, data);
}

internal sealed class NativeApplicationBridgeEvent(WebUiEvent webUiEvent) : IApplicationBridgeEvent
{
    public ulong ClientId => (ulong)webUiEvent.ClientId;
    public ulong ConnectionId => (ulong)webUiEvent.ConnectionId;
    public int ArgumentCount => checked((int)webUiEvent.ArgumentCount);
    public byte[] GetBytes(int index) => webUiEvent.GetBytes((nuint)index);
    public void CloseClient() => webUiEvent.CloseClient();
}

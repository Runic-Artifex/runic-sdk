using System;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi.Mvvm;

internal interface ICsWebUiMvvmWindow
{
    IDisposable Bind(
        string name,
        Func<ICsWebUiMvvmEvent, CancellationToken, ValueTask> callback);

    void SendRaw(string functionName, ReadOnlySpan<byte> data);
}

internal interface ICsWebUiMvvmEvent
{
    ulong ClientId { get; }

    ulong ConnectionId { get; }

    int ArgumentCount { get; }

    byte[] GetBytes(int index);

    void SendRaw(string functionName, ReadOnlySpan<byte> data);

    void CloseClient();
}

internal sealed class CsWebUiMvvmWindow(WebUiWindow window) : ICsWebUiMvvmWindow
{
    private readonly WebUiWindow _window = window ?? throw new ArgumentNullException(nameof(window));

    public IDisposable Bind(
        string name,
        Func<ICsWebUiMvvmEvent, CancellationToken, ValueTask> callback) =>
        _window.BindAsync(
            name,
            async (webUiEvent, cancellationToken) =>
            {
                await callback(new CsWebUiMvvmEvent(webUiEvent), cancellationToken).ConfigureAwait(false);
                return WebUiResult.None;
            });

    public void SendRaw(string functionName, ReadOnlySpan<byte> data) =>
        _window.SendRaw(functionName, data);
}

internal sealed class CsWebUiMvvmEvent(WebUiEvent webUiEvent) : ICsWebUiMvvmEvent
{
    public ulong ClientId => (ulong)webUiEvent.ClientId;

    public ulong ConnectionId => (ulong)webUiEvent.ConnectionId;

    public int ArgumentCount => checked((int)webUiEvent.ArgumentCount);

    public byte[] GetBytes(int index) => webUiEvent.GetBytes((nuint)index);

    public void SendRaw(string functionName, ReadOnlySpan<byte> data) =>
        webUiEvent.SendRaw(functionName, data);

    public void CloseClient() => webUiEvent.CloseClient();
}

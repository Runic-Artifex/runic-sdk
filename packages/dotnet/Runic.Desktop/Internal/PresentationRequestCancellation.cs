namespace Runic.Desktop.Internal;

internal sealed class PresentationRequestCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly CancellationTokenRegistration _requesterRegistration;
    private readonly CancellationTokenRegistration _surfaceRegistration;
    private readonly CancellationTokenRegistration _hostRegistration;
    private int _reason;

    internal PresentationRequestCancellation(
        CancellationToken requesterDisconnected,
        CancellationToken surfaceClosing,
        CancellationToken hostStopping)
    {
        _source = new CancellationTokenSource();
        _requesterRegistration = requesterDisconnected.Register(
            static state => ((PresentationRequestCancellation)state!).Cancel(RequestCancellationReason.RequesterDisconnected),
            this);
        _surfaceRegistration = surfaceClosing.Register(
            static state => ((PresentationRequestCancellation)state!).Cancel(RequestCancellationReason.SurfaceClosing),
            this);
        _hostRegistration = hostStopping.Register(
            static state => ((PresentationRequestCancellation)state!).Cancel(RequestCancellationReason.HostStopping),
            this);
    }

    internal CancellationToken Token => _source.Token;

    internal RequestCancellationReason Reason => (RequestCancellationReason)Volatile.Read(ref _reason);

    public void Dispose()
    {
        _hostRegistration.Dispose();
        _surfaceRegistration.Dispose();
        _requesterRegistration.Dispose();
        _source.Dispose();
    }

    private void Cancel(RequestCancellationReason reason)
    {
        if (Interlocked.CompareExchange(ref _reason, (int)reason, 0) == 0)
        {
            _source.Cancel();
        }
    }
}

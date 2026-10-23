using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

// Network.framework's path monitor (nw_path_monitor) on a private dispatch queue. macOS has no public captive-portal or
// internet check, so an available path has unknown connectivity.
internal sealed partial class MacNetworkStatus : INetworkStateSource
{
    private const string Network = "/System/Library/Frameworks/Network.framework/Network";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int Satisfied = 1, Satisfiable = 3; // nw_path_status_t
    private static readonly TimeSpan FirstUpdate = TimeSpan.FromSeconds(5);
    private static readonly Lazy<nint> s_queue = new(static () => QueueCreate("org.runic.platform.network", 0));

    public async ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken)
    {
        // A monitor reports the current path at once after it starts.
        var first = new TaskCompletionSource<PlatformResult<DesktopNetworkState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = Start(result => first.TrySetResult(result));
        try { return await first.Task.WaitAsync(FirstUpdate, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopNetworkState>> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(Start(report));
    }

    private static Monitor Start(Action<PlatformResult<DesktopNetworkState>> report)
    {
        var monitor = MonitorCreate();
        if (monitor == 0) throw new NativeBackendUnavailableException("nw_path_monitor_create failed.");
        MonitorSetQueue(monitor, s_queue.Value);
        var handler = MacDesktopBlock.Create(path =>
        {
            if (path != 0) report(new PlatformResult<DesktopNetworkState>.Success(State(PathStatus(path), PathIsExpensive(path) != 0, PathIsConstrained(path) != 0)));
        });
        // The monitor keeps its own copy of the block.
        try { MonitorSetUpdateHandler(monitor, handler); }
        finally { MacDesktopBlock.Release(handler); }
        MonitorStart(monitor);
        return new Monitor(monitor);
    }

    // A satisfied path is available; a satisfiable one, such as an on-demand VPN, may become available with a connection.
    internal static DesktopNetworkState State(int status, bool expensive, bool constrained)
    {
        bool available = status is Satisfied or Satisfiable;
        return new DesktopNetworkState(available)
        {
            Connectivity = available ? DesktopNetworkConnectivity.Unknown : DesktopNetworkConnectivity.None,
            // Low Data Mode is a constrained path; cellular and personal hotspots are expensive.
            IsMetered = expensive || constrained,
        };
    }

    private sealed class Monitor(nint monitor) : IAsyncDisposable
    {
        private nint _monitor = monitor;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _monitor, 0);
            if (current != 0)
            {
                // After cancel the queue runs no more updates; the release frees the handler block.
                MonitorCancel(current);
                Release(current);
            }
            return ValueTask.CompletedTask;
        }
    }

    [LibraryImport(Network, EntryPoint = "nw_path_monitor_create")] private static partial nint MonitorCreate();
    [LibraryImport(Network, EntryPoint = "nw_path_monitor_set_queue")] private static partial void MonitorSetQueue(nint monitor, nint queue);
    [LibraryImport(Network, EntryPoint = "nw_path_monitor_set_update_handler")] private static partial void MonitorSetUpdateHandler(nint monitor, nint handler);
    [LibraryImport(Network, EntryPoint = "nw_path_monitor_start")] private static partial void MonitorStart(nint monitor);
    [LibraryImport(Network, EntryPoint = "nw_path_monitor_cancel")] private static partial void MonitorCancel(nint monitor);
    [LibraryImport(Network, EntryPoint = "nw_path_get_status")] private static partial int PathStatus(nint path);
    [LibraryImport(Network, EntryPoint = "nw_path_is_expensive")] private static partial byte PathIsExpensive(nint path);
    [LibraryImport(Network, EntryPoint = "nw_path_is_constrained")] private static partial byte PathIsConstrained(nint path);
    [LibraryImport(Network, EntryPoint = "nw_release")] private static partial void Release(nint value);
    [LibraryImport(LibSystem, EntryPoint = "dispatch_queue_create", StringMarshalling = StringMarshalling.Utf8)] private static partial nint QueueCreate(string label, nint attributes);
}

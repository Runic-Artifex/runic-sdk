using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.WinSock;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// The network connectivity hint of the Network List Manager, through iphlpapi's flat API (Windows 10 2004 and later).
[SupportedOSPlatform("windows10.0.19041")]
internal sealed unsafe class WindowsNetworkStatus : INetworkStateSource
{
    // Whether this Windows exports the hint API; older builds report the service unavailable.
    internal static bool IsSupported { get; } = NativeLibrary.TryLoad("iphlpapi.dll", typeof(WindowsNetworkStatus).Assembly,
        DllImportSearchPath.System32, out var module) && NativeLibrary.TryGetExport(module, "GetNetworkConnectivityHint", out _)
        && NativeLibrary.TryGetExport(module, "NotifyNetworkConnectivityHintChange", out _);

    public ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NL_NETWORK_CONNECTIVITY_HINT hint;
        var error = PInvoke.GetNetworkConnectivityHint(&hint);
        return ValueTask.FromResult(error == WIN32_ERROR.NO_ERROR ? new PlatformResult<DesktopNetworkState>.Success(State(hint))
            : (PlatformResult<DesktopNetworkState>)new PlatformResult<DesktopNetworkState>.Failed(PlatformFailureCode.Other,
                new PlatformDiagnostic("Win32", (long)error)));
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopNetworkState>> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var subscription = new Subscription(report);
        return ValueTask.FromResult<IAsyncDisposable>(subscription.Start());
    }

    // The hint as a state. Windows reports a captive portal, and a network whose internet check failed otherwise, as
    // constrained internet access; Runic reports both as a captive portal. A hidden or unknown level is the .NET view.
    internal static DesktopNetworkState State(NL_NETWORK_CONNECTIVITY_HINT hint)
    {
        var connectivity = hint.ConnectivityLevel switch
        {
            NL_NETWORK_CONNECTIVITY_LEVEL_HINT.NetworkConnectivityLevelHintNone => DesktopNetworkConnectivity.None,
            NL_NETWORK_CONNECTIVITY_LEVEL_HINT.NetworkConnectivityLevelHintLocalAccess => DesktopNetworkConnectivity.Local,
            NL_NETWORK_CONNECTIVITY_LEVEL_HINT.NetworkConnectivityLevelHintInternetAccess => DesktopNetworkConnectivity.Internet,
            NL_NETWORK_CONNECTIVITY_LEVEL_HINT.NetworkConnectivityLevelHintConstrainedInternetAccess => DesktopNetworkConnectivity.CaptivePortal,
            _ => DesktopNetworkConnectivity.Unknown,
        };
        bool available = connectivity switch
        {
            DesktopNetworkConnectivity.None => false,
            DesktopNetworkConnectivity.Unknown => NetworkInterface.GetIsNetworkAvailable(),
            _ => true,
        };
        bool? metered = hint.ConnectivityCost switch
        {
            _ when hint.OverDataLimit || hint.Roaming => true,
            NL_NETWORK_CONNECTIVITY_COST_HINT.NetworkConnectivityCostHintFixed or NL_NETWORK_CONNECTIVITY_COST_HINT.NetworkConnectivityCostHintVariable => true,
            NL_NETWORK_CONNECTIVITY_COST_HINT.NetworkConnectivityCostHintUnrestricted => false,
            _ => null,
        };
        return new DesktopNetworkState(available)
        {
            Connectivity = available ? connectivity : DesktopNetworkConnectivity.None,
            IsMetered = metered,
        };
    }

    // One registration of NotifyNetworkConnectivityHintChange; Windows calls back on a thread of its own, first with the
    // current hint.
    private sealed class Subscription(Action<PlatformResult<DesktopNetworkState>> report) : IAsyncDisposable
    {
        private readonly Action<PlatformResult<DesktopNetworkState>> _report = report;
        private GCHandle _self;
        private HANDLE _handle;

        internal Subscription Start()
        {
            _self = GCHandle.Alloc(this);
            HANDLE handle;
            var error = PInvoke.NotifyNetworkConnectivityHintChange(&Changed, (void*)GCHandle.ToIntPtr(_self), true, &handle);
            if (error != WIN32_ERROR.NO_ERROR)
            {
                _self.Free();
                throw new NativeBackendUnavailableException($"NotifyNetworkConnectivityHintChange failed with {error}.");
            }
            _handle = handle;
            return this;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Changed(void* context, NL_NETWORK_CONNECTIVITY_HINT hint)
        {
            try
            {
                if (GCHandle.FromIntPtr((nint)context).Target is Subscription subscription)
                    subscription._report(new PlatformResult<DesktopNetworkState>.Success(State(hint)));
            }
            catch { /* Never unwind into iphlpapi. */ }
        }

        public ValueTask DisposeAsync()
        {
            if (!_handle.IsNull)
            {
                // Returns once no callback runs any more, so the handle can be freed. Never called from a callback.
                PInvoke.CancelMibChangeNotify2(_handle);
                _handle = default;
                _self.Free();
            }
            return ValueTask.CompletedTask;
        }
    }
}

using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Runic.Application.Views.RunicViewsHotReload))]

namespace Runic.Application.Views;

/// <summary>Refreshes active browser snapshots after an in-process .NET code update.</summary>
/// <remarks>
/// Messages go to the logger of the window session that owns the affected Bridge (events 1100-1102), or to
/// <see cref="System.Diagnostics.Trace"/> without one. The <c>RUNIC_IDE_CONTRACT_RESTART_REQUIRED</c> line for IDEs
/// is always written to standard error.
/// </remarks>
public static class RunicViewsHotReload
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<IHotReloadableBridge>> Active = [];
    private static bool _restartRequired;

    /// <summary>
    /// Raised once when an applied Debug edit changes a generated Bridge contract, so the running frontend no longer
    /// matches and the application must restart. The sender is <see langword="null"/>. The event is static: unsubscribe
    /// a handler whose owner ends before the process.
    /// </summary>
    public static event EventHandler<RunicHotReloadRestartEventArgs>? RestartRequired;

    internal static void Track(IHotReloadableBridge bridge)
    {
        if (!MetadataUpdater.IsSupported) return;
        lock (Gate)
        {
            if (Active.Count > 256) Active.RemoveAll(reference => !reference.TryGetTarget(out _));
            Active.Add(new WeakReference<IHotReloadableBridge>(bridge));
        }
    }

    internal static void UpdateApplication(Type[]? updatedTypes)
    {
        IHotReloadableBridge[] current;
        lock (Gate)
        {
            if (_restartRequired) return;
            Active.RemoveAll(reference =>
            {
                IHotReloadableBridge? ignored;
                return !reference.TryGetTarget(out ignored);
            });
            current = Active.Select(reference => reference.TryGetTarget(out var bridge) ? bridge : null)
                .OfType<IHotReloadableBridge>().ToArray();
        }

        // A changed child ViewModel or View contract can alter a mounted
        // parent's generated content union and DI composition. Inspect every
        // active Bridge instead of only the types reported by Hot Reload.
        var inspected = new HashSet<Type>();
        foreach (var bridge in current)
        {
            if (!inspected.Add(bridge.ContractModelType)) continue;
            var mismatch = bridge.ContractMismatch();
            if (mismatch is null) continue;
            lock (Gate)
            {
                if (_restartRequired) return;
                _restartRequired = true;
            }
            var message = $"RUNIC_IDE_CONTRACT_RESTART_REQUIRED|{mismatch}";
            Console.Error.WriteLine(message);
            ViewsLog.HotReloadRestartRequired(bridge.Logger, mismatch);
            try { RestartRequired?.Invoke(null, new RunicHotReloadRestartEventArgs(mismatch)); }
            catch (Exception error) { ViewsLog.HotReloadRestartNotificationFailed(bridge.Logger, error, BridgeTelemetry.ErrorType(error)); }
            return;
        }
        foreach (var bridge in current)
        {
            try { bridge.RefreshAfterHotReload(); }
            catch (Exception error)
            {
                ViewsLog.HotReloadRefreshFailed(bridge.Logger, error, bridge.ContractModelType.Name, BridgeTelemetry.ErrorType(error));
            }
        }
    }
}

/// <summary>The data of <see cref="RunicViewsHotReload.RestartRequired"/>.</summary>
public sealed class RunicHotReloadRestartEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    /// <param name="mismatch">Which generated contract the edit changed.</param>
    public RunicHotReloadRestartEventArgs(string mismatch)
    {
        ArgumentNullException.ThrowIfNull(mismatch);
        Mismatch = mismatch;
    }

    /// <summary>Which generated contract the edit changed, as written after <c>RUNIC_IDE_CONTRACT_RESTART_REQUIRED|</c>.</summary>
    public string Mismatch { get; }
}

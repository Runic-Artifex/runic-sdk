using System.Diagnostics;
using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Runic.Application.Views.RunicBridgeHotReload))]

namespace Runic.Application.Views;

/// <summary>Refreshes active browser snapshots after an in-process .NET code update.</summary>
public static class RunicBridgeHotReload
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<IHotReloadableBridge>> Active = [];
    private static bool _restartRequired;

    /// <summary>Raised when an applied Debug edit changes a generated Bridge contract.</summary>
    public static event Action<string>? RestartRequired;

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
            Trace.TraceError(message);
            try { RestartRequired?.Invoke(mismatch); }
            catch (Exception error) { Trace.TraceWarning($"Runic restart notification failed: {error}"); }
            return;
        }
        foreach (var bridge in current)
        {
            try { bridge.RefreshAfterHotReload(); }
            catch (Exception error) { Trace.TraceWarning($"A Runic Bridge Hot Reload refresh failed: {error}"); }
        }
    }
}

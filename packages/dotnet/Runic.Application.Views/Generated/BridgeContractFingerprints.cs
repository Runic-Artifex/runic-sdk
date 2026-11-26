using System.Collections.Concurrent;

namespace Runic.Application.Views.Generated;

/// <summary>
/// The generated contract fingerprint of each ViewModel type whose Bridge this process created. The fingerprint hashes
/// the View's contract IR (<c>runic.views.fingerprint/1</c>), which only the generator has, so the test drivers of
/// Runic.Application.Testing read it here to send the contracts of operations, interactions and view requests.
/// </summary>
internal static class BridgeContractFingerprints
{
    private static readonly ConcurrentDictionary<Type, string> Fingerprints = new();

    internal static void Record(Type modelType, string fingerprint)
    {
        // Every Bridge of a type carries the same generated value; only the first construction writes.
        if (!Fingerprints.ContainsKey(modelType)) Fingerprints.TryAdd(modelType, fingerprint);
    }

    internal static string? Of(Type modelType) => Fingerprints.TryGetValue(modelType, out var fingerprint) ? fingerprint : null;
}

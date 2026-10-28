namespace Runic.Application.Views;

// The outcome of an open or close request (specs/application/README.md, "Presentations").
internal enum BridgePresentationOutcome
{
    // Registered, an exact repeat, or closed.
    Ok,
    // Another connection or client owns the presentation id on the route.
    Ignored,
    // The connection already holds MaximumPerConnection presentations of the route.
    Capacity,
    // The route is suspended or the window closed.
    Released,
}

// The open presentations of one route and the connection that owns each. Not thread-safe: the owner locks.
internal sealed class BridgePresentationOwners
{
    // A connection holds at most this many presentations per route.
    internal const int MaximumPerConnection = 64;

    private readonly Dictionary<string, (string ClientKey, string ConnectionKey)> _owners = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, (string ClientKey, string ConnectionKey)> Owners => _owners;

    internal int Count => _owners.Count;

    internal string First() => _owners.Keys.First();

    internal BridgePresentationOutcome Open(string presentation, BridgeConnection connection, out bool added)
    {
        added = false;
        if (_owners.TryGetValue(presentation, out var owner))
            return Owns(owner, connection) ? BridgePresentationOutcome.Ok : BridgePresentationOutcome.Ignored;
        var held = 0;
        foreach (var other in _owners.Values)
            if (other.ConnectionKey == connection.Key) held++;
        if (held >= MaximumPerConnection) return BridgePresentationOutcome.Capacity;
        _owners.Add(presentation, (connection.ClientKey, connection.Key));
        added = true;
        return BridgePresentationOutcome.Ok;
    }

    internal BridgePresentationOutcome Close(string presentation, BridgeConnection connection, out bool removed)
    {
        removed = false;
        if (!_owners.TryGetValue(presentation, out var owner)) return BridgePresentationOutcome.Ok;
        if (!Owns(owner, connection)) return BridgePresentationOutcome.Ignored;
        _owners.Remove(presentation);
        removed = true;
        return BridgePresentationOutcome.Ok;
    }

    internal bool Remove(string presentation) => _owners.Remove(presentation);

    // Removes and returns the presentations of a closed connection.
    internal string[] ReleaseConnection(string connectionKey)
    {
        var released = _owners.Where(pair => pair.Value.ConnectionKey == connectionKey).Select(pair => pair.Key).ToArray();
        foreach (var presentation in released) _owners.Remove(presentation);
        return released;
    }

    // Removes and returns every presentation.
    internal string[] Clear()
    {
        var released = _owners.Keys.ToArray();
        _owners.Clear();
        return released;
    }

    private static bool Owns((string ClientKey, string ConnectionKey) owner, BridgeConnection connection) =>
        owner.ConnectionKey == connection.Key && owner.ClientKey == connection.ClientKey;
}

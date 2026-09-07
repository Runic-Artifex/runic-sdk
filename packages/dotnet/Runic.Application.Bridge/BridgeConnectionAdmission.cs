namespace Runic.Application.Bridge;

/// <summary>Associates an application session with an initialized physical connection.</summary>
/// <remarks>Call under the transport dispatch lock. Transport authentication precedes this policy.</remarks>
public sealed class BridgeConnectionAdmission
{
    private (ulong Client, ulong Connection)? _owner;
    private long _epoch = -1;
    private bool _connected;

    /// <summary>Checks ownership and reconnect epoch before application dispatch.</summary>
    public bool CanAccept(ulong client, ulong connection, BridgeClientEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_owner is null) return envelope.Kind == "initialize";
        if (_connected && _owner == (client, connection))
            return envelope.ConnectionEpoch == _epoch || envelope.Kind == "initialize" && envelope.ConnectionEpoch > _epoch;
        return envelope.Kind == "initialize" && envelope.ConnectionEpoch > _epoch;
    }

    /// <summary>Transfers ownership only after successful initialization.</summary>
    public void Accept(ulong client, ulong connection, BridgeClientEnvelope request, BridgeHostEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (request.Kind != "initialize" || response.Kind != "snapshot") return;
        _owner = (client, connection);
        _epoch = request.ConnectionEpoch;
        _connected = true;
    }

    /// <summary>Checks whether a connection may retrieve queued application events.</summary>
    public bool Owns(ulong client, ulong connection) => _connected && _owner == (client, connection);

    /// <summary>Requires a new initialization epoch after physical disconnection.</summary>
    public void Disconnect(ulong client, ulong connection)
    {
        if (_owner == (client, connection)) _connected = false;
    }
}

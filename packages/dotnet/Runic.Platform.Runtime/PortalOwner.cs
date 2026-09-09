namespace Runic.Platform.Runtime;

/// <summary>A verified presentation owner that can export an XDG portal parent.</summary>
public interface IPortalWindowOwner : INativePickerOwner
{
    /// <summary>Exports a parent valid until the returned lease is disposed.</summary>
    ValueTask<PortalParentLease> ExportParentAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns an exported parent identifier; dispose after the portal request closes.</summary>
public abstract class PortalParentLease : IAsyncDisposable
{
    /// <summary>Gets an x11: or wayland: identifier, never a raw toolkit pointer.</summary>
    public abstract string Identifier { get; }
    /// <summary>Gets cancellation when the parent closes, when supported by the owner.</summary>
    public virtual CancellationToken OwnerClosed => CancellationToken.None;
    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();
}

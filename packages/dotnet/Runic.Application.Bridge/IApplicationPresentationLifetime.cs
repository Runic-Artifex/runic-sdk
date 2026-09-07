namespace Runic.Application.Bridge;

/// <summary>A scoped service that drains presentation-owned work before its host closes.</summary>
/// <remarks>Register the same scoped instance under this interface and its feature interfaces.
/// Reconnection does not stop these services. Stop must reject new work, request cancellation,
/// and await outstanding native releases without blocking the owner thread. Disposal follows
/// after bridge dispatch has drained, while the host presentation is still alive.</remarks>
public interface IApplicationPresentationLifetime
{
    /// <summary>Stops presentation work. Concurrent shutdown requests share this operation.</summary>
    ValueTask StopAsync();
}

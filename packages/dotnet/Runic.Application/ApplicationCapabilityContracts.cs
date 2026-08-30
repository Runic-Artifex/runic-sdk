using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Runic.Application;

/// <summary>Describes whether a manifest-declared capability is available from the selected host.</summary>
public enum ApplicationCapabilityAvailability
{
    /// <summary>The selected host supports the capability.</summary>
    Available,
    /// <summary>The selected host does not support the capability.</summary>
    Unavailable,
}

/// <summary>Provides one explicit host projection for a manifest-declared capability.</summary>
public sealed record ApplicationCapabilityStatus
{
    /// <summary>Initializes a validated capability status.</summary>
    public ApplicationCapabilityStatus(string name, ApplicationCapabilityAvailability availability, string? unavailableReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (availability == ApplicationCapabilityAvailability.Available && unavailableReason is not null)
        {
            throw new ArgumentException("Available capabilities cannot include an unavailable reason.", nameof(unavailableReason));
        }
        if (availability == ApplicationCapabilityAvailability.Unavailable && string.IsNullOrWhiteSpace(unavailableReason))
        {
            throw new ArgumentException("Unavailable capabilities require a stable reason.", nameof(unavailableReason));
        }
        Name = name;
        Availability = availability;
        UnavailableReason = unavailableReason;
    }

    /// <summary>Gets the manifest capability identity.</summary>
    public string Name { get; }
    /// <summary>Gets whether the selected host supports the capability.</summary>
    public ApplicationCapabilityAvailability Availability { get; }
    /// <summary>Gets the bounded reason when the capability is unavailable.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Creates an available capability status.</summary>
    public static ApplicationCapabilityStatus Available(string name) => new(name, ApplicationCapabilityAvailability.Available);
    /// <summary>Creates an unavailable capability status with a stable reason.</summary>
    public static ApplicationCapabilityStatus Unavailable(string name, string reason) => new(name, ApplicationCapabilityAvailability.Unavailable, reason);
}

/// <summary>Lets a selected host project availability for manifest-declared capabilities.</summary>
public interface IApplicationCapabilityProvider
{
    /// <summary>Gets the explicit status for one capability declared by the application manifest.</summary>
    ApplicationCapabilityStatus GetCapabilityStatus(string capability);
}

/// <summary>Provides the selected host's complete, manifest-authoritative capability projection.</summary>
public sealed class ApplicationCapabilityProjection
{
    private readonly ImmutableDictionary<string, ApplicationCapabilityStatus> _statuses;

    internal ApplicationCapabilityProjection(ApplicationCompositionManifest manifest, IApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(host);
        _statuses = manifest.Capabilities
            .Select(capability => Resolve(capability, host))
            .ToImmutableDictionary(status => status.Name, StringComparer.Ordinal);
    }

    /// <summary>Gets statuses in the manifest's canonical capability order.</summary>
    public ImmutableArray<ApplicationCapabilityStatus> Statuses => _statuses.Values.OrderBy(status => status.Name, StringComparer.Ordinal).ToImmutableArray();

    /// <summary>Gets the status for one manifest-declared capability.</summary>
    public ApplicationCapabilityStatus GetRequired(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return _statuses.TryGetValue(capability, out ApplicationCapabilityStatus? status)
            ? status
            : throw new ArgumentException($"Capability '{capability}' is not declared by the application manifest.", nameof(capability));
    }

    private static ApplicationCapabilityStatus Resolve(string capability, IApplicationHost host)
    {
        if (host is not IApplicationCapabilityProvider provider)
        {
            return ApplicationCapabilityStatus.Unavailable(capability, "host-does-not-report-capabilities");
        }
        ApplicationCapabilityStatus status = provider.GetCapabilityStatus(capability)
            ?? throw new InvalidOperationException($"The selected host did not return a status for capability '{capability}'.");
        if (!string.Equals(status.Name, capability, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The selected host returned capability '{status.Name}' while '{capability}' was requested.");
        }
        return status;
    }
}

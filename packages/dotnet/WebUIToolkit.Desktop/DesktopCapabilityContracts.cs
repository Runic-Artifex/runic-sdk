using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WebUIToolkit.Desktop;

/// <summary>Identifies a desktop behavior that may vary by host and platform.</summary>
public enum DesktopCapability
{
    /// <summary>Dispatch work to the native UI owner.</summary>
    UiDispatch = 1,
    /// <summary>Focus the native window.</summary>
    WindowFocus = 2,
    /// <summary>Focus a semantic element in the browser document.</summary>
    ElementFocus = 3,
    /// <summary>Change native window dimensions and position.</summary>
    WindowPlacement = 4,
    /// <summary>Minimize, maximize, or restore the native window.</summary>
    WindowState = 5,
    /// <summary>Register keyboard accelerators.</summary>
    KeyboardAccelerators = 6,
    /// <summary>Read and write text through the operating-system clipboard.</summary>
    Clipboard = 7,
    /// <summary>Open native file-selection and save dialogs.</summary>
    FileDialogs = 8,
    /// <summary>Receive files and text dropped on the application.</summary>
    DragAndDrop = 9,
    /// <summary>Open an external URI with the operating system.</summary>
    ExternalUri = 10,
    /// <summary>Publish a desktop or browser notification.</summary>
    Notifications = 11,
    /// <summary>Select an isolated browser profile before window startup.</summary>
    BrowserProfile = 12,
    /// <summary>Use browser-local persistent storage.</summary>
    BrowserStorage = 13,
    /// <summary>Create and own more than one native window.</summary>
    MultipleWindows = 14,
}

/// <summary>Describes whether a host can provide one capability.</summary>
public enum DesktopCapabilityStatus
{
    /// <summary>The capability is ready for use.</summary>
    Supported = 1,
    /// <summary>The host cannot provide the capability.</summary>
    Unsupported = 2,
    /// <summary>The capability exists but is not currently available.</summary>
    Unavailable = 3,
    /// <summary>The capability requires permission that has not been granted.</summary>
    PermissionRequired = 4,
}

/// <summary>Contains the stable status and remediation for one capability.</summary>
public sealed record DesktopCapabilityDescriptor
{
    /// <summary>Creates one immutable capability observation.</summary>
    public DesktopCapabilityDescriptor(
        DesktopCapability capability,
        DesktopCapabilityStatus status,
        string? reason = null)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (reason is not null && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A capability reason cannot be empty.", nameof(reason));
        }

        Capability = capability;
        Status = status;
        Reason = reason;
    }

    /// <summary>Gets the capability.</summary>
    public DesktopCapability Capability { get; }

    /// <summary>Gets its current status.</summary>
    public DesktopCapabilityStatus Status { get; }

    /// <summary>Gets bounded consumer-facing context when the capability is not supported.</summary>
    public string? Reason { get; }

    /// <summary>Gets whether the capability is ready for use.</summary>
    public bool IsSupported => Status == DesktopCapabilityStatus.Supported;
}

/// <summary>Provides a frozen, queryable desktop capability report.</summary>
public sealed class DesktopCapabilityReport
{
    private readonly ReadOnlyDictionary<DesktopCapability, DesktopCapabilityDescriptor> _items;

    /// <summary>Creates and validates a complete immutable report.</summary>
    public DesktopCapabilityReport(
        string host,
        string platform,
        IReadOnlyList<DesktopCapabilityDescriptor> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(capabilities);

        Dictionary<DesktopCapability, DesktopCapabilityDescriptor> items = [];
        foreach (DesktopCapabilityDescriptor descriptor in capabilities)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!items.TryAdd(descriptor.Capability, descriptor))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Capability}' is reported more than once.",
                    nameof(capabilities));
            }
        }

        foreach (DesktopCapability capability in Enum.GetValues<DesktopCapability>())
        {
            if (!items.ContainsKey(capability))
            {
                throw new ArgumentException(
                    $"Capability '{capability}' is missing from the report.",
                    nameof(capabilities));
            }
        }

        Host = host;
        Platform = platform;
        _items = new ReadOnlyDictionary<DesktopCapability, DesktopCapabilityDescriptor>(items);
    }

    /// <summary>Gets the stable host adapter identity.</summary>
    public string Host { get; }

    /// <summary>Gets the bounded platform identity.</summary>
    public string Platform { get; }

    /// <summary>Gets all descriptors keyed by capability.</summary>
    public IReadOnlyDictionary<DesktopCapability, DesktopCapabilityDescriptor> Capabilities => _items;

    /// <summary>Gets one capability descriptor.</summary>
    public DesktopCapabilityDescriptor this[DesktopCapability capability] =>
        _items.TryGetValue(capability, out DesktopCapabilityDescriptor? descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(capability));
}

/// <summary>Exposes the host's complete capability report to application code.</summary>
public interface IDesktopCapabilities
{
    /// <summary>Gets the immutable report.</summary>
    DesktopCapabilityReport Report { get; }
}

/// <summary>Reports a deterministic unavailable or unsupported capability invocation.</summary>
public sealed class DesktopCapabilityException : InvalidOperationException
{
    /// <summary>Creates a failure from a non-supported descriptor.</summary>
    public DesktopCapabilityException(DesktopCapabilityDescriptor descriptor)
        : base(CreateMessage(descriptor))
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Capability = descriptor.Capability;
        Status = descriptor.Status;
    }

    /// <summary>Gets the rejected capability.</summary>
    public DesktopCapability Capability { get; }

    /// <summary>Gets the observed support status.</summary>
    public DesktopCapabilityStatus Status { get; }

    private static string CreateMessage(DesktopCapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Reason is null
            ? $"Desktop capability '{descriptor.Capability}' is {descriptor.Status}."
            : $"Desktop capability '{descriptor.Capability}' is {descriptor.Status}: {descriptor.Reason}";
    }
}

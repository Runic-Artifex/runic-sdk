using System.Collections.Immutable;
namespace Runic.Platform.Administration.Windows.Shares;

/// <summary>An SMB share enumeration row.</summary>
public sealed record ShareSummary(string Name, string Description, uint NativeType);

/// <summary>A share's configuration and self-relative security descriptor, separate from filesystem permissions.</summary>
public sealed record ShareSnapshot(string Name, string Path, string Description, uint NativeType,
    uint MaximumUses, uint CurrentUses, ImmutableArray<byte> SecurityDescriptor);

/// <summary>Creates a disk share. A null security descriptor delegates initial security to Windows defaults.</summary>
public sealed record ShareSpecification(string Name, string Path)
{
    /// <summary>Share description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Maximum simultaneous uses; uint.MaxValue is unlimited.</summary>
    public uint MaximumUses { get; init; } = uint.MaxValue;
    /// <summary>An optional self-relative Windows security descriptor.</summary>
    public ImmutableArray<byte>? SecurityDescriptor { get; init; }
}

/// <summary>Selected share edits. Path/type are intentionally not rewritten by metadata/security updates.</summary>
public sealed record ShareUpdate
{
    /// <summary>Replacement description.</summary>
    public string? Description { get; init; }
    /// <summary>Replacement maximum uses.</summary>
    public uint? MaximumUses { get; init; }
    /// <summary>Replacement self-relative security descriptor preserving caller-specified ACE ordering.</summary>
    public ImmutableArray<byte>? SecurityDescriptor { get; init; }
}

namespace Runic.Platform.Administration.Windows.Shares;

/// <summary>An SMB share enumeration row.</summary>
public sealed record ShareSummary(string Name, string Description, uint NativeType);

/// <summary>A share's configuration and optional stored security descriptor, separate from filesystem permissions.</summary>
/// <remarks>A null SecurityDescriptor means Windows returned no stored descriptor. This is distinct from a
/// descriptor containing an empty DACL (deny all) or a null DACL. Access errors are thrown, never represented as null.</remarks>
public sealed record ShareSnapshot(string Name, string Path, string Description, uint NativeType,
    uint MaximumUses, uint CurrentUses, WindowsSecurityDescriptor? SecurityDescriptor);

/// <summary>Creates a disk share with an explicit share security descriptor. Filesystem permissions remain separate.</summary>
/// <remarks>The descriptor is required so a share never receives an implicit Windows default grant.</remarks>
public sealed record ShareSpecification(string Name, string Path, WindowsSecurityDescriptor SecurityDescriptor)
{
    /// <summary>Share description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Maximum simultaneous uses; uint.MaxValue is unlimited.</summary>
    public uint MaximumUses { get; init; } = uint.MaxValue;
}

/// <summary>Selected share edits. Path/type are intentionally not rewritten by metadata/security updates.</summary>
public sealed record ShareUpdate
{
    /// <summary>Replacement description.</summary>
    public string? Description { get; init; }
    /// <summary>Replacement maximum uses.</summary>
    public uint? MaximumUses { get; init; }
    /// <summary>Replacement security descriptor; its ACE order is preserved.</summary>
    public WindowsSecurityDescriptor? SecurityDescriptor { get; init; }
}

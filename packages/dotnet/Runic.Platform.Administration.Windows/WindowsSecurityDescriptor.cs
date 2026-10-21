using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace Runic.Platform.Administration.Windows;

/// <summary>An immutable self-relative Windows security descriptor that compares by its binary form.</summary>
/// <remarks>The binary form is kept exactly as supplied or read, so owner, group, ACE order and inheritance flags are
/// preserved. Use <see cref="ToRawSecurityDescriptor"/> to inspect or edit it with the
/// <see cref="System.Security.AccessControl"/> types, and <see cref="From(RawSecurityDescriptor)"/> to convert the result back.</remarks>
public sealed class WindowsSecurityDescriptor : IEquatable<WindowsSecurityDescriptor>
{
    private string? _sddl;

    // The array is owned by this instance and never changed.
    private WindowsSecurityDescriptor(byte[] binaryForm) => BinaryForm = ImmutableCollectionsMarshal.AsImmutableArray(binaryForm);

    /// <summary>The self-relative binary form.</summary>
    public ImmutableArray<byte> BinaryForm { get; }

    /// <summary>The descriptor in Security Descriptor Definition Language, including every section it contains.</summary>
    public string Sddl => _sddl ??= ToRawSecurityDescriptor().GetSddlForm(AccessControlSections.All);

    /// <summary>Creates a descriptor from SDDL.</summary>
    /// <exception cref="ArgumentException">The SDDL is not a valid security descriptor.</exception>
    public static WindowsSecurityDescriptor FromSddl(string sddl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sddl);
        return From(new RawSecurityDescriptor(sddl));
    }

    /// <summary>Creates a descriptor from the binary form of <paramref name="descriptor"/>. Later changes to it are not reflected.</summary>
    public static WindowsSecurityDescriptor From(RawSecurityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return new(bytes);
    }

    /// <summary>Creates a descriptor from a self-relative binary form, which is copied.</summary>
    /// <exception cref="ArgumentException">The bytes are not one complete self-relative security descriptor.</exception>
    public static WindowsSecurityDescriptor FromBinaryForm(ReadOnlySpan<byte> binaryForm)
    {
        if (binaryForm.IsEmpty) throw new ArgumentException("Supply a self-relative security descriptor.", nameof(binaryForm));
        var bytes = binaryForm.ToArray();
        RawSecurityDescriptor parsed;
        try { parsed = new RawSecurityDescriptor(bytes, 0); }
        catch (Exception error) when (error is ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new ArgumentException("Supply a valid self-relative security descriptor.", nameof(binaryForm), error);
        }
        if ((parsed.ControlFlags & ControlFlags.SelfRelative) == 0 || parsed.BinaryLength > bytes.Length)
            throw new ArgumentException("Supply a complete self-relative security descriptor.", nameof(binaryForm));
        return new(bytes);
    }

    /// <summary>Returns a new <see cref="RawSecurityDescriptor"/> for this descriptor on every call.</summary>
    public RawSecurityDescriptor ToRawSecurityDescriptor() => new(ImmutableCollectionsMarshal.AsArray(BinaryForm)!, 0);

    /// <inheritdoc />
    public bool Equals(WindowsSecurityDescriptor? other) =>
        other is not null && (ReferenceEquals(this, other) || BinaryForm.AsSpan().SequenceEqual(other.BinaryForm.AsSpan()));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as WindowsSecurityDescriptor);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(BinaryForm.AsSpan());
        return hash.ToHashCode();
    }

    /// <summary>Returns <see cref="Sddl"/>.</summary>
    public override string ToString() => Sddl;

    /// <summary>Compares two descriptors by their binary form.</summary>
    public static bool operator ==(WindowsSecurityDescriptor? left, WindowsSecurityDescriptor? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Compares two descriptors by their binary form.</summary>
    public static bool operator !=(WindowsSecurityDescriptor? left, WindowsSecurityDescriptor? right) => !(left == right);
}

using System.ComponentModel;

#pragma warning disable CA1040 // Tombstones have no members: using one is the error.

namespace Runic.Platform.Runtime;

// Tombstone (0.14): a removed type that applications upgrading from older releases still name. It is an error that
// names the replacement, instead of CS0246, and is removed in the release after 0.14.

/// <summary>Removed in 0.9: use <see cref="INativeWindowOwner"/>.</summary>
[Obsolete("INativePickerOwner was removed in 0.9: use INativeWindowOwner, which DesktopNativeOwner implements, and take " +
    "dialogs from a Window's IFileDialogs. See docs/migration-1.0.md.", error: true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface INativePickerOwner;

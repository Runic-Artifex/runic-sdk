using Runic.Platform.Runtime;

namespace Runic.Platform.Linux;

/// <summary>Explicitly selects GTK services without loading another OS provider.</summary>
public static class LinuxPlatformProvider
{
    /// <summary>Creates dialogs bound to the verified GTK presentation owner.</summary>
    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new LinuxFilePicker(owner));
    }

    /// <summary>Creates the GTK clipboard; dispose it before stopping the owner dispatcher.</summary>
    public static LinuxTextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new LinuxTextClipboard(owner);
    }
}

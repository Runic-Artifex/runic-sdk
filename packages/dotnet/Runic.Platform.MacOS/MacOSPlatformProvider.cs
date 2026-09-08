using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

/// <summary>Explicit AppKit provider selection; no native initialization occurs until used.</summary>
public static class MacOSPlatformProvider
{
    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new NativePickerBackend(owner, new MacOsFilePicker(owner));
    }

    public static ITextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit requires macOS.");
        return new MacOsTextClipboard(owner);
    }
}

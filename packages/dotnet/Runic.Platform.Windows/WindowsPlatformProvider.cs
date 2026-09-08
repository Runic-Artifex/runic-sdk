using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

/// <summary>Explicitly selects the Windows provider without probing or loading other providers.</summary>
public static class WindowsPlatformProvider
{
    /// <summary>Creates owned native file dialogs. The owner must dispatch on its Windows STA.</summary>
    public static IPickerBackend CreateFileDialogs(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, new WindowsFilePicker(owner));
    }

    /// <summary>Creates native Unicode text clipboard services for a verified presentation owner.</summary>
    public static ITextClipboard CreateTextClipboard(INativePickerOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new WindowsTextClipboard(owner, new Win32Clipboard());
    }
}

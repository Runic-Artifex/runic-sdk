using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// DesktopHost and DesktopWindow.SetColorSchemeAsync on GTK 3 and GTK 4 (Runic.Desktop.Gtk4 links this file). GTK has no
// per-window scheme: the settings below, or with libadwaita its style manager, belong to the process. WebKitGTK's
// prefers-color-scheme follows gtk-application-prefer-dark-theme, which the libadwaita style manager sets too, and with
// GTK 4.20 or later gtk-interface-color-scheme. NoPreference returns each setting to what it was before Runic first
// changed it. GTK thread only.
internal static unsafe partial class GtkColorScheme
{
    private const string PreferDark = "gtk-application-prefer-dark-theme";
    // GTK 4.20: GtkInterfaceColorScheme UNSUPPORTED, DEFAULT, DARK, LIGHT.
    private const string InterfaceScheme = "gtk-interface-color-scheme";
    private const int InterfaceDefault = 1, InterfaceDark = 2, InterfaceLight = 3;
    private const nint GTypeBoolean = 5 << 2;
    private const int GValueSize = 24;
    // AdwColorScheme: DEFAULT, FORCE_LIGHT, PREFER_LIGHT, PREFER_DARK, FORCE_DARK.
    private const int AdwDefault = 0, AdwForceLight = 1, AdwForceDark = 4;

    // The settings before Runic changed them: -1 until the first change.
    private static int s_original = -1;
    private static int s_originalInterface = -1;

    // Applies the scheme to the GtkSettings of the default display. Returns false when it could not.
    internal static bool Apply(nint settings, DesktopColorScheme colorScheme)
    {
        // libadwaita warns about the GTK setting and replaces it: its style manager decides, and sets the setting.
        if (TryApplyLibadwaita(colorScheme)) return true;
        if (settings == 0) return false;
        var applied = false;
        if (FindProperty(*(nint*)settings, InterfaceScheme) is not 0 and var pspec)
        {
            if (s_originalInterface < 0) s_originalInterface = GetEnum(settings, InterfaceScheme, pspec);
            SetEnum(settings, InterfaceScheme, pspec, colorScheme switch
            {
                DesktopColorScheme.Dark => InterfaceDark,
                DesktopColorScheme.Light => InterfaceLight,
                // UNSUPPORTED is GTK's own value before the platform reported one; DEFAULT asks the platform again.
                _ => s_originalInterface == 0 ? InterfaceDefault : s_originalInterface,
            });
            applied = true;
        }
        if (!HasProperty(settings, PreferDark)) return applied;
        if (s_original < 0) s_original = GetBoolean(settings, PreferDark) ? 1 : 0;
        var dark = colorScheme switch
        {
            DesktopColorScheme.Dark => true,
            DesktopColorScheme.Light => false,
            _ => s_original == 1,
        };
        SetBoolean(settings, PreferDark, dark);
        return true;
    }

    // Only a libadwaita that the application already loaded and initialized (adw_init): loading it here would give the
    // process a second toolkit setup.
    private static bool TryApplyLibadwaita(DesktopColorScheme colorScheme)
    {
        nint library;
        try { library = DlOpen("libadwaita-1.so.0", RtldNow | RtldNoLoad); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        if (library == 0) return false;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "adw_is_initialized", out var initialized)
                || ((delegate* unmanaged[Cdecl]<int>)initialized)() == 0
                || !NativeLibrary.TryGetExport(library, "adw_style_manager_get_default", out var getDefault)
                || !NativeLibrary.TryGetExport(library, "adw_style_manager_set_color_scheme", out var setScheme))
                return false;
            var manager = ((delegate* unmanaged[Cdecl]<nint>)getDefault)();
            if (manager == 0) return false;
            ((delegate* unmanaged[Cdecl]<nint, int, void>)setScheme)(manager, colorScheme switch
            {
                DesktopColorScheme.Dark => AdwForceDark,
                DesktopColorScheme.Light => AdwForceLight,
                _ => AdwDefault,
            });
            return true;
        }
        finally { _ = DlClose(library); }
    }

    // g_object_class_find_property(G_OBJECT_GET_CLASS(instance), name): GTK 4 keeps the deprecated setting, a later
    // GTK may not.
    private static bool HasProperty(nint instance, string name) => FindProperty(*(nint*)instance, name) != 0;

    // GParamSpec.value_type, after the type instance, the name and the flags.
    private static nint ValueType(nint pspec) => *(nint*)(pspec + 3 * sizeof(nint));

    private static int GetEnum(nint instance, string name, nint pspec)
    {
        var value = stackalloc byte[GValueSize];
        new Span<byte>(value, GValueSize).Clear();
        InitValue((nint)value, ValueType(pspec));
        try
        {
            GetProperty(instance, name, (nint)value);
            return GetValueEnum((nint)value);
        }
        finally { UnsetValue((nint)value); }
    }

    private static void SetEnum(nint instance, string name, nint pspec, int number)
    {
        var value = stackalloc byte[GValueSize];
        new Span<byte>(value, GValueSize).Clear();
        InitValue((nint)value, ValueType(pspec));
        try
        {
            SetValueEnum((nint)value, number);
            SetProperty(instance, name, (nint)value);
        }
        finally { UnsetValue((nint)value); }
    }

    private static bool GetBoolean(nint instance, string name)
    {
        var value = stackalloc byte[GValueSize];
        new Span<byte>(value, GValueSize).Clear();
        InitValue((nint)value, GTypeBoolean);
        try
        {
            GetProperty(instance, name, (nint)value);
            return GetValueBoolean((nint)value) != 0;
        }
        finally { UnsetValue((nint)value); }
    }

    private static void SetBoolean(nint instance, string name, bool on)
    {
        var value = stackalloc byte[GValueSize];
        new Span<byte>(value, GValueSize).Clear();
        InitValue((nint)value, GTypeBoolean);
        try
        {
            SetValueBoolean((nint)value, on ? 1 : 0);
            SetProperty(instance, name, (nint)value);
        }
        finally { UnsetValue((nint)value); }
    }

    private const int RtldNow = 2, RtldNoLoad = 4;

    [LibraryImport("libc", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)] private static partial nint DlOpen(string file, int mode);
    [LibraryImport("libc", EntryPoint = "dlclose")] private static partial int DlClose(nint handle);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_class_find_property", StringMarshalling = StringMarshalling.Utf8)] private static partial nint FindProperty(nint objectClass, string name);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_init")] private static partial nint InitValue(nint value, nint type);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_unset")] private static partial void UnsetValue(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_get_boolean")] private static partial int GetValueBoolean(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_boolean")] private static partial void SetValueBoolean(nint value, int on);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_get_enum")] private static partial int GetValueEnum(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_enum")] private static partial void SetValueEnum(nint value, int number);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_get_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void GetProperty(nint instance, string name, nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_set_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetProperty(nint instance, string name, nint value);
}

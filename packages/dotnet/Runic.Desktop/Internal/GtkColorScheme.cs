using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// DesktopHost and DesktopWindow.SetColorSchemeAsync on GTK 3 and GTK 4 (Runic.Desktop.Gtk4 links this file). GTK has no
// per-window scheme: the settings below, or with libadwaita its style manager, belong to the process. WebKitGTK's
// prefers-color-scheme follows gtk-application-prefer-dark-theme, which the libadwaita style manager sets too, and with
// GTK 4.20 or later gtk-interface-color-scheme. NoPreference undoes Runic's value with gtk_settings_reset_property
// (GTK 3.20 and GTK 4), so the settings follow the desktop's again, also when it changes later. GTK thread only.
internal static unsafe partial class GtkColorScheme
{
    private const string PreferDark = "gtk-application-prefer-dark-theme";
    // GTK 4.20: GtkInterfaceColorScheme UNSUPPORTED, DEFAULT, DARK, LIGHT.
    private const string InterfaceScheme = "gtk-interface-color-scheme";
    private const int InterfaceDark = 2, InterfaceLight = 3;
    private const nint GTypeBoolean = 5 << 2;
    private const int GValueSize = 24;
    // AdwColorScheme: DEFAULT, FORCE_LIGHT, PREFER_LIGHT, PREFER_DARK, FORCE_DARK.
    private const int AdwDefault = 0, AdwForceLight = 1, AdwForceDark = 4;

    // The settings before Runic changed them, for a GTK older than 3.20 without gtk_settings_reset_property: -1 until
    // the first change.
    private static int s_original = -1;

    // Applies the scheme to the GtkSettings of the default display. resetProperty is gtk_settings_reset_property of the
    // loaded GTK, or 0. Returns false when it could not.
    internal static bool Apply(nint settings, DesktopColorScheme colorScheme, nint resetProperty)
    {
        // libadwaita warns about the GTK setting and replaces it: its style manager decides, and sets the setting.
        // Its DEFAULT follows the desktop.
        if (TryApplyLibadwaita(colorScheme)) return true;
        if (settings == 0) return false;
        var applied = false;
        var follow = colorScheme == DesktopColorScheme.NoPreference;
        if (FindProperty(*(nint*)settings, InterfaceScheme) is not 0 and var pspec)
        {
            // GTK 4.20 has gtk_settings_reset_property.
            if (follow) Reset(settings, InterfaceScheme, resetProperty);
            else SetEnum(settings, InterfaceScheme, pspec, colorScheme == DesktopColorScheme.Dark ? InterfaceDark : InterfaceLight);
            applied = true;
        }
        if (!HasProperty(settings, PreferDark)) return applied;
        if (!follow && s_original < 0 && resetProperty == 0) s_original = GetBoolean(settings, PreferDark) ? 1 : 0;
        if (follow && resetProperty != 0) Reset(settings, PreferDark, resetProperty);
        else if (!follow || s_original >= 0) SetBoolean(settings, PreferDark, follow ? s_original == 1 : colorScheme == DesktopColorScheme.Dark);
        return true;
    }

    private static void Reset(nint settings, string name, nint resetProperty)
    {
        if (resetProperty == 0) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* text = bytes) ((delegate* unmanaged[Cdecl]<nint, byte*, void>)resetProperty)(settings, text);
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
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_enum")] private static partial void SetValueEnum(nint value, int number);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_get_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void GetProperty(nint instance, string name, nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_set_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetProperty(nint instance, string name, nint value);
}

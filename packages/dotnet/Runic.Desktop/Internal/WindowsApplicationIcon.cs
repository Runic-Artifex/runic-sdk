using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

/// <summary>
/// The icon of a Runic Desktop window without <see cref="DesktopWindowOptions.IconFile"/> on Windows (sdk#1207): the
/// application's own icon, the first icon group (RT_GROUP_ICON) of its executable, as Explorer shows it. Nothing else is
/// shipped for it: the build embeds the ApplicationIcon, which Runic.Platform's targets generate from the package icon
/// when the project sets none, in the managed assembly, and the SDK copies it into the application host (when it
/// builds on Windows) and NativeAOT into the native executable.
/// </summary>
/// <remarks>
/// When the process is not the application's executable, as with <c>dotnet app.dll</c>, whose process module is
/// dotnet.exe, or an application host built on another operating system that lacks the resource, the icon comes from
/// the entry assembly beside it, loaded as an image resource only.
/// </remarks>
internal static unsafe partial class WindowsApplicationIcon
{
    private const nint ResourceGroupIcon = 14;
    private const uint ImageIcon = 1;
    private const int SmCxIcon = 11;
    private const int SmCxSmIcon = 49;
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

    /// <summary>Where the last icon came from, for the native smoke: the executable, the entry assembly, or none.</summary>
    internal static string? LastSource { get; private set; }

    /// <summary>The large and small icon for a window at <paramref name="dpi"/>, which the caller destroys; zero when the
    /// application has no icon.</summary>
    internal static (nint Big, nint Small) Load(uint dpi)
    {
        var state = new LoadState
        {
            BigSize = Native.GetSystemMetricsForDpi(SmCxIcon, dpi),
            SmallSize = Native.GetSystemMetricsForDpi(SmCxSmIcon, dpi),
        };
        string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
        string? process = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        // The application's own executable (its host or NativeAOT image) is named after the entry assembly.
        if ((entry is null || string.Equals(process, entry, StringComparison.OrdinalIgnoreCase))
            && Load(Native.GetModuleHandleW(0), ref state))
        {
            LastSource = Environment.ProcessPath;
            return (state.Big, state.Small);
        }
        if (entry is not null)
        {
            string assembly = Path.Combine(AppContext.BaseDirectory, entry + ".dll");
            if (File.Exists(assembly))
            {
                nint module = Native.LoadLibraryExW(assembly, 0, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
                if (module != 0)
                {
                    try
                    {
                        if (Load(module, ref state))
                        {
                            LastSource = assembly;
                            return (state.Big, state.Small);
                        }
                    }
                    finally
                    {
                        Native.FreeLibrary(module);
                    }
                }
            }
        }
        LastSource = null;
        return (0, 0);
    }

    private static bool Load(nint module, ref LoadState state)
    {
        if (module == 0) return false;
        state.Module = module;
        fixed (LoadState* pointer = &state)
        {
            // The callback stops after the first group, so the call reports failure; the handles tell.
            _ = Native.EnumResourceNamesW(module, ResourceGroupIcon, &First, (nint)pointer);
        }
        if (state.Big != 0 && state.Small != 0) return true;
        if (state.Big != 0) Native.DestroyIcon(state.Big);
        if (state.Small != 0) Native.DestroyIcon(state.Small);
        state.Big = state.Small = 0;
        return false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int First(nint module, nint type, nint name, nint parameter)
    {
        var state = (LoadState*)parameter;
        // The name is an integer resource or a string that lives only during the callback, so both load here.
        state->Big = Native.LoadImageW(module, name, ImageIcon, state->BigSize, state->BigSize, 0);
        state->Small = Native.LoadImageW(module, name, ImageIcon, state->SmallSize, state->SmallSize, 0);
        return 0;
    }

    private struct LoadState
    {
        public nint Module;
        public int BigSize;
        public int SmallSize;
        public nint Big;
        public nint Small;
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll")]
        internal static partial nint GetModuleHandleW(nint name);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint LoadLibraryExW(string path, nint file, uint flags);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool FreeLibrary(nint module);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumResourceNamesW(nint module, nint type, delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int> callback, nint parameter);

        [LibraryImport("user32.dll")]
        internal static partial nint LoadImageW(nint instance, nint name, uint type, int width, int height, uint load);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(nint icon);

        [LibraryImport("user32.dll")]
        internal static partial int GetSystemMetricsForDpi(int index, uint dpi);
    }
}

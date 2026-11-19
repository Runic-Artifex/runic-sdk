namespace Runic.Desktop.Internal;

// The icon of a Runic Desktop window without DesktopWindowOptions.IconFile on macOS (sdk#1207). The icon is the
// application's: in a bundle with an icon (CFBundleIconFile or CFBundleIconName), such as the development bundle of
// dotnet run or a packaged .app, AppKit already shows it. Outside one, such as dotnet app.dll or a build run with
// RunicMacDevelopmentBundle=false, the Dock would show the generic icon, so the application's icon is the package icon
// that Runic.Desktop's targets copy beside the executable and name in runtimeconfig.json (Runic.Desktop.ApplicationIcon).
internal sealed partial class MacOsWkWebViewHost
{
    internal const string ApplicationIconSwitch = "Runic.Desktop.ApplicationIcon";

    // Main thread only: whether the default icon was considered; it is the application's, so once is enough.
    private static bool s_applicationIconChecked;

    /// <summary>The file set as the application icon by default, for the native smoke; null when none was.</summary>
    internal static string? AppliedApplicationIcon { get; private set; }

    // Main thread only.
    private static void ApplyApplicationIcon(nint application)
    {
        if (s_applicationIconChecked) return;
        s_applicationIconChecked = true;
        if (AppContext.GetData(ApplicationIconSwitch) is not string name || name.Length == 0) return;
        string path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path) || BundleHasIcon()) return;
        var file = Api.CreateString(path);
        var image = Api.SendNintNint(Api.SendNint(Api.GetClass("NSImage"), "alloc"), "initWithContentsOfFile:", file);
        Api.SendVoid(file, "release");
        if (image == 0) return;
        Api.SendVoidNint(application, "setApplicationIconImage:", image);
        Api.SendVoid(image, "release");
        AppliedApplicationIcon = path;
    }

    private static bool BundleHasIcon()
    {
        var bundle = Api.SendNint(Api.GetClass("NSBundle"), "mainBundle");
        if (bundle == 0) return false;
        foreach (var key in new[] { "CFBundleIconFile", "CFBundleIconName" })
        {
            var text = Api.CreateString(key);
            var value = Api.SendNintNint(bundle, "objectForInfoDictionaryKey:", text);
            Api.SendVoid(text, "release");
            if (value != 0) return true;
        }
        return false;
    }
}

using System.Runtime.InteropServices;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// The shared NSApplication for services that need one without a window, such as the status item.
internal static class MacAppKit
{
    private const nint ActivationPolicyAccessory = 1, ActivationPolicyProhibited = 2;
    private static readonly Lazy<bool> Loaded = new(static () =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _) && Class("NSStatusBar") != 0);
    private static bool s_launched;

    internal static bool IsAvailable => OperatingSystem.IsMacOS() && Loaded.Value;

    /// <summary>Returns NSApp, creating and launching it unless Runic Desktop's event loop does. Main thread only.</summary>
    /// <remarks>
    /// An unbundled process starts with the prohibited activation policy, under which it shows nothing; it becomes an
    /// accessory application, without a Dock icon, until a Desktop window makes it a regular one. A bundle with
    /// <c>LSUIElement</c> starts as an accessory application and stays one.
    /// </remarks>
    internal static nint EnsureApplication()
    {
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        if (Send(application, Sel("activationPolicy")) == ActivationPolicyProhibited)
            BoolArg(application, Sel("setActivationPolicy:"), ActivationPolicyAccessory);
        if (!s_launched)
        {
            s_launched = true;
            // Runic Desktop's event loop runs [NSApp run], which sends finishLaunching once; a second one would post the
            // launch notifications again.
            if (MacOpenEvents.LifecycleOwner() == 0 && Bool(application, Sel("isRunning")) == 0)
                Send(application, Sel("finishLaunching"));
        }
        return application;
    }
}

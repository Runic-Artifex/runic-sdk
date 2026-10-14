using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// VoiceOver opens a menu bar item's menu with its show-menu action (VO-Shift-M), which NSStatusBarButton answers only
// for an item with an assigned menu. The status item assigns its menu only while it is open, so its button becomes an
// instance of a subclass, made at run time, whose accessibilityPerformShowMenu opens the same menu as a right click.
// VoiceOver's press (accessibilityPerformPress) stays the button's action, the primary activation.
internal static unsafe partial class MacStatusButton
{
    private const string Prefix = "RunicStatusButton_";

    /// <summary>Gives the button the show-menu action. Main thread only.</summary>
    internal static void Adopt(nint button)
    {
        var type = GetClass(button);
        var name = Marshal.PtrToStringUTF8(ClassName(type)) ?? "";
        if (name.StartsWith(Prefix, StringComparison.Ordinal)) return;
        var subclass = Class(Prefix + name);
        if (subclass == 0)
        {
            subclass = AllocateClass(type, Prefix + name, 0);
            if (subclass == 0) return;
            AddMethod(subclass, Sel("accessibilityPerformShowMenu"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&PerformShowMenu,
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:" : "c@:");
            RegisterClass(subclass);
        }
        SetClass(button, subclass);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte PerformShowMenu(nint self, nint selector)
    {
        try { return MacStatusIcon.ShowMenu(self) ? (byte)1 : (byte)0; }
        catch { return 0; /* Never unwind through AppKit. */ }
    }

    [LibraryImport(ObjC, EntryPoint = "object_getClass")] private static partial nint GetClass(nint instance);
    [LibraryImport(ObjC, EntryPoint = "object_setClass")] private static partial nint SetClass(nint instance, nint type);
    [LibraryImport(ObjC, EntryPoint = "class_getName")] private static partial nint ClassName(nint type);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
}

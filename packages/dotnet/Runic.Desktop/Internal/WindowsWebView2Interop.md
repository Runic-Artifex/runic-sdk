# Windows WebView2 binding

The host calls WebView2's Win32 COM API directly. Native calls use the vtable
layout from the pinned Microsoft SDK; callbacks and environment options use
.NET source-generated COM interop. This supports NativeAOT without the legacy
`Microsoft.Web.WebView2.Core.dll` wrapper's reflection and assembly-path probes.

`Microsoft.Web.WebView2` contributes only native assets. NuGet supplies the
architecture-specific `WebView2Loader.dll` for JIT builds; it is resolved from the
application directory (RID publish) or its `runtimes/win-*/native` subdirectory
(portable build). The native loader discovers the installed WebView2 runtime.
No managed WebView2 assembly or WPF/WinForms control is required.

Windows x64/ARM64 NativeAOT publishes instead link the pinned SDK's
`WebView2LoaderStatic.lib` through Runic's transitive targets and direct P/Invoke
imports. The published executable needs no adjacent loader DLL and does not
extract one at startup. This applies to package consumers as well as source
project references. The Edge WebView2 Runtime must still be installed; linking
the small loader does not embed the browser engine. Normal JIT builds retain
their native DLL deployment. Loader failures are reported separately from a
missing runtime.

The window thread initializes COM as STA and owns the controller, environment,
event subscriptions, and their release. Creation callbacks borrow their result
pointers, so they add one reference before completing the managed task. Native
getters return owned pointers or CoTaskMem strings, which are released explicitly.
Event registration retains both the native registration and our generated COM
reference until removal. Callback exceptions become failing HRESULTs.

When updating the SDK, review its `WebView2.h` and
`WebView2EnvironmentOptions.h`, including `CORE_WEBVIEW_TARGET_PRODUCT_VERSION`
in our environment options. Tests compare called vtable positions with the
pinned header and exercise generated callbacks, strings, and reference ownership.
The Windows CI job also publishes and runs the embedded-window smoke from a
directory containing only its NativeAOT executable, with warnings treated as
errors during publication. Linux tests cannot certify Windows
window behavior.

References: [WebView2 native API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/)
and [.NET source-generated COM](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation).

Static linking follows [Microsoft's WebView2 distribution guidance](https://learn.microsoft.com/microsoft-edge/webview2/how-to/static)
and [.NET NativeAOT direct P/Invoke guidance](https://learn.microsoft.com/dotnet/core/deploying/native-aot/interop).

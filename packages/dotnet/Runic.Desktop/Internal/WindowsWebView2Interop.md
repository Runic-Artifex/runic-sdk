# Windows WebView2 binding

The host calls WebView2's Win32 COM API directly. Native calls use the vtable
layout from the pinned Microsoft SDK; callbacks and environment options use
.NET source-generated COM interop. This supports NativeAOT without the legacy
`Microsoft.Web.WebView2.Core.dll` wrapper's reflection and assembly-path probes.

`Microsoft.Web.WebView2` contributes only native assets. NuGet supplies the
architecture-specific `WebView2Loader.dll`; the loader is resolved from the
application directory (RID publish) or its `runtimes/win-*/native` subdirectory
(portable build). The native loader discovers the installed WebView2 runtime.
No managed WebView2 assembly or WPF/WinForms control is required.

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
The Windows CI job also publishes and runs the embedded-window smoke with
NativeAOT and warnings treated as errors. Linux tests cannot certify Windows
window behavior.

References: [WebView2 native API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/)
and [.NET source-generated COM](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation).

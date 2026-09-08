# Linux clipboard native conformance

Build and run in the locked SDK environment with a fresh X11 display, for example
`GDK_BACKEND=x11 GSETTINGS_BACKEND=memory xvfb-run -a dotnet run --project tests/dotnet/Runic.Platform.Linux.Tests -c Release -- --native`.
Always select X11 explicitly: inherited `GDK_BACKEND=wayland` otherwise overrides
Xvfb and uses the host compositor. Without `--native`, the executable runs only
portable owner-race checks and never initializes GTK.
The test creates GTK directly on a dedicated owner thread, uses the shipping
provider and reads the selection in an independent process using GTK's native API.
It checks absent/empty text, Unicode, length bounds, canceled writes, cancellation
after native acquisition, canceled read drain/retry and ownership replacement.

Publish this same executable with `-r linux-x64 -p:PublishAot=true` for NativeAOT;
run the published executable with `--native` under the same explicit display profile. Keep source revision,
executable/dependency hashes, toolchain and display profile with the result. A
headless X11 result does not certify Wayland, portal file selection, a screen
reader, focus restoration or an installed Desktop application. No selected files
are simulated by this test. The prototype native-host acceptance executable owns
real dialog cancellation and interactive selected-file testing against the shipping
picker.

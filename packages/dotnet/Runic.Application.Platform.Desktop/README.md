# Runic.Application.Platform.Desktop

Use `services.AddRunicDesktopPlatform(() => host.Window, owner => new PlatformProvider
{ Files = /* explicitly selected picker */, Clipboard = /* explicitly selected clipboard */ })`
when configuring the bridge presentation. Reference the desired OS provider package in
the application. This integration has no implicit OS provider dependencies.

`DesktopNativeOwner` binds the first available embedded window identity and verifies it
again inside each native callback. Reconnect preserves identity; replacement invalidates
old operations. Browser-backed presentations report native services unavailable. Native
handles remain in trusted C# callbacks and cannot be supplied by frontend requests.

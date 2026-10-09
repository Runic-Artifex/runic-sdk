# Runic.Platform

Host-independent native-service contracts for Runic applications.

Preview API targeting .NET 10. Native providers are explicitly selected; this package has no Desktop or ASP.NET Core dependency.

Services return `PlatformResult<T>`: `Success`, `Unavailable` with a
`PlatformUnavailableReason`, or `Failed` with a `PlatformFailureCode`. Operations
without a value return `PlatformResult<PlatformUnit>`. The type names carry the
`Platform` prefix so they do not collide with `System.Reactive.Unit` and similar
names when both namespaces are imported.

Branch on the failure code. When a provider observed a native error, `Failed.Diagnostic`
carries a `PlatformDiagnostic` with its domain (`HRESULT`, `Win32`, `errno`, an
`NSError`/`GError` domain or a D-Bus error name), numeric code and, where the operating system's error table describes the code, its
message. It is for logs and support, not display. Free-form text from native services
(GError and D-Bus messages, NSError descriptions) is left out, so a diagnostic contains
no paths.

`IFileDialogs.OpenDirectoryAsync(new OpenDirectoryOptions())` returns a
`PickerResult<IDirectoryLease>` for one existing local directory. Keep the selected
lease alive while C# filesystem operations use `LocalPath`; it retains native
access until disposal or presentation close. The path preserves the selected
identity, including Unicode and escaped filename characters. Native paths that
cannot be represented exactly in C# are refused. Leases and native grants remain
in C#; expose only application DTOs to a frontend.

User dismissal returns `Dismissed`, caller cancellation throws
`OperationCanceledException`, and presentation shutdown returns
`Unavailable(OwnerClosed)`. The `platform.directories.open` capability reports
availability when using `PresentationFiles`. Existing file-only implementations
remain compatible and report directory selection unavailable.

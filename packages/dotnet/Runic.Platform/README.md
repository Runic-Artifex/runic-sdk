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
`NSError`/`GError` domain or a D-Bus error name), numeric code and native message for
logs and support. It is not intended for display and never contains paths.

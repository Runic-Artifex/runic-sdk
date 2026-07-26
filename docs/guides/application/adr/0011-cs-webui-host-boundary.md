# ADR 0011: cs-webui is the desktop host boundary

- Status: Accepted
- Date: 2026-07-26

## Context

WebUIToolkit is intended to build desktop applications on the external
[`CsWebUi`](https://www.nuget.org/packages/CsWebUi) binding for WebUI. The first
hosting implementation accidentally acquired `Microsoft.AspNetCore.App` shared
framework references because it used Microsoft.Extensions hosting and dependency
injection types. That made the package graph imply that WebUIToolkit hosted an
ASP.NET Core web application.

Microsoft.Extensions.Hosting and Microsoft.Extensions.DependencyInjection are
useful composition libraries, but neither requires ASP.NET Core. CsWebUi already
owns the native browser or webview window, local asset serving, JavaScript-to-.NET
callbacks, navigation, and process-wide wait/cleanup lifecycle.

## Decision

- `WebUIToolkit.Hosting.CsWebUi` is the first-party desktop host adapter. It
  translates the dependency-neutral browser contracts into `WebUiWindow` and
  `WebUiApplication` operations.
- `WebUIToolkit.Hosting.WebUi` owns toolkit session, static-asset, and MVVM
  transport composition. Its name describes the toolkit's UI mode, not an
  ASP.NET Core server.
- `WebUIToolkit.Hosting.GenericHost` may compose
  `Microsoft.Extensions.Hosting`, and `WebUIToolkit.Hosting.WebUi` may use
  dependency-injection abstractions. They reference those NuGet packages
  directly and do not reference the `Microsoft.AspNetCore.App` shared framework.
- Desktop samples use local HTML, CSS, and JavaScript served by CsWebUi and bind
  browser events directly to C# application state. An HTTP or ASP.NET Core
  adapter, if introduced later, is optional and separately named.
- The Nix development shell supplies the native WebUI library and Linux browser
  and webview runtime dependencies required by CsWebUi.

## Consequences

The normal application path is a native CsWebUi process rather than a localhost
ASP.NET Core service. Hosting contracts remain unit-testable without launching a
browser, while the concrete adapter owns the unavoidable native and
process-global behavior. HTMX support remains a view/transport integration and
does not redefine the desktop host.

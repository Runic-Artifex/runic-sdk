# Choose a presentation host

The current `Runic.Application.Templates` starter uses the CS-WebUI integration
for its .NET Window and View composition. Install a matching template version,
then choose React, Vue, Svelte, or Angular with the corresponding template name.
For a starter walkthrough, see [getting started](../application/getting-started/README.md)
and the [first Window example](../../../examples/first-window/README.md).

[`Runic.Application.Views.CsWebUi.DependencyInjection`](../../../packages/dotnet/Runic.Application.Views.CsWebUi.DependencyInjection/README.md)
connects the generated Window and View contracts to a CS-WebUI presentation.
CS-WebUI remains a separately maintained upstream product, and applications can
also use its lower-level API directly.

`Runic.Desktop` is an independent presentation library for browser and embedded
WebView windows. Choose its embedded backend and fallback policy explicitly; its
[package guide](../../../packages/dotnet/Runic.Desktop/README.md) documents native
prerequisites. Do not assume a template's CS-WebUI composition changes to Desktop
when a provider is installed.

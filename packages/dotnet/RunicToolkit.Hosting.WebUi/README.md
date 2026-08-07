# RunicToolkit.Hosting.WebUi

`RunicToolkit.Hosting.WebUi` composes dependency-neutral browser contracts with a
scoped root session and a deterministic manifest-backed static-asset endpoint.
It uses only `Microsoft.Extensions.DependencyInjection.Abstractions` for its optional
scoped-session bridge. It is not an ASP.NET Core web application host.

The package deliberately does not discover or load a native browser runtime. Use the
first-party `RunicToolkit.Hosting.CsWebUi` adapter, create a `WebUiModeRunner`, and add
the matching `FrontendAssetValidator` for `LaunchKind.UserInterface`. This keeps command
launches from resolving or initializing UI services.

HTMX and CsWebUi remain separate adapters around these closed static-asset,
root-session, and stop-notification seams.

Application protocol ownership belongs to `RunicToolkit.ApplicationBridge`; its
CsWebUi adapter remains a separate package so this hosting layer stays independent
of any application contract or rendering framework.

# Architecture

WebUIToolkit is centered on the native CsWebUi process boundary:

1. CsWebUi owns native windows, the embedded browser, and JavaScript bindings.
2. `WebUiAppBuilder` owns common application hosting and lifecycle.
3. Frontend packages add framework-specific extension members to that shared
   builder.
4. Applications choose compiled cwhtml/HTMX or the binary MVVM client; neither
   path requires ASP.NET Core.
5. Generated, closed contracts preserve trimming and Native-AOT compatibility.

The core runtime remains styling-neutral. Bootstrap 5.3 and Font Awesome are
sample and customer-migration defaults, not dependencies of MVVM, Flow,
Hosting, or the transport contracts.

## Authoritative decisions

- [ADR 0001](../adr/0001-webuitoolkit-identity.md) — identity and namespace
  ownership.
- [ADR 0002](../adr/0002-dependency-direction.md) — dependency direction.
- [ADR 0006](../adr/0006-target-framework-policy.md) — target-framework policy.
- [ADR 0007](../adr/0007-hosting-lifecycle-policy.md) — hosting lifecycle.
- [ADR 0011](../adr/0011-cs-webui-host-boundary.md) — CsWebUi host boundary.
- [ADR 0012](../adr/0012-native-html-and-frontend-direction.md) — current
  frontend and native HTML direction.

Earlier workflow and wave ADRs remain valid historical records where they have
not been superseded, but the [current roadmap](../roadmap/README.md) owns work
ordering. The [contract registry](../contracts/registry.md) owns versioned
identities.

See the [complete ADR index](../adr/README.md) for every recorded decision.

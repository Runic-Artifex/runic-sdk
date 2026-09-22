# Runic Application documentation

Runic Application provides reusable .NET application hosting, desktop capability
contracts, application bridge infrastructure, frontend SDK infrastructure,
framework adapters, and application developer tools.

- [Architecture](architecture/README.md)
- [Application Bridge direction](architecture/application-bridge.md)
- [Getting started](getting-started/README.md)
- [Frontend contracts](guides/frontend-contracts.md)
- [Framework adapters](guides/frontend-frameworks.md)
- [Migrate from CS-WebUI to Runic Desktop](guides/migrate-cs-webui-to-runic-desktop.md)
- [Reference](reference/README.md)
- [Development](contributing/development.md)
- [Quality gates](contributing/quality-gates.md)
- [Architecture decisions](adr/README.md)

Runnable applications live in
[`examples`](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples).
Application, Desktop, Assets, Translations, and Command Line are developed in
the SDK monorepo and publish as one coordinated package set. CS-WebUI is
maintained independently; Flow is archived.

## Current host and dependency inventory

The maintained CS-WebUI surface is intentionally small and explicit:

| Surface | Current evidence |
| --- | --- |
| SDK adapter | `Runic.Application.CsWebUi`, covered by `tests/dotnet/Runic.Application.CsWebUi.Tests` for startup readiness and presentation shutdown. |
| Application templates | The React, Vue, Svelte, and Angular templates under `tools/Runic.Application.Templates/content` all retain the `RunicHost=cswebui` option and reference the same adapter package. They are host variants of the application template, not separate CS-WebUI products. |
| Maintained example | `examples/document-migration` builds the CS-WebUI host, exercises the bridge and dirty-close behavior, and checks that the host does not pull Desktop or ASP.NET Core dependencies. It is evidence and a migration reference, not a separately released application. |
| Upstream runtime | The independently maintained `CsWebUi` package remains the native runtime boundary. The SDK adapter composes it and does not claim ownership of its release. |

`Tmds.DBus.Protocol` is a runtime dependency of
`Runic.Platform.Linux.Portal`; `Tmds.DBus.Generator` is a private build-time
dependency that produces the bundled portal bindings from pinned upstream XML.
Neither Tmds package is a Runic product, template, or standalone release
identity. This inventory records implementation ownership only and does not
create a new release offering.

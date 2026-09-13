# Application architecture

`Runic.Application` owns the generated composition manifest and application
lifecycle. The manifest is the authority consumed by hosts, tooling, testing and
publishers; hosts do not reconstruct it from separate configuration.

The Application Bridge is the typed, revisioned boundary between an application
and its browser presentation. It owns the generated contract, validation,
sessions, reconnect handling, cancellation and command/event semantics. C# is
the default contract authority; an explicit whole-contract Effect model remains
available. Framework packages project the controller into React, Vue, Svelte or
Angular without acquiring protocol authority.

Hosts adapt that application core without changing its domain commands or bridge
contract:

- `Runic.Application.Desktop` composes a Runic Desktop presentation.
- `Runic.Application.CsWebUi` composes the independently packaged CS-WebUI
  runtime and has no Desktop or ASP.NET Core dependency.
- `Runic.Application.Hosting` integrates a deliberately application-owned Generic
  Host or local Application Bridge WebSocket endpoint.

`Runic.Assets` owns verified frontend asset manifests. Native platform services
are explicit, presentation-scoped providers: they retain native handles, leases
and paths in C# and report capability availability without exposing those resources
to the frontend. See [Platform service architecture](os-integration-rfc.md) and
the [platform package](../../../../packages/dotnet/Runic.Platform/README.md).

SDK products own their integration packages within this monorepo. Integrations
depend on the cores they connect; cores do not depend back on adapters. CS-WebUI
remains independent, and cross-repository composition is verified through packed
NuGet/npm consumers rather than source references.

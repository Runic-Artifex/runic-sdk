# Architecture

The dependency direction is deliberately inward:

1. `Runic.Application` and the Application Bridge
   contract kernel define framework-neutral contracts.
2. Effect Schema and the canonical bridge manifest define encoded wire data;
   generators consume committed artifacts and never start Node during C# builds.
3. Presentation frameworks consume one framework-neutral controller.
   Framework integrations provide idiomatic lifecycle
   projections without owning protocol state.
4. `Runic.Application.Desktop` and `Runic.Application.Hosting` adapt the
   application core to native Desktop and local WebSocket presentation.
5. `Runic.Assets` turns an explicitly built frontend directory
   into a verified application asset manifest.
6. React, Vue, Svelte, and Angular own only their
   presentation state and framework lifecycle; the bridge owns validation,
   transport, revisions, reconnects, cancellation, and command semantics.

SDK products own their integration packages within this monorepo. Integrations
depend on the cores they connect; those cores do not depend back on adapters.
CS-WebUI remains independent, and Flow is archived.

Cross-domain source references inside this repository are declared in
`eng/workspace.json`. Cross-repository composition is verified through packed
NuGet/npm consumers rather than source references.

For implemented platform services and their host boundaries, see the
[platform package](../../../../packages/dotnet/Runic.Platform/README.md). The
[OS integration RFC](os-integration-rfc.md) retains the historical design context.

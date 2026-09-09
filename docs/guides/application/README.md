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

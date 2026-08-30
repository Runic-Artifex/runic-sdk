# Current Svelte template acceptance

This package-only consumer generates exactly one `runic-app-svelte` project from
the current local NuGet candidate feed. It installs the current Runic npm
candidates from a loopback scoped registry, builds the Svelte/Vite frontend,
runs the deterministic C# smoke test, and inspects the generated
`runic.application/1` manifest twice.

Provide a composed local NuGet feed and the three freshly packed npm archives:

```bash
RUNIC_CURRENT_SVELTE_TEMPLATE_NUGET_FEED=/path/to/feed \
RUNIC_CURRENT_SVELTE_TEMPLATE_APPLICATION_VERSION=0.2.0-preview.example \
RUNIC_CURRENT_SVELTE_TEMPLATE_NPM_ARCHIVES=/path/bridge.tgz,/path/svelte.tgz,/path/vite.tgz \
node eng/current-svelte-template/verify.mjs run-twice ../.github/runic.release.json
```

The receipt intentionally records only the logical local-feed identities and
candidate hashes, never an ephemeral loopback port. Public non-Runic frontend
dependencies remain outside the Runic candidate provenance claim.

![Runic Artifex Documentation banner](.github/assets/brand/banner.png)

# Runic Artifex Documentation

The documentation portal for the independent Runic Artifex projects and the
explicit integration seams between them.

The portal source and live site are public while the first package preview train
awaits registry publication. A successful documentation deployment is not
authorization to publish packages or claim that registry artifacts are live.

The portal is built with SvelteKit and Svelte 5. It is fully prerendered with
`@sveltejs/adapter-static` and served by the Runic Artifex NixOS VPS at
`https://docs.runic-artifex.eu`. The same source produces the canonical landing
page served at `https://runic-artifex.eu`; `https://www.runic-artifex.eu`
redirects to the apex.

## Develop locally

Use Node.js 24 and npm:

```bash
npm ci
npm run dev
```

The terminal prints the local portal URL (normally `http://localhost:5173`).

## Verify

```bash
npm audit --omit=dev --audit-level=high
npm run lint
npm run check
npm test
```

`npm test` creates the production build and verifies important rendered routes
and release information.

## Build for the VPS

The production build is emitted to `build/`. Every documentation route is
prerendered as an `index.html`, and gzip/Brotli variants are produced for nginx.
The server flake builds this repository twice with `RUNIC_SITE_ORIGIN`: once for
the apex landing page and once for the documentation origin. Production
deployments are reproducible and do not install Node.js dependencies at runtime.

## Content model

- `/getting-started` introduces the product family.
- `/products` owns product and integration documentation.
- `/architecture` records dependency direction and ownership rules.
- `/packages` is the NuGet/npm catalog.
- `/releases` describes the coordinated first preview train.

## Package publication gate

Packages may go public only after:

1. every product's exact release workflow succeeds on `main`;
2. NuGet and npm trusted publishers are configured;
3. the npm organization/scope is under Runic Artifex control;
4. documentation links and install commands are verified against public
   registries; and
5. the launch is explicitly approved.

## Maintainer release workflow

The public release page summarizes availability. Maintainers use the following
runbook to prepare and publish the artifacts behind that status:

1. Finish product changes and select immutable release inputs from green
   `main` branches.
2. Restore, test, and pack from those exact commits without sibling-source
   dependencies. Run frontend checks, isolated downstream-consumer tests, and
   applicable NativeAOT publication and execution gates.
3. Review the documentation against the exact artifacts, including identifiers,
   versions, links, and install commands.
4. Publish those same verified artifacts through protected GitHub environments
   and OIDC trusted publishers for NuGet and npm. If npm requires a first
   package record before trusted publishing can be configured, use a
   short-lived bootstrap token and revoke it immediately afterward.

Committed npm lockfiles are release inputs. Dependency changes update them in a
reviewed source change. `npm ci` consumes those lockfiles, so release jobs do not
re-resolve dependencies. Ephemeral `npm version` stamping may update manifest
and lockfile metadata for the candidate version.

The first-preview order is intentionally narrow:

- Runic Command Line can publish independently.
- Runic Translations can publish its NuGet and npm artifacts independently.
- Runic Translations Editor follows Runic Translations.
- The Svelte and Vite integrations precede the Toolkit public-template gate.
- Runic Toolkit follows both integrations.
- Runic Assets and Runic Flow follow Runic Toolkit.

## License

The portal source and documentation are licensed under the MIT License.

# Releasing Runic Svelte

The `CI` workflow builds and validates `@runic-artifex/svelte` and
`@runic-artifex/sveltekit` as one exact-version family with Bun. Pull requests
pack candidates locally. Branch pushes publish private, immutable GitHub
Packages candidates using the coordinate
`1.0.0-ci.sha<first-16-characters-of-the-commit>`, then install those exact
versions in a clean smoke-test project.

Rerunning the same revision reuses a registry candidate only when its tarball
digest matches. A coordinate collision with different bytes fails the workflow.
GitHub Packages access must be granted to every repository that consumes these
candidates.

The `Public release` workflow is dispatch-only. Every dispatch requires an
explicit version and rebuilds the package files with the same Bun lockfile and
packager used by CI. When publication is approved, those exact bytes are first
stored privately in GitHub Packages with the `release-staging` tag. The public
job downloads and revalidates that immutable staging version before npm
publication; it does not rebuild or use an Actions artifact as a package
handoff.

Run a verify-only dispatch first and retain its two tarballs and `SHA256SUMS`.
Publication is accepted only from `main`, after the exact `PUBLISH PUBLIC`
confirmation and approval from the `public-release` environment.

Before the first public release:

1. make the repository public and create the `public-release` environment with
   a required reviewer and a `main` deployment policy;
2. add a short-lived npm granular access token as environment secret
   `NPM_BOOTSTRAP_TOKEN`, limited to the `@runic-artifex` scope, and publish both
   packages with `npm_bootstrap` enabled;
3. configure npm trusted publishing separately for `@runic-artifex/svelte` and
   `@runic-artifex/sveltekit`, using this repository,
   workflow filename `public-release.yml`, environment `public-release`, and
   the `npm publish` allowed action;
4. delete `NPM_BOOTSTRAP_TOKEN`; all later releases use OIDC with
   `npm_bootstrap` disabled.

The workflow verifies the downloaded checksums and preflights both package
identities before publishing either. Retries skip an existing package only when
npm's recorded SHA-512 integrity matches the verified tarball. Prerelease
versions use the `preview` dist-tag; stable versions use `latest`.

Do not create a release tag until publication has passed for that exact version.

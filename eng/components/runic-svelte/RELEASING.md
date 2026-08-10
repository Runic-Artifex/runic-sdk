# Releasing Runic Svelte

The `Public release` workflow builds and validates
`@runic-artifex/svelte` and `@runic-artifex/sveltekit` as one exact-version
family. Every dispatch requires an explicit version. The next planned private
candidate is `0.1.0-preview.8.1`; this is a planning value, not a claim that the
candidate has been verified or published.

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

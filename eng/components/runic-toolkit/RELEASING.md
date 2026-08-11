# Releasing Runic Toolkit

The `Public release` workflow builds one versioned family containing 15 NuGet
packages and `@runic-artifex/application-bridge` on npm. Every dispatch requires
an explicit exact version. The next planned private candidate is
`0.1.0-preview.27.1`; this is a planning value, not a claim that the candidate
has been verified or published.

The candidate build also consumes the planned Runic Svelte and Vite candidates
(`0.1.0-preview.14.1`) through the template acceptance gate. Publish those private
candidates first, then retain the Toolkit artifact and `SHA256SUMS` file from a
verify-only dispatch on the final `main` commit.

Publication is accepted only from `main`, after the exact `PUBLISH PUBLIC`
confirmation and approval from the `public-release` environment. Before the
first public release:

1. complete and publish the product documentation, make the repository public,
   and add a required reviewer plus a `main` deployment policy to the
   `public-release` environment;
2. configure NuGet trusted publishers for owner `Runic-Artifex`, repository
   `runic-toolkit`, workflow `public-release.yml`, and environment
   `public-release`, then set `NUGET_USER` to the matching nuget.org account;
3. add a short-lived npm granular access token as environment secret
   `NPM_BOOTSTRAP_TOKEN`, limited to the `@runic-artifex` scope, and publish the
   first version with `npm_bootstrap` enabled;
4. configure npm trusted publishing for `@runic-artifex/application-bridge`
   using this repository, `.github/workflows/public-release.yml`, and environment
   `public-release`; and
5. delete `NPM_BOOTSTRAP_TOKEN`; all later releases use OIDC with
   `npm_bootstrap` disabled.

The workflow verifies artifact digests after download, rejects a new dispatch
that reuses an existing NuGet version, and permits a rerun of the same workflow
artifact to skip a partial NuGet push. npm retries skip an existing package only
when its registry integrity matches the verified candidate.

Do not create a release tag until publication has passed for that exact version.

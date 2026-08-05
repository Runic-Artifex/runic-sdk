# Releasing Runic Toolkit

The `Public release` workflow builds, consumes, and validates the eighteen
NuGet packages and six npm packages as one independently versioned family.
Verify-only dispatches are safe on any branch. Publication is accepted only
from `main`, after the exact `PUBLISH PUBLIC` confirmation and the
`public-release` environment's `main` deployment policy. Add a required reviewer
when the repository becomes public.

Before the first public release:

1. complete and publish the product documentation, then make this repository public;
2. create NuGet trusted-publisher policies for owner `Runic-Artifex`, repository
   `runic-toolkit`, workflow `public-release.yml`, and environment `public-release`;
3. set the environment variable `NUGET_USER` to the nuget.org account;
4. verify control of the npm scope `@runic-artifex`;
5. add a narrowly scoped, short-lived `NPM_BOOTSTRAP_TOKEN` environment secret and
   run the first publication with `npm_bootstrap` enabled;
6. configure npm trusted publishing for all six packages using this repository,
   `public-release.yml`, and environment `public-release`; disable token publishing
   for each package and remove the bootstrap secret.

Every later npm release must leave `npm_bootstrap` disabled so npm uses OIDC and
generates provenance from the public GitHub repository.

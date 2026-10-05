# Runic documentation

[docs.runic-artifex.eu](https://docs.runic-artifex.eu) is built from this directory.
Start using Runic with the [getting-started guide](guides/application/getting-started/README.md).
The [guide index](guides/README.md) contains detailed API and architecture material.

## Work on the docs

Use the SDK's locked development environment. From the SDK root:

```sh
bun run bootstrap
bun run dev:docs
bun run --cwd docs check
bun run --cwd docs lint
bun run --cwd docs test
```

The focused docs test builds the static site and checks routes, links, installation
commands and translation schemas. The build needs no GitHub access or sibling
repositories. Deploy `docs/build/` to `docs.runic-artifex.eu`; preserve the apex
`runic-artifex.eu/schemas/translations/*` routes to the schema mirror in
`public/schemas/translations`.

## Next release

After publishing an SDK release, from the SDK root run:

```sh
bun run docs:release <published-version>
```

This uses the GitHub CLI to read the published release's tagged SDK package
inventory and updates `src/lib/active-sdk-release.json`. Review and commit that
file, update any SDK guides affected by API changes, then deploy the docs. A
development version bump does not change the public catalog, and new packages
appear only after they have shipped. `published-release.json` is the immutable
0.6.0-preview.1 unified catalog; it remains release history after Command Line
and Translations become independent.

Runic Translations owns its guides and canonical schemas in
[`runic-translations-sdk`](https://github.com/Runic-Artifex/runic-translations-sdk).
Before deploying a changed schema mirror, synchronize it from the pinned source
revision in `sources/translations-schemas.json`. The docs build consumes only the
checked-in mirror, so it remains reproducible without a sibling repository or
network access.

The marketing site links here for versions and installs; routine package releases
need no marketing-site edits. GitHub release notes own version-specific changes,
migration instructions and known issues. Historical guides stay labeled as such.

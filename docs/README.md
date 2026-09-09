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
`runic-artifex.eu/schemas/translations/*` routes to the same schema files.

## Next release

After publishing the SDK, from the SDK root run:

```sh
bun run docs:release <published-version>
```

This uses the GitHub CLI to read the published release's tagged package inventory
and updates `src/lib/published-release.json`. Review and commit that file, update
any guides affected by API changes, then deploy the docs. Version labels, package
links and installation commands all use this one snapshot. A development version
bump does not change the public catalog, and new packages appear only after they
have shipped. No generated code, receipt file or release-policy pin needs updating.

The marketing site links here for versions and installs; routine package releases
need no marketing-site edits. GitHub release notes own version-specific changes,
migration instructions and known issues. Historical guides stay labeled as such.

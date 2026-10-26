# Documentation ownership

The shared Runic documentation portal and portal guides now live in
[runic-site/docs](https://github.com/Runic-Artifex/runic-site/tree/main/docs),
published at [docs.runic-artifex.eu](https://docs.runic-artifex.eu/).

Develop and update the portal in that repository. SDK package READMEs and
specifications remain in this repository; the portal imports a small pinned
template snapshot rather than building from SDK source. Runic Translations
maintains its canonical consumer guides in
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk/tree/main/docs/guides/translations).

Historical SDK tags retain their original documentation and URLs. Existing
public portal routes and canonical apex schema URLs remain unchanged.
See the [deployment cutover plan](https://github.com/Runic-Artifex/runic-site/blob/main/docs/plans/documentation-ownership.md).

The [diagnostics catalog](diagnostics.md) describes every diagnostic ID that an
SDK package reports, other than the experimental API IDs. Diagnostics link to it at the release tag of the package.

[Experimental APIs](experimental.md) lists the experimental diagnostic IDs
and how to suppress them. The diagnostics link to it at the release's tag.

The [security guide](security.md) is for authors of Runic applications: what Runic
protects by default, which options relax it, and what an application checks itself.

The [packaging guide](packaging.md) covers `dotnet runic package`: what each
format needs, and how you sign the packages with your own credentials.

The [MSBuild reference](msbuild.md) lists every MSBuild property and item that
the SDK packages and tools read, with links to where each is described.

The [configuration guide](configuration.md) shows how an application configures
itself with Generic Host and `Microsoft.Extensions.Configuration`, ships
`appsettings.json`, and stores per-user settings; Runic has no configuration API
of its own.

The [migration guide](migration-1.0.md) takes an application from 0.9 to 1.0:
what to change for each breaking or behaviour change of 0.10.0, 0.11.0 and
0.12.0.

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

[Experimental APIs](experimental.md) lists the experimental diagnostic ID
(`RUNICWPF001`) and how to suppress it. The diagnostics link to it at the release's tag.

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

The [hosted regions guide](hosted-regions.md) shows a guest application's Window
in a region of a host page, in a frame or as a component, with the security
notes and troubleshooting for both.

The [debugging guide](debugging.md) debugs an application end to end under
`dotnet runic dev`: the IDE's .NET debugger, the browser's developer tools of
each engine, the Runic dock, Runic Inspect, traces and the Bridge analyzers.

The [tracing guide](tracing.md) shows the C# handler timing in the browser's
Performance panel under `dotnet runic dev`, and one trace from a click in the
page to the C# it runs in a local viewer such as the Aspire dashboard.

The [side panel guide](side-panel.md) opens a second page beside a window's
page, growing the window instead of covering the page, with the fallback where
the window cannot grow and each platform's limits.

The [security guide](security.md) is for authors of Runic applications: what Runic
protects by default, which options relax it, and what an application checks itself.

The [packaging guide](packaging.md) covers `dotnet runic package`: what each
format needs, and how you sign the packages with your own credentials.

The [MSBuild reference](msbuild.md) lists every MSBuild property and item that
the SDK packages and tools read, with links to where each is described.

The [environment variables reference](environment.md) lists the `RUNIC_*`
variables that the SDK packages and tools read or set: which ones are settings,
which are handshakes between Runic processes, and which take effect only during
development.

The [configuration guide](configuration.md) shows how an application configures
itself with Generic Host and `Microsoft.Extensions.Configuration`, ships
`appsettings.json`, and stores per-user settings; Runic has no configuration API
of its own.

The [migration guide](migration-1.0.md) takes an application from 0.9, or from
a 0.7 or 0.8 preview, to 1.0: what to change for each breaking or behaviour
change since then.

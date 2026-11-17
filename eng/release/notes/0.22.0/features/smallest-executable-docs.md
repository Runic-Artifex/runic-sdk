## Making the executable smaller (docs)

The packaging guide has a new section,
[Making the executable smaller](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/packaging.md#making-the-executable-smaller):
what Runic already does for a trimmed or NativeAOT publish, and four opt-in .NET
settings with their measured savings and what the application gives up
(`StackTraceSupport=false`, `InvariantGlobalization=true`,
`OptimizationPreference=Size`, `UseSystemResourceKeys=true`), and how to measure
an application with `dotnet runic size` and an `.mstat` file. The templates are
unchanged.

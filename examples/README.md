# Examples

Runnable Runic applications, from the smallest Window to multi-window
applications. Each uses explicit .NET Window and View types and generated
TypeScript clients. To start a new application, use the
[project templates](https://docs.runic-artifex.eu/getting-started/) instead.

- [First Window](first-window/README.md) introduces one Window, ViewModel, and frontend client.
- [First Window on Runic Desktop](first-window-desktop/README.md) hosts the same counter with Runic Desktop and ReactiveUI.
- [CommunityToolkit Notes](notes-view-first/README.md) shows nested content, View scopes, and typed writes.
- [Reactive Notes](notes-reactive-views/README.md) demonstrates ReactiveUI routing and multiple Views over a model.
- [DynamicData](dynamicdata/README.md) presents a 100,000-row cache through independent, keyed viewports.
- [Command line](command-line/README.md) is an independent CLI example.

The examples build the Runic packages from source with `ProjectReference` and
`Import` lines, so they run only inside this repository. To copy one into your
own project, replace those lines with package references; the
[First Window README](first-window/README.md#copy-it-into-your-own-project)
shows how.

Run the focused example checks from the repository root using the commands in
each example README. The template acceptance suite separately packs the
published templates and verifies React, Vue, Svelte, and Angular consumers:

```sh
bun run pack
bun run verify:templates
```

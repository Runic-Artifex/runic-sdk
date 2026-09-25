# Examples

These examples exercise the current Runic Application Views model from package
source. Each uses explicit .NET Window and View contracts and generated
TypeScript clients.

- [First Window](first-window/README.md) introduces one Window, ViewModel, and frontend client.
- [Toolkit Notes](notes-view-first/README.md) shows nested content, View scopes, and typed writes.
- [Reactive Notes](notes-reactive-views/README.md) demonstrates ReactiveUI routing and multiple Views over a model.
- [Command line](command-line/README.md) is an independent CLI example.

Run the focused example checks from the repository root using the commands in
each example README. The template acceptance suite separately packs the
published templates and verifies React, Vue, Svelte, and Angular consumers:

```sh
bun run pack
bun run verify:templates
```

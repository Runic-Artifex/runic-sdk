# Examples

From the repository root, run `bun run example:counter`. This headless example exercises
member discovery, generated codecs, scoped dependency injection, initialization, and
command dispatch without requiring a native webview.

For complete React, Vue, Svelte, and Angular applications, the maintained source is
`tools/Runic.Application.Templates/content`. Run
`bun run verify:templates` after `bun run pack` to create fresh applications from the
packed templates and build them with npm, pnpm, and Bun. This tests the public installation
path separately from workspace project references.

The [customer migration](customer-migration/README.md) follows a WPF/CommunityToolkit
editor through an idiomatic Runic feature and a native React host.

The [document migration](document-migration/README.md) preserves a MAUI-derived
text-document model while replacing MVVM orchestration with commands and frontend drafts.

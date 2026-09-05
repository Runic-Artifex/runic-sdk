# Examples

From the repository root, run `bun run example:counter`. This headless example exercises
member discovery, generated codecs, scoped dependency injection, initialization, and
command dispatch without requiring a native webview.

For complete React, Vue, Svelte, and Angular applications, the maintained source is
`tools/RunicToolkit.Templates/content`. Run
`bun run verify:templates` after `bun run pack` to create fresh applications from the
packed templates and build them with npm, pnpm, and Bun. This tests the public installation
path separately from workspace project references.

The imported `tests/fixtures/legacy-examples/samples` tree and its old package manifest/lockfiles are historical
fixtures. They are excluded from the Bun workspace and aggregate solutions. Their original
history and verification receipts remain available for reference.

The [customer migration](customer-migration/README.md) follows a WPF/CommunityToolkit
editor through an idiomatic Runic feature and a native React host.

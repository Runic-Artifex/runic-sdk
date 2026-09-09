# Migrating an application to the preview

Use exact published versions from the [package catalog](https://docs.runic-artifex.eu/packages/) and preserve domain logic before changing UI
orchestration. Start with the [customer migration reference](../../../examples/customer-migration/README.md)
and the completed [MAUI-derived document reference](../../../examples/document-migration/README.md).
Both preserve shared domain behavior while replacing presentation orchestration;
the document fixture does not establish a compiled MAUI or mobile host.

1. Keep validation, import/export formats and document rules in ordinary C# domain
   code. Map UI actions to Runic commands and expose serializable state.
2. Keep in-progress text edits as frontend drafts. Capture the document/customer
   revision when starting export or save, so edits made while a picker is open do
   not silently change the requested operation.
3. Resolve platform services from the presentation scope. Select only the native
   provider needed by the Desktop deployment. Read capability snapshots and offer
   useful unavailable outcomes in CS-WebUI.
4. Consume read/save leases once and dispose them, including on cancellation or
   failed parsing. Commit through the write transaction contract. Preserve conflict
   and uncertain-commit outcomes; do not automatically retry a write whose outcome
   is unknown.
5. Keep native file access, paths and handles behind the C# service boundary. Send
   application data and operation outcomes over the bridge.
6. Reconnect to the same presentation without replaying an import, export or write.
   Cancel or invalidate work when its owner is replaced. Await scoped cleanup at
   shutdown and protect dirty documents during close.
7. Test real bridge flows, not only direct command calls: edits while pending,
   cancellation followed by retry, captured export revisions, reconnect, process
   restart persistence, keyboard operation and focus restoration.

The customer reference must cover native import, export, copy and paste. The document
reference must cover open, edit, save, picker cancellation and dirty-close decisions.
Test failures and unavailable capabilities are part of the UI contract. Successful
clipboard writes must not be reported as canceled after they have taken effect.

Upgrading between previews may require source changes and regenerated bridge code.
Use the Effect dependency version declared by the selected published packages.
Regenerate contracts and run the host and package-consumer checks affected by the
upgrade; development project references alone do not exercise the published graph.

## Translation catalogs

New scaffolds and maintained templates set `sourceLayout: "locale-toml"` in
`runic.json`. Each locale lives in a sibling file such as `en.toml` or `de.toml`,
with MF2 string leaves grouped by standard TOML tables, nested tables, dotted
keys or inline tables. Identifier-safe path segments join with underscores into logical message
IDs: `[documents.actions] save` becomes `documents_actions_save`. Flat keys remain
supported; collisions such as `a_b.c`, `a.b_c` and flat `a_b_c` are rejected.
Prefer multiline literal strings for readable MF2 plurals. Plain tables are the
preferred default; optional arrays of tables require a stable identifier string
`_id` immediately in every row.
Keep it identical across locales: it contributes to the logical ID, is not
translated and makes row order irrelevant. Scalar and mixed arrays and a separate
TOML matching language are not supported. MF2 remains the message
language, including parameters and plurals. A project without `sourceLayout`
continues to read legacy `{locale}/{message_id}.mf2` files; do not mix layouts.

Commit or back up the original catalog, then use the matching preview tool:

```sh
dotnet tool run runic-translations -- migrate --project translations --dry-run
dotnet tool run runic-translations -- migrate --project translations
dotnet tool run runic-translations -- validate --project translations
```

The dry run lists planned creations, replacements and deletions without writing.
The applying command checks for collisions and commits the conversion as a
transaction, updates the discriminator and removes the migrated MF2 files.
Message IDs and decoded source content are preserved; existing MF2 line-ending
normalization and body trimming still apply. Regenerate outputs and verify the
application's formatted messages before accepting the migration. See the
[locale project guide](../translations/mf2-projects.md) for the supported profile.

The pinned Tomlyn 2.10.1 parser belongs to compiler and authoring tooling, not
generated application runtimes. Upgrade tool and build packages together.
Runic's source-built Translations Editor demonstrates the English/German TOML
workflow and MF2 count messages; final automated UI acceptance remains pending.
Standalone Editor distributions are outside this preview.

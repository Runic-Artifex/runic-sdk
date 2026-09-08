# Migrating an application to the preview

Use exact candidate versions and preserve domain logic before changing UI
orchestration. Start with the [customer migration reference](../../../examples/customer-migration/README.md)
and its before/after feature. The MAUI-derived document demonstration is the second
required reference; its final source and acceptance evidence must be linked from the
release notes once integrated.

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
Use the exact Effect release candidate documented in the release guide. Re-run both
host and package-consumer acceptance after upgrading; development project references
do not prove the published dependency graph works.

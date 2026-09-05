# RFC: Migrating application behavior out of MVVM

Status: direction accepted; first reference implementation delivered. SDK forms,
operation ergonomics, platform services, and automated migration remain proposals.

## Product promise

Keep business logic. Move application behavior into Runic. Provide excellent tooling
for rebuilding the presentation layer. Success means migrating a useful feature
without duplicating business rules or silently changing its outcomes.

CommunityToolkit compatibility is not the destination. Do not introduce an
`ICommand`/`INotifyPropertyChanged` remoting protocol, a viewmodel adapter package,
a second MVVM-shaped application model, or a universal XAML converter. Developers
should finish a migrated feature with native Runic concepts and remove its Toolkit
dependency. Existing domain libraries may be shared during the transition.

## Reference and evidence

The [customer migration](../../../../examples/current/customer-migration/README.md)
contains a compiled CommunityToolkit viewmodel and WPF host, shared domain rules and
persistence, a member-authored Runic application module, and a React frontend.
Live acceptance exposed a protocol bug: cancellation acknowledgements were decoded
as application receipts, and malformed replies could leave promises pending. The
bridge now validates its own cancellation acknowledgement and settles failed requests;
regression tests cover accepted, declined, malformed and mismatched replies.

Managed tests compare saved business state and verify rejected edits, cancellation,
conflicts, scoped state, and reconnect. Browser acceptance drives the actual C# host.

The first target is desktop WPF migration. A MAUI-derived feature is the next
validation target; portable viewmodel reuse does not establish MAUI platform or
mobile-host compatibility. The initial coexistence model is separate applications
sharing libraries. Hosting Runic controls inside WPF/MAUI remains a separate decision.

## Responsibility boundaries

- Domain code owns business rules, persistence, authorization, and transactions.
- Runic application features expose intentional commands, validated snapshots,
  typed errors and domain/operation events through the existing member-based bridge.
- Frontends own drafts, focus, presentation search, expanded panels, and local
  selection. Selection/navigation moves to C# when application behavior requires it.
- Platform services own OS interactions. Provide dependency injection and test
  implementations, documented capabilities, and access to platform-specific features.
- A transport session is not an application database or durable workflow. Application
  data may be shared across sessions; operation/UI state must have explicit lifetimes.

Commands are intents, not remotely writable property assignments. Submit a complete
form draft with a concurrency token when atomic acceptance is needed. Keep draft
values separate from confirmed state and preserve them after validation errors or
cancelled work. C# is authoritative; client affordances do not authorize a command.

## Migration workflow

1. Select a feature and capture its behavior in tests.
2. Identify domain logic, presentation state, orchestration and platform dependencies
   inside its viewmodel. Explain decisions such as what selection actually means.
3. Extract shared services, preserving business rules and tests.
4. Introduce Runic state/commands and move local editing to frontend forms.
5. Integrate the feature's OS interactions explicitly, recording unsupported cases.
6. Compare business outcomes, then remove the old feature's viewmodel/Toolkit usage.

A future read-only analyzer can classify dependencies and suggest refactorings with
source locations. Scaffolding should generate a reviewable starting point and name
unresolved decisions. It must not claim to infer business semantics from attributes.
Prove at least the WPF and MAUI-derived reference features before fixing a public
migration CLI or large code-fix API.

## DX requirements and observed gaps

| Area | Reference evidence | Next SDK work |
| --- | --- | --- |
| Contract authoring | Generated C#/TypeScript module contract; no handwritten wire types | Reduce repetitive state publication without implicit object remoting |
| Forms | Local draft, dirty checks, C# field issues, bounded import, stale-validation suppression | Framework-neutral form primitives with idiomatic framework bindings |
| Operations | Owned cancellable save, progress, explicit terminal snapshot | Reusable pending/error/progress helpers and documented commit/cancel semantics |
| Ordering | Projection generations ignore late receipts; edit sequences reject stale validation | Decide whether controller metadata or shared helpers should own these mechanics |
| Reconnect | Confirmed data and operation outcome recover; draft preserved in memory | Consistent reconnect affordances and optional durable draft recovery |
| Debugging | Named contracts and testable headless feature | Command/state inspector, traces and source navigation |
| Platform | WPF native dialogs versus WebView file input/HTML dialog | Close confirmation implemented; owned dialogs, dispatching and broader capability reporting remain |

Do not promote the sample's helpers to public packages until another feature tests
those abstractions. The reference is evidence for API design, not a claim that the
full proposed DX exists today.

## OS integration priorities driven by migration

1. Asynchronous close veto is implemented with native hooks and capability reporting;
   finish platform runner evidence and the Application host's macOS main-thread runner.
   See the [lifecycle contract](../../../runic-desktop/docs/window-close-lifecycle.md).
2. File dialogs and access lifetimes; clipboard; UI dispatching with explicit thread rules.
3. Navigation/back, activation, menus and keyboard shortcuts.
4. Notifications and background work lifecycle where supported.
5. Native accessibility testing, distribution, signing, installation and updates.

Publish a platform capability matrix and keep platform-specific APIs available.
Do not force a lowest-common-denominator contract or advertise unsupported parity.
These priorities complement the existing desktop delivery roadmap.

## Acceptance for the next milestone

Migrate a second production-style feature using the reference approach. Measure
first-success time, handwritten coordination code, source diagnostics, startup,
input latency and recovery behavior. Introduce reusable forms/operation APIs only
where both implementations demonstrate the same need. Verify native close and file
flows on the supported OS runners before describing them as migration guarantees.

## Sequenced follow-up

After the close-interception follow-up, restructure the monorepo before broadening
OS APIs. The [repository reorganisation plan](../../../../eng/repository-reorganisation.md)
replaces imported repository boundaries with package, tool, application and test
ownership. Preserve package identities and Git ancestry; verify standalone consumers
and generated artifacts after moves. File dialogs and reusable migration DX remain
subsequent feature work, informed by the second reference feature.

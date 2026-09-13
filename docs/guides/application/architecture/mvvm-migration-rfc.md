# Migrating application behavior out of MVVM

The maintained customer and document references demonstrate the migration
approach. This guide records their architectural boundaries; it is not a
compatibility layer or an automated-migration plan.

## Product promise

Keep business logic. Move application behavior into Runic. Success means
migrating a useful feature without duplicating business rules or silently
changing its outcomes.

CommunityToolkit compatibility is not the destination. Do not introduce an
`ICommand`/`INotifyPropertyChanged` remoting protocol, a viewmodel adapter
package, a second MVVM-shaped application model or a universal XAML converter.
Developers should finish a migrated feature with native Runic concepts and
remove its Toolkit dependency. Existing domain libraries may be shared during
the transition.

## Reference evidence

The [customer migration](../../../../examples/customer-migration/README.md)
contains a compiled CommunityToolkit viewmodel and WPF host, shared domain rules
and persistence, a member-authored Runic application module and a React frontend.
Its bridge regression coverage includes accepted, declined, malformed and
mismatched cancellation replies, so failed replies settle instead of leaving a
frontend request pending.

The [document reference](../../../../examples/document-migration/README.md)
extracts MAUI-derived document orchestration into shared services, Runic commands
and a React draft. Its MAUI page is a reference fixture, not a compiled MAUI host;
this does not establish MAUI platform or mobile-host compatibility. The initial
coexistence model is separate applications sharing libraries. Each reference can
select Desktop or CS-WebUI without changing its domain services or bridge contract;
host-specific bootstrap remains explicit.

## Responsibility boundaries

- Domain code owns business rules, persistence, authorization and transactions.
- Runic application features expose intentional commands, validated snapshots,
  typed errors and domain/operation events through the member-based bridge.
- Frontends own drafts, focus, presentation search, expanded panels and local
  selection. Selection or navigation moves to C# when application behavior
  requires it.
- Platform services own OS interactions and are supplied through DI. They report
  typed capability availability and keep native resources in C#.
- A transport session is not an application database or durable workflow.
  Application data may be shared across sessions; operation/UI state needs an
  explicit lifetime.

Commands are intents, not remotely writable property assignments. Submit a
complete form draft with a concurrency token when atomic acceptance is needed.
Keep draft values separate from confirmed state and preserve them after validation
errors or cancelled work. C# is authoritative; client affordances do not authorize
a command.

## Migration workflow

1. Select a feature and capture its behavior in tests.
2. Identify domain logic, presentation state, orchestration and platform
   dependencies in its viewmodel. Explain application decisions such as what
   selection means.
3. Extract shared services while preserving business rules and tests.
4. Introduce Runic state and commands; move local editing to frontend forms.
5. Integrate OS interactions explicitly and handle unavailable outcomes.
6. Compare business outcomes, then remove the old feature's viewmodel/Toolkit use.

The references deliberately keep their helpers local. They are evidence for API
design, not a claim that generic forms, operation helpers or migration tooling
exist. Do not infer business semantics from MVVM attributes or promote a sample
helper without another concrete consumer.

For native interactions, inject typed platform services and keep selected
resources, access leases and paths in C#. The [Platform service architecture]
(os-integration-rfc.md), [desktop services guide](../../desktop-services.md) and
[window lifecycle contract](../../desktop/window-close-lifecycle.md) describe the
current boundaries. Verify the migrated behavior through real bridge flows,
including cancellation and dirty-close protection.

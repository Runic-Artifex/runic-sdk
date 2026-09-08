# Move a feature from CommunityToolkit.Mvvm to Runic

Use the [runnable customer editor](../../../../examples/customer-migration/README.md)
as the complete before/after implementation. This is a refactoring guide, not an
adapter installation guide. Runic does not interpret XAML or execute remote bindings.

## 1. Write down the feature's observable behavior

Record successful saves, invalid inputs, uniqueness checks, cancellation, concurrent
edits, navigation with unsaved work, and process/connection interruption. Preserve
business tests. Viewmodel property-notification counts are implementation details
unless another application component actually depends on them.

## 2. Extract domain behavior

Move validators, repositories and service orchestration into a library without WPF,
MAUI or CommunityToolkit dependencies. Keep platform work behind explicit services.
The reference's `Domain/Customers.cs` is consumed by both applications and has no
Runic dependency either. Its version check and atomic file commit stay in C#.

## 3. Replace the viewmodel's application responsibilities

`After/CustomerFeature.cs` is a partial bridge module. Its snapshot contains confirmed
customers and operation state. `ValidateCustomer` returns typed field issues;
`SaveCustomer` takes the whole draft and starts an owned operation. `CustomerRejected`
expresses admission failures. Completion, cancellation and save failures appear in
an event and in the recoverable snapshot.

Retain your domain services and introduce request/response DTOs at the boundary.
Keep contract-safe limits separate from validation rules: a form may contain an
invalid email while the transport still needs to accept that draft for validation.
Never expose a property solely because it has a public setter.

## 4. Rebuild the presentation responsibilities

Replace observable editing properties with frontend form state. Replace WPF/MAUI
bindings with typed inputs, actions and rendering. Rebuild XAML layouts, styles,
converters and control behaviors using frontend components. Keep display formatting
near the view; keep business calculations with the domain.

The reference submits a full draft on Save. Field blur can request C# validation,
but Save independently validates and never depends on that asynchronous response.
An edit sequence suppresses stale validation responses. The original record version
is retained until a successful save or explicit discard, preventing lost updates.

Local navigation asks before discarding edits. Background events do not replace a
dirty draft. Input is temporarily disabled during this sample's save to avoid
ambiguous editing of a submitted version. Other products can allow continued editing
with an explicit reconciliation policy.

## 5. Migrate OS interactions deliberately

Replace direct `Dispatcher`, `Shell`, dialog or application-singleton references with
services that match the feature's actual needs. A web file input can obtain selected
file contents without granting arbitrary path access, but it does not reproduce all
native file-picker capabilities. The customer reference now uses `IFileDialogs`
and `ITextClipboard` for native import/export and copy/paste, while retaining the
browser JSON input as an explicit alternative. Register `AddRunicPlatform()` for
unavailable browser services or bind an explicitly selected Desktop provider.
Keep file leases and transactions in C#; send bounded contact candidates to the
frontend for review. Correlate candidates with the draft sequence and customer
identity, and capture the confirmed saved revision before starting export.

For embedded-window closing, configure
`DesktopWindowOptions.ConfirmCloseAsync` and answer from the state owner. The customer
reference's `--native` mode asks its React draft through the authenticated script
channel. Use `RequestCloseAsync` for application close buttons; `CloseAsync` and
disposal intentionally bypass confirmation. See the
[close lifecycle contract](../../desktop/window-close-lifecycle.md)
for custom-host, browser and platform limitations.

## 6. Verify and remove the old dependency

Run the business comparison and live browser tests. Test the actual native platforms
for keyboard, focus, accessibility and lifecycle behavior. Remove the migrated
feature's viewmodel and CommunityToolkit reference from its new application path.
The reference keeps `Before/` only as comparative evidence: `After/` and `Host/`
have no dependency on it.

A MAUI migration follows the same responsibility split, but its Shell navigation,
permissions, storage, activation and mobile lifecycle need their own platform work.
Do not infer mobile support from successful compilation of shared business code.

See the [migration RFC](../architecture/mvvm-migration-rfc.md) for current gaps and
criteria for future SDK helpers and migration tooling.

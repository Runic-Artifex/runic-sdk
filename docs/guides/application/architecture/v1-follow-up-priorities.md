# Priorities after the member-based bridge

## 1. Keep SDK changes verifiable

Use the shared toolchain, bootstrap command and package inventory. Verify cross-package
changes through both source tests and isolated package consumers. Keep fast contract
tests before browser and native jobs; run the actual workflow for integrated checks.

## 2. Make a complete desktop application easy to ship

Prioritize stable window lifecycle, menus and shortcuts, dialogs, clipboard,
notifications, tray integration, deep links and multiwindow state. Publish a
platform capability matrix and test the supported combinations on native Windows,
macOS and Linux runners. Add signing, notarization, installation, updates and
rollback to the documented application workflow.

Runic has a useful proposition: .NET application behavior with modern web UI
frameworks. Establish a strong desktop support promise before committing to a
mobile feature matrix. Avalonia explicitly publishes support tiers across desktop,
mobile and WebAssembly; a comparably clear promise matters more than a long list
of nominal targets. [Avalonia platform policy](https://docs.avaloniaui.net/docs/supported-platforms).

## 3. Ship an accessible application component kit

Provide a cohesive shell, navigation, forms with validation, dialogs, tree views,
virtualized lists/tables, loading/error states and theme tokens. Make keyboard
navigation, focus restoration, high contrast, scaling, screen-reader semantics,
IME input and localization part of their acceptance tests. Reuse the editor as a
real integration application, while keeping the component API independent of its
domain.

Both Avalonia and MAUI document platform accessibility integration. This is a
competitive baseline; rendering HTML alone does not prove the complete native
window experience works with assistive technology.
[Avalonia accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility),
[MAUI accessibility](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/accessibility?view=net-maui-10.0).

## 4. Measure reliability and developer experience

Turn cold startup, idle memory, first interactive frame, bridge round-trip latency,
large-list responsiveness and reconnect recovery into repeatable benchmarks on
named reference machines. Track distributions and regressions rather than claiming
framework-wide superiority from one synthetic test. Add fault injection for host
exit, invalid messages, cancelled commands, lost connections and failed updates.

Make `dotnet runic dev` and `doctor` the documented happy path. Add source-located
fix suggestions, safe contract inspection, command/event tracing, and a concise
migration guide. Maintain a small production-style reference application for each
supported frontend. Keep browser automation and native accessibility tests alongside
headless protocol tests; Avalonia's layered testing model is a useful reference.
[Avalonia testing](https://docs.avaloniaui.net/docs/testing/).

Suggested order: monorepo and unified CI, desktop delivery guarantees, accessible
components, then deeper platform expansion based on demonstrated demand.

## Parallel focus: native Runic DX for MVVM migrations

The accepted migration direction is to replace MVVM presentation architecture with
idiomatic Runic, preserving domain logic. Do not build a CommunityToolkit adapter.
The [migration RFC](mvvm-migration-rfc.md) and
[customer reference](../../../../examples/customer-migration/README.md)
provide the first concrete evidence. Use its forms, operation, ordering and native
lifecycle gaps to shape SDK work alongside OS integration. Follow with a MAUI-derived
feature before investing in automated migration scaffolding.

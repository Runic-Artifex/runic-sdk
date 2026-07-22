# ADR 0001: WebUIToolkit identity and namespace ownership

- Status: Accepted
- Date: 2026-07-22

## Decision

Every first-party .NET namespace, assembly name, project name, package ID, generator-owned infrastructure type, telemetry source, diagnostic catalog, sample, and test project uses the exact parent identifier `WebUIToolkit`. Consumer-selected generated namespaces such as `Example.Product.Localization` and template `@namespace` values remain consumer-owned.

The mechanical mapping for unshipped draft identities is `CsWebUi.*` to `WebUIToolkit.*`. No compatibility facade for draft names will be created.

The external lowercase `cs-webui` project remains an upstream dependency and keeps its existing identity. Third-party names such as CommunityToolkit.Mvvm, ReactiveUI, and HTMX are unchanged.

Frontend packages use the reserved target scope `@webuitoolkit/*`; publication remains blocked until scope ownership is verified. Schema URLs must not be published until their domain is owned and deployed.

Flow keeps deliberately flat public namespaces: `WebUIToolkit.MVVM.Navigation`, `WebUIToolkit.MVVM.Dialogs`, `WebUIToolkit.MVVM.Operations`, `WebUIToolkit.MVVM.Workflows`, and `WebUIToolkit.MVVM.Flow`.

Dependency notices use `WebUIToolkit.DependencyNotices.*` even though their planning documents used neutral project names.

Branded public identifiers are renamed before implementation:

| Draft identifier | Implementation identifier |
|---|---|
| `CsWebUiApplication` / `CsWebUiApplicationBuilder` | `WebUIToolkitApplication` / `WebUIToolkitApplicationBuilder` |
| `AddCsWebUiMvvm` / `AddCsWebUiFlow` | `AddWebUIToolkitMvvm` / `AddWebUIToolkitFlow` |
| `ValidateCsWebUiFlowAsync` | `ValidateWebUIToolkitFlowAsync` |
| `AddCsCommandLine*` | `AddWebUIToolkitCommandLine*` |
| `CsWebUiGenerateBindings` | `WebUIToolkitGenerateBindings` |
| `CsWebUiFrontend*` | `WebUIToolkitFrontend*` |
| `CsWebUiTextResourceKind` | `WebUIToolkitTextResourceKind` |
| `CSWEBUI_CLI_OUTPUT` | `WEBUITOOLKIT_CLI_OUTPUT` |
| `cswebui.cli/1` | `webuitoolkit.cli/1` |

## Consequences

- Owned source paths may not introduce a `CsWebUi` namespace or package identity.
- Serialized protocol IDs and schema identifiers are registered before fixtures become stable.
- The repository does not publish a bare `WebUIToolkit` roll-up package without a separate ADR.
- The eventual `WebUIToolkit.Collections.Observable` package retains the public namespace `WebUIToolkit.Collections`.

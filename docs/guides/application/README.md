# WebUIToolkit documentation

WebUIToolkit is a Native-AOT-first toolkit for building desktop applications on
top of CsWebUi. It supports two frontend tracks:

- compiled mixed C# `.cwuix` or declarative `.cwhtml` views with HTMX over one
  private native binding; and
- React, Vue, Svelte, or Angular views over the generated binary MVVM contract.

Both tracks share C# ViewModels, commands, validation, collections, application
flow, hosting, and desktop capability services.

## Start here

- [Getting started](./getting-started/README.md) — enter the development
  environment, run a sample, and use the coordinated development command.
- [Guides](./guides/README.md) — C# markup, cwhtml, frontend-framework, and WPF migration
  guidance.
- [Reference](./reference/README.md) — command-line tools, SDKs, generated
  contracts, and package-level documentation.
- [Architecture](./architecture/README.md) — product boundaries, package
  direction, and architectural decisions.
- [Roadmap](./roadmap/README.md) — the single current ordering of product work.
- [Contributing](./contributing/README.md) — development modes, quality gates,
  and repository orchestration.

## Historical material

The [release evidence](./release/README.md) and
[archived plans](./roadmap/archive/README.md) explain earlier delivery waves.
They are retained for traceability, but they do not describe the current
product priorities. The current roadmap is authoritative for unfinished work.

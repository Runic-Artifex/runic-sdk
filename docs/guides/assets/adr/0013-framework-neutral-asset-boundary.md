# ADR 0013: Framework-neutral asset and VFS boundary

- Status: Accepted
- Date: 2026-07-28

## Context

Static application assets currently cross several repository layers:
`WebUIToolkit.Hosting.Abstractions` defines manifest/provider contracts,
`WebUIToolkit.Hosting.Build` creates production manifests, and
`WebUIToolkit.Hosting.WebUi` opens local files and maps them to an endpoint.
CsWebUi also has a customer-derived virtual filesystem implementation.

The product needs one reusable boundary for deterministic production assets
and local development directories. That boundary must work for compiled HTML
and every browser framework without making HTMX, MVVM, Bootstrap, CsWebUi, or
ASP.NET Core part of the asset model. Published desktop applications must
remain offline and compatible with trimming, single-file publication, and
Native AOT.

## Decision

Introduce the dependency-free `WebUIToolkit.Assets` package and the
`WebUIToolkit.Assets` namespace.

- `AssetManifest` is an immutable, ordinally sorted catalog with exactly one
  entry point.
- `AssetDescriptor` contains a safe application-relative path, media type,
  length, SHA-256 digest, strong entity tag, and explicit cache mode.
- `AssetPath` rejects rooted paths, traversal, empty segments, encoded octets,
  URI query/fragment syntax, control characters, and other ambiguous forms.
- `EmbeddedAssetSource` maps explicit assembly resource names to asset paths.
  It does not extract resources or enumerate an application filesystem. This
  is the production/offline/single-file/Native-AOT source.
- `DevelopmentDirectoryAssetSource` is explicitly for local development. It
  rejects reparse points, constrains reads to a fixed root, assigns `no-store`,
  and atomically publishes a fresh immutable manifest on `Refresh`.
- `IAssetSource` exposes manifest, validation, and exact-path open operations.
  Endpoint routing and response delivery remain adapter responsibilities.

Media type resolution is a deterministic package-owned table and never uses
an operating-system registry. The package has no third-party or other
WebUIToolkit package dependency.

Existing Hosting asset contracts remain temporarily available. Hosting and
CsWebUi adapters can migrate to `IAssetSource` separately, using compatibility
adapters where necessary. They must not create another general-purpose asset
model.

## Consequences

- Asset packages can be embedded in application assemblies and read directly
  after trimming or single-file publication.
- Vite output, checked-in static files, cwhtml assets, and framework bundles
  can all become the same manifest/source input without framework coupling.
- Development files are treated as mutable and uncacheable; production
  embedded content has digest-based validators and may opt into immutable
  caching for content-addressed paths.
- Runtime adapters decide how cache metadata maps onto CsWebUi, HTTP, or a
  future transport.
- The package tests cover hostile paths, deterministic manifests, embedded
  content, refresh/drift behavior, links, cancellation, and dependency
  neutrality. The isolated package consumer additionally Native-AOT publishes
  and runs from a packed package.

## External coordination

Moving `cs-webui` into a WebUIToolkit GitHub organization and changing its
repository or NuGet ownership cannot be completed inside this repository.
That work requires maintainer coordination and is deliberately not performed
by this decision. Once coordinated, cs-webui should adapt this package rather
than copy its contracts.

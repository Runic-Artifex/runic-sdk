# ADR 0013: Framework-neutral asset and VFS boundary

- Status: Accepted
- Date: 2026-07-28

## Context

Static application assets currently cross several repository layers:
`WebUIToolkit.Hosting.Abstractions` defines manifest/provider contracts,
`WebUIToolkit.Hosting.Build` creates production manifests, and
`WebUIToolkit.Hosting.WebUi` opens local files and maps them to an endpoint.
CS-WebUI also has a customer-derived virtual filesystem implementation.

The product needs one reusable boundary for deterministic production assets
and local development directories. That boundary must work for compiled HTML
and every browser framework without making HTMX, MVVM, Bootstrap, CS-WebUI, or
ASP.NET Core part of the asset model. Published desktop applications must
remain offline and compatible with trimming, single-file publication, and
Native AOT.

## Decision

Introduce the dependency-free `RunicAssets` package and namespace.

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

Host-specific adapters depend on `RunicAssets`; the core package never depends
on CS-WebUI, ASP.NET Core, Runic Toolkit, or another UI framework.

The CS-WebUI adapter uses CS-WebUI's public, policy-free custom file-handler API.
It constructs complete HTTP responses directly from the current Runic Assets
manifest and source; it does not project assets through CS-WebUI's virtual file
system or ask CS-WebUI to infer media and cache policy. This preserves live
development refresh and keeps native callback and buffer-lifetime mechanics
inside CS-WebUI.

Runic Assets also owns the incremental `dist` packer and embedded archive build
target. The target emits the canonical metadata-bearing Runic Assets archive;
CS-WebUI does not ship an overlapping Vite packer, virtual filesystem, or asset
policy layer.

## Consequences

- Asset packages can be embedded in application assemblies and read directly
  after trimming or single-file publication.
- Vite output, checked-in static files, generated assets, and framework bundles
  can all become the same manifest/source input without framework coupling.
- Development files are treated as mutable and uncacheable; production
  embedded content has digest-based validators and may opt into immutable
  caching for content-addressed paths.
- Runtime adapters decide how cache metadata maps onto CS-WebUI, HTTP, or a
  future transport.
- The package tests cover hostile paths, deterministic manifests, embedded
  content, refresh/drift behavior, links, cancellation, and dependency
  neutrality. The isolated package consumer additionally Native-AOT publishes
  and runs from a packed package.

## External coordination

CS-WebUI now lives in the Runic Artifex organization. Its integration is owned
here so CS-WebUI remains independently useful while Runic Assets controls how
its transport-neutral contract maps to WebUI delivery.

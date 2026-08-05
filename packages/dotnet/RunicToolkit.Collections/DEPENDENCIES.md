# Dependency manifest

This manifest covers the `RunicToolkit.Collections` shipping artifact and its owned
verification projects.

## Shipping artifact

| Category | Inventory |
|---|---|
| Target | `net10.0` |
| Runtime package dependencies | None |
| Framework use | .NET BCL collection, component-model, and notification APIs |
| Project references | None |
| Native/RID assets | None |
| Reflection or dynamic-code discovery | None |
| Public namespace | `RunicToolkit.Collections` |

The shipping project contains no runtime `PackageReference`. SDK/build tooling is
not emitted as a dependency in the package nuspec. Package constraints are owned by
the repository; consuming applications own their resolved dependency graph.

The package-consumer harness rejects a nuspec dependency, a `runtimes/` entry, or a
missing `lib/net10.0` assembly/XML-doc asset. The package readme is also required.

## Verification projects

The executable unit/property tests and benchmark harness use no external test or
benchmark framework. Their production dependency is the owned Collections project.
The package-consumer harness uses only BCL APIs and invokes the installed .NET SDK to
pack, restore, build, run, and optionally publish an isolated temporary consumer.

The AOT smoke may require runtime packs supplied by the .NET SDK for the selected
RID. Restore the intended RID before publishing.

This inventory is dependency evidence, not a license approval. ADR 0004 blocks
public package publication until dependency inventory, notices, SBOM linkage,
ownership, and license review are approved.

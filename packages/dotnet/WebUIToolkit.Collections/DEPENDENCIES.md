# Dependency manifest

This manifest covers the `WebUIToolkit.Collections` shipping artifact and its owned
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
| Public namespace | `WebUIToolkit.Collections` |

The shipping project contains no `PackageReference`. Its portable generated lock
currently records `Microsoft.NET.ILLink.Tasks` as an SDK/build tool resolved for the
repository's shipping configuration; it is not emitted as a dependency in the
package nuspec. The committed `packages.lock.json` is the authoritative version and
hash record. It must remain portable and contain no RID-specific section.

The package-consumer harness rejects a nuspec dependency, a `runtimes/` entry, or a
missing `lib/net10.0` assembly/XML-doc asset. The package readme is also required.

## Verification projects

The executable unit/property tests and benchmark harness use no external test or
benchmark framework. Their production dependency is the owned Collections project.
The package-consumer harness uses only BCL APIs and invokes the installed .NET SDK to
pack, restore, build, run, and optionally publish an isolated temporary consumer.

The AOT smoke may require runtime packs supplied by the .NET SDK for the selected
RID. RID restore must use the ignored
`obj/aot.packages.lock.json` with `RestoreLockedMode=false`; it must never add RID
sections to a committed lock. Ordinary managed restore must continue to pass
`--locked-mode` with the portable committed locks.

This inventory is dependency evidence, not a license approval. ADR 0004 blocks
public package publication until dependency inventory, notices, SBOM linkage,
ownership, and license review are approved.

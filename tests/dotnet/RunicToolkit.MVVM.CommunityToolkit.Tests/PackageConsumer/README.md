# CommunityToolkit package-consumer fixture

The normal repository build has a project reference for development. The script
switches it to a package reference, packs Core and the CommunityToolkit
integration into an owner-local feed, and seeds only the cached exact
CommunityToolkit.Mvvm 8.4.2 package. The consumer has no direct toolkit
dependency. The script validates the packed dependency metadata and restores into
an empty package cache with all ambient sources cleared. It then confirms that the runtime closure does not load
`RunicToolkit.MVVM.Build` tooling.

```console
pwsh tests/RunicToolkit.MVVM.CommunityToolkit.Tests/PackageConsumer/Test-PackageConsumer.ps1
```

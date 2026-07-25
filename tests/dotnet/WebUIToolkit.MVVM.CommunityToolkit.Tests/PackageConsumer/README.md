# CommunityToolkit package-consumer fixture

The normal repository build has a project reference for development. The script
switches it to a package reference, packs Core and the CommunityToolkit
integration into an owner-local feed, and seeds only the cached exact
CommunityToolkit.Mvvm 8.4.2 package. The consumer has no direct toolkit
dependency: its committed package-mode lock proves the transitive package graph.
The script normalizes newly packed local artifacts to deterministic bytes,
checks their SHA-512 content hashes against the committed lock, and directly
replays that lock in an empty package cache with all ambient sources cleared.
It then confirms that the runtime closure does not load
`WebUIToolkit.MVVM.Build` tooling.

```console
pwsh tests/WebUIToolkit.MVVM.CommunityToolkit.Tests/PackageConsumer/Test-PackageConsumer.ps1
```

After an intentional package-graph change, regenerate the fixture lock with
`-UpdateLock`, review it, and rerun without that switch to prove locked replay.

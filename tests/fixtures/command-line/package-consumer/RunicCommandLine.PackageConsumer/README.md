# Command Line package consumer

`Invoke-PackageConsumer.ps1` packs the Command Line kernel, its Hosting bridge,
and `Hosting.Abstractions` into a fresh local feed, restores the template
consumer into a fresh package cache, and proves both managed and `win-x64`
Native AOT execution. The consumer exercises command execution plus help,
version, invalid, and explicit empty-input UI-fallback handoff decisions.
Per-run artifacts
use a short uniquely named directory below the OS temporary directory so the
isolated Native AOT package cache remains below Windows path-length limits.

The fresh package cache and isolated feed prove that the consumer resolves only
the packed artifacts and their declared dependencies before managed and NativeAOT
execution.

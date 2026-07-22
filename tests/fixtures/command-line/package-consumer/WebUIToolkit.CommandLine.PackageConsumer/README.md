# Command Line package consumer

`Invoke-PackageConsumer.ps1` packs the three owned Command Line projects into a
fresh local feed, restores the template consumer into a fresh package cache,
and proves both managed and `win-x64` Native AOT execution. Per-run artifacts
use a short uniquely named directory below the OS temporary directory so the
isolated Native AOT package cache remains below Windows path-length limits.

The ordinary consumer lock is generated and verified as portable. Native AOT
restore uses the ignored `obj/aot.packages.lock.json` path so RID-specific
sections cannot enter a committed project lock.

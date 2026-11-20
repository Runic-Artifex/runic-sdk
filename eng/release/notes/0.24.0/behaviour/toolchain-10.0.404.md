- **Toolchain floor: .NET SDK 10.0.404 (runtime 10.0.15) and Node 24.22.3.** The packages are built with the
  November 10, 2026 .NET security release, and the compatibility set that `dotnet runic doctor` checks follows the
  repository's toolchain pins: `dotnet-sdk` now fails below SDK 10.0.404 (10.0.403 before) and certifies 10.0.404 or
  11.0.100, and `javascript-runtime` wants Node 24.22.3 or a later Node 24 (24.22.1 before), or Node 26 as before.
  `RUNICBRIDGE033` warns on a build with an SDK older than 10.0.404. Install .NET SDK 10.0.404 or a newer 10.0
  release and Node 24.22.3 or later, then rebuild self-contained and NativeAOT packages, which embed the runtime.
  Bun 1.4.4 and the npm and pnpm baselines are unchanged.

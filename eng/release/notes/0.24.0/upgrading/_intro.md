## Upgrading from 0.23

Update every Runic NuGet and npm package to 0.24.0 together, rebuild, and commit
the regenerated clients: every View's contract string changes once, since each
View now has a fingerprint of its own. Views protocol 4 is unchanged, so a 0.23
page and a 0.24 host still connect, but an operation that a 0.23 page started
answers `unknown` after the upgrade. A project that generates Views clients
needs Node.js or Bun when it builds, also without a frontend, and the toolchain
floor is .NET SDK 10.0.404 and Node 24.22.3.

Most applications change nothing else. Code that reads or spreads a generated
field descriptor's members reads them under `fieldMembers`, an application that
names the native Desktop server's files or links it into its own Rust library
moves to `rax_server`, and an application that uses Runic Translations moves to
0.16.0. `dotnet runic dev` now applies the first launch profile; for a profile
that sets more than `DOTNET_ENVIRONMENT`, `--no-launch-profile` keeps 0.23's
behaviour.

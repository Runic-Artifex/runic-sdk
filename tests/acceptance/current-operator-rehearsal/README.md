# Local operator rehearsal

This local-only verifier composes the fixed W80 readiness audit with the
existing direct `dotnet-runic` staging, offline manual-replacement, opt-in
support-envelope, interrupted-recovery, and documentation receipts. It copies
every supplied receipt into a temporary directory before linking it. Its only
positive migration result is `user-performed-verified-manual-replacement`.
It neither changes a package, archive, workspace, bridge, nor a network state.

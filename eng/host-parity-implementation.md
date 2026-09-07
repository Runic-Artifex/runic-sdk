# Host parity implementation

Authorized scope: implement the 2026-09-07 host-choice and footprint assessment.

- [x] Shared admission policy and CS-WebUI application host package project.
- [x] Native frontend channel, reconnect/cancellation/validation acceptance.
- [x] Host choice in CLI, counter templates and the customer reference.
- [x] Standalone package consumers and host/template verification matrix.
- [x] Minimal Desktop hosting profile and behavior parity.
- [x] Size reporting command, package-consumer measurements and tuning guidance.
- [x] Platform CI configuration, final documentation and Linux verification evidence.

Commit each verified wave. Do not change historical release evidence or publish
packages as part of this implementation. Report measured sizes only with their
RID, settings and behavioral verification status.

The first wave also corrects native persistent-server shutdown and makes browser
acceptance fail on a crash during cleanup. Verification runs through the actual
Nix development shell; see `docs/guides/desktop/nixos-development.md`.
The second wave adds host-aware templates and CLI development, an opt-in shared
development document, and live HMR acceptance on both hosts. The third wave adds a minimal Desktop profile, `runic size`, package-only native
measurements and a four-RID CI matrix. Final receipts are in `eng/host-footprint/linux-x64-2026-09-07/`, measured from
clean commit c274ca5c. Cross-platform CI is configured; only Linux x64 is locally
certified. No package has been published.

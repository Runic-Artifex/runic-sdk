# Host parity implementation

Authorized scope: implement the 2026-09-07 host-choice and footprint assessment.

- [x] Shared admission policy and CS-WebUI application host package project.
- [x] Native frontend channel, reconnect/cancellation/validation acceptance.
- [ ] Host choice in templates, CLI and customer/counter examples.
- [ ] Standalone package consumers and host/template verification matrix.
- [ ] Minimal Desktop hosting profile and behavior parity.
- [ ] Size reporting command, package-consumer measurements and tuning guidance.
- [ ] Platform CI, final documentation and committed verification evidence.

Commit each verified wave. Do not change historical release evidence or publish
packages as part of this implementation. Report measured sizes only with their
RID, settings and behavioral verification status.

The first wave also corrects native persistent-server shutdown and makes browser
acceptance fail on a crash during cleanup. Verification runs through the actual
Nix development shell; see `docs/guides/desktop/nixos-development.md`.
Standalone package consumption, templates/CLI and the remaining footprint work
are still pending; the new package has not been published.

# Unsigned direct `dotnet-runic` staging consumer

This W60 proof consumes only a directly packed local `dotnet-runic` staging
directory, its explicit local prerequisite feed, the Toolkit source needed to
verify provenance, and the W60-001 closed Editor candidate set. It runs the
installed tool twice with separate local NuGet and CLI caches. It is not an
approval of the canonical seven-package release gate.

```console
RUNIC_W60_TOOL_STAGING=/path/to/staged-tool \
RUNIC_W60_TOOL_PREREQUISITE_FEED=/path/to/prerequisites \
RUNIC_W60_TOOLKIT_ROOT=/path/to/runic-toolkit \
RUNIC_W60_AUTHORITY_MANIFEST=/path/to/.github/runic.release.json \
RUNIC_W60_EDITOR_CANDIDATE_SET=/path/to/closed-editor-candidates \
node eng/current-unsigned-tool-staging/verify.mjs run-twice > unsigned-tool-staging-receipt.json
```

The staging record and verifier refuse source project references, a prebuilt
tool in the prerequisite feed, remote package sources, identity/provenance
drift, support-envelope content, assigned/published candidate authority, and
any substitution of `seven-package-release-gate-required` with approval.

# Local non-public readiness audit

This local-only audit retains exactly the four W70-001 frozen profiles. It
reuses the W70 clean-room, support, and native-shell receipt verifiers and
keeps the post-freeze seven-package Toolkit gate separate: a passing gate at a
newer source revision is recorded as package-gate evidence, never as a new
retained C# host profile.

The audit copies only explicit receipt inputs into a fresh temporary directory.
It accepts no assigned candidate authority, publication, signing, update, or
external-action facts. The native result remains the bounded Linux/X64 managed
refusal for private-file streaming/WebView; it is not a platform-success claim.

Create the post-freeze package-gate record only after the canonical gate has
completed successfully:

```console
node eng/current-readiness-audit/record-canonical-package-gate.mjs \
  --toolkit /path/to/runic-toolkit --packages /path/to/seven-packages \
  --version 0.2.0-preview.w80001 > /path/to/package-gate.json
```

Then link the retained train twice:

```console
node eng/current-readiness-audit/verify.mjs run-twice \
  --profile /path/to/controlled-nonpublic-profile.json \
  --freeze /path/to/w70-freeze.json \
  --w04 /path/to/w04-clean-install.json \
  --clean-room /path/to/w70-clean-room.json \
  --support /path/to/w70-support.json \
  --native /path/to/w70-native.json \
  --package-gate /path/to/post-freeze-package-gate.json \
  > /path/to/w80-readiness-audit.json
node eng/current-readiness-audit/verify.mjs verify-twice ... \
  --receipt /path/to/w80-readiness-audit.json
```

This is neither a release authorization nor a version assignment, signature,
notarization, upload, publication, or update mechanism.

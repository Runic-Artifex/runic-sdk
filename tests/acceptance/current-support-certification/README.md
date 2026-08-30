# Frozen support certification

This local-only linker accepts the W70-001 profile/freeze receipt and the
three frozen W50 consumer receipts. Each journey copies only those explicit
inputs into a fresh temporary directory and validates one shared Editor
diagnostics authority. The certificate excludes source, translation, review,
session, cookie, and token content; it records structural work bounds only,
never a timing SLA.

```console
node eng/current-support-certification/verify.mjs run-twice \
  --profile /path/to/controlled-nonpublic-profile.json \
  --freeze /path/to/w70-freeze.json \
  --support /path/to/w50-support.json \
  --recovery /path/to/w50-recovery.json \
  --quality /path/to/w50-quality.json
```

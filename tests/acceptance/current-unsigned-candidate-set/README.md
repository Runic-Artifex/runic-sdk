# Unsigned Editor candidate-set consumer

This is the independent W60 local-only consumer for the closed Editor
linux-x64, osx-arm64, and win-x64 staging set. It invokes the supplied release
authority linker from a temporary empty working directory; it neither packs nor
references product projects, uploads data, assigns a version, nor publishes.

Run it only against an unmodified, clean release-authority checkout and an
explicit candidate-set root whose immediate children are the three runtime IDs:

```console
RUNIC_W60_AUTHORITY_MANIFEST=/path/to/.github/runic.release.json \
RUNIC_W60_EDITOR_CANDIDATE_SET=/path/to/closed-editor-candidates \
node eng/current-unsigned-candidate-set/verify.mjs run-twice > unsigned-candidate-set-receipt.json

RUNIC_W60_AUTHORITY_MANIFEST=/path/to/.github/runic.release.json \
RUNIC_W60_EDITOR_CANDIDATE_SET=/path/to/closed-editor-candidates \
node eng/current-unsigned-candidate-set/verify.mjs verify-twice unsigned-candidate-set-receipt.json
```

Optional W50 receipts are citations only: set
`RUNIC_W60_PRODUCT_EVIDENCE=support-envelope:/path/to/support-receipt.json,native-capability:/path/to/native-receipt.json`.
The linker records their role/schema/digest and rejects payload-bearing evidence.

The receipt binds the authority revision/tree/digest, the unassigned
`translations-editor-archive` distribution, all three archives, sibling and
staging checksums, package manifests, SBOMs, provenance, and placeholder-only
receipt templates. It remains explicitly `publication: "forbidden"`.

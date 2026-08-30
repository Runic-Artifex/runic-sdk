# Editor structural-quality evidence

This local-only evidence profile joins the existing Application Bridge structural
gate with the Editor's deterministic large-catalog and keyboard/accessibility
checks. It is not a release, performance-SLA, browser-visual-E2E, WebUI ABI, or
native-certification claim.

Run it from this repository after committing the supplied Toolkit and Editor
candidate trees:

```sh
node eng/current-editor-quality/verify.mjs run-twice ../runic-toolkit ../runic-translations-editor > editor-quality-receipt.json
node eng/current-editor-quality/verify.mjs verify-twice ../runic-toolkit ../runic-translations-editor editor-quality-receipt.json
```

The receipt binds the two local source revisions and trees. It retains only
structural facts: returned-frame counts, schema-validated delivery, the existing
bounded-command package tests, the 50,000-message/100-locale retained-heap work
bound, and owned keyboard, recovery, label, landmark, and forced-colors checks.
Machine timing remains console observation only and is deliberately excluded
from the receipt.

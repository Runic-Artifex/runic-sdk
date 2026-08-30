# W40 localized-profile compatibility

This linker closes evidence for exactly two profiles: the D008 hosted SvelteKit product and the C# Runic Translations Editor. It links their deterministic receipts to the portable `runic-mf2-subset/1` consumer receipt; it does not declare a general compatibility promise.

- Portable ownership: the generated ESM ABI, typed translation-reference wire contract, MF2 subset, and closed XLIFF 2.1/review-sidecar interchange boundary are shared. Structured interchange content is loss-recorded or rejected, never silently flattened.
- Hosted ownership (D004/D008): C# remains authoritative for identity, session, admission, and the sanitized typed product projection. Svelte renders that projection only, resolving locale request-locally from the URL before the locale cookie. No bearer, CORS, proxy, or bridge policy changes are implied.
- Desktop ownership: the Editor consumes its own catalog to build embedded ESM and C# resources, then exercises the existing closed interchange smoke. Its catalog fingerprint is deliberately distinct from the hosted product fingerprint.
- W20/W70 boundary: the ApplicationBridge is local-only, not a hosted route. W70 native-platform certification remains out of scope.

Run with independently produced receipt files:

```sh
node eng/current-localization-compatibility/verify.mjs run-twice <mf2-receipt> <hosted-receipt> <desktop-receipt>
```

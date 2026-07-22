# Wave B security model and offline reproducibility

This document extends `threat-model-v1.md`; all Wave A controls remain mandatory.

## Untrusted inputs

NuGet/npm locks and metadata, restored directory contents, symlinks/reparse points, SBOM JSON, remote origins, redirect targets, response metadata/body bytes, cache contents, output names, terminal strings, and v2 runtime documents are attacker-controlled. A package being restored does not make its contents trusted.

## Security invariants

- Inventory adapters parse data files and never execute package code, scripts, Node/npm, restore, shell commands, or build targets.
- All file access begins at an explicit root and applies containment after normalization. Existing links/reparse points cannot escape. Output staging and cache roots follow the same rule.
- JSON depth/property/component/byte caps and evidence byte caps are enforced before unbounded allocation or model construction.
- XML package metadata prohibits DTD/external entity resolution and is strictly bounded/decoded.
- HTML output uses context-aware escaping and has no active/remote content. Plain text and diagnostics neutralize hostile controls in structural fields.
- Acquisition requires explicit online consent, expected SHA-256, exact allowed host, bounded redirects/bytes/time, and revalidation after every redirect.
- No ambient proxy credential, registry token, netrc, npmrc, NuGet credential provider, or authorization header is discovered by offline operations. Secrets are never persisted in origin indexes.
- Content-addressed and disposable cache writes are atomic; names are digest-derived, not attacker filenames. Corrupt cache content is rejected.
- Outputs are staged and atomically promoted. Inputs cannot be output destinations; aliases and duplicates are rejected.
- Cancellation/failure leaves no committed partial evidence or mixed output set.

## Explicit residual boundaries

SHA-256 proves byte identity, not legal correctness, authorship, or source authenticity. SPDX parsing proves syntax, not that an identifier is approved or that supplied text corresponds to it. SBOM fallback matching is identity reconciliation, not proof the SBOM represents the built artifact. Policy decisions do not replace legal review.

Wave B admits raw bounded HTTP response bodies only. Archive extraction, decompression, signatures/transparency logs, authenticated origins, registry search, malware scanning, and source-code license classification are deferred and MUST NOT be approximated implicitly.

## Offline reproducibility recipe

The handoff supplies concrete project paths and `<rid>`. From a clean checkout with the evidence fixtures present:

1. Disable/deny outbound network at the test boundary and clear only disposable Dependency Notices caches. Do not delete declared source evidence.
2. Restore each owned project with its committed portable lock:

   ```powershell
   dotnet restore <project.csproj> --locked-mode
   ```

3. Build Release without restore:

   ```powershell
   dotnet build <tests.csproj> -c Release --no-restore
   ```

4. Execute the owned test binary twice from two different absolute checkout roots/cultures with empty disposable caches. Capture canonical output/manifest SHA-256 and require equality.
5. Run the CLI fixture pipeline with network denied: scan, policy, generate, verify, and SBOM reconciliation. Any attempted transport is a test failure even if the command later succeeds.
6. Pack to a local temporary feed, restore a clean consumer only from that feed, run generation/runtime behavior, and compare golden bytes.
7. Publish the tool and Runtime consumer for `<rid>` with an ignored RID lock:

   ```powershell
   dotnet publish <aot-project.csproj> -c Release -r <rid> --self-contained true `
     -p:PublishAot=true `
     -p:NuGetLockFilePath=obj/aot.packages.lock.json `
     -p:RestoreLockedMode=false
   ```

8. Execute the native binary, require exit zero and no owned trim/AOT warnings. Remove only generated intermediates through normal clean behavior.
9. Rerun ordinary portable `dotnet restore <project.csproj> --locked-mode` for every committed lock and confirm `git status --short` is clean.

Evidence records include commands, SDK/runtime/RID, test count, native binary path/result, output hashes, network-denial method, local package manifest, and final commit SHA. Machine-specific absolute paths are reported in handoff logs only and never embedded in generated artifacts.

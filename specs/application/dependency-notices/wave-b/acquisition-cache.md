# Evidence acquisition and cache contract

Acquisition is a preparation operation, not part of build, scan, policy, generation, verification, SBOM reconciliation, rendering, runtime loading, or package consumption.

## Admission policy

Network access is admitted only when both conditions are true:

1. the operation is exactly `Acquire`; and
2. the caller explicitly sets `AllowNetwork`/`--allow-network`.

Every other combination fails with `WUTNOTICE7001` before transport use. There is no environment-variable, config-file, redirect, or cached-credential shortcut that implies consent.

An acquisition policy requires a non-empty exact host allowlist. Hosts are normalized to lowercase DNS/IP host strings; wildcards, ports, user information, and path fragments are invalid allowlist entries. HTTPS is required by default. HTTP is allowed only by a separate explicit policy option. URL fragments are removed and credentials are rejected.

Each redirect is resolved against the current URL and fully revalidated for scheme, credentials, and exact host. Default limits are five redirects, 16 MiB response bytes, and 30 seconds total timeout. Configured redirects may not exceed 20 and timeout may not exceed 10 minutes. Declared `Content-Length` and streamed bytes are both bounded.

| Failure | Diagnostic |
|---|---|
| Scheme, credentials, or exact host blocked | `WUTNOTICE7002` |
| Redirect missing/invalid/blocked or redirect cap reached | `WUTNOTICE7003` |
| Declared or streamed content exceeds the cap | `WUTNOTICE7004` |
| Downloaded SHA-256 differs from the required expected digest | `WUTNOTICE7005` |

The request MUST contain an expected canonical lowercase SHA-256. A URL alone is never sufficient authorization and never establishes authenticity.

## Content-addressed evidence store

- The stable identity is `sha256/<64-lowercase-hex>`, under one explicitly declared store root.
- Bytes are hashed while bounded and are committed only after the expected digest matches.
- Writes use a unique contained temporary file and atomic same-root promotion. Concurrent writers of identical bytes converge on one immutable asset. A pre-existing asset is rehashed before reuse.
- An existing asset whose bytes do not match its name is corruption and MUST NOT be overwritten silently.
- The store never derives a path from origin host, URL path, component display text, media type, or a caller-provided filename.
- Partial downloads, cancellation, timeouts, and failed digest checks leave no visible committed asset.

## Origin index and review

The origin index schema version is 1. It maps a sanitized canonical origin to an exact SHA-256 and is encoded as canonical UTF-8 JSON with LF newline. Entries are ordered by sanitized origin and then digest using ordinal comparison. Credentials and fragments are absent; sensitive query values MUST be removed before diagnostics or review output.

One origin resolving to different content is a review event, never an implicit update. An acquisition command writes content and an index/lock diff only when the caller requests the mutating operation. Generation consumes pinned digests and does not consult origins.

## Cache layers

- Evidence storage is durable content-addressed input, not a correctness cache.
- Optional inventory/model/output caches are disposable accelerators. Keys include all contract/input digests and implementation versions; values are validated before use.
- Cache locations are never serialized. Cache reads and writes remain beneath a caller-supplied root and follow the Wave A path/link rules.
- Concurrent cache population must be atomic. Corrupt, unknown-version, or incomplete entries are misses, not partial results.
- Deleting all disposable caches and denying network MUST reproduce identical outputs from source-controlled locks and evidence.

Authentication, registry search, signatures, archive extraction, decompression, proxies with credentials, and automatic origin discovery are not admitted by this contract.

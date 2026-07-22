# Dependency Notices diagnostic catalog v1

All identifiers are within the orchestrator-reserved `WUTNOTICE1000`–`WUTNOTICE7999` range. Codes, default severity, source policy, and argument meaning are compatibility contracts.

| Code | Severity | Meaning and required context |
|---|---|---|
| WUTNOTICE1001 | Error | Invalid Package URL; source and original identity. |
| WUTNOTICE1002 | Error | Invalid manual-component contract; JSON pointer/source and remediation. |
| WUTNOTICE1003 | Error | Duplicate canonical Package URL; canonical identity and source. |
| WUTNOTICE2001 | Error | Missing evidence; canonical Package URL and declared source. |
| WUTNOTICE2002 | Error | Evidence digest mismatch; Package URL plus expected and actual lowercase SHA-256. |
| WUTNOTICE3001 | Error | SPDX syntax error; expression source, zero-based offset, expected syntax. |
| WUTNOTICE3002 | Error | LicenseRef has no exact evidence link; Package URL and identifier. |
| WUTNOTICE4001 | Error | Policy denied the effective license subject; matched rule and expression. |
| WUTNOTICE4002 | Warning | Policy requires review; matched rule and expression. |
| WUTNOTICE4003 | Error | Required license obligation/evidence is missing; obligation and subject. |
| WUTNOTICE4004 | Error | OR expression requires an explicit selected branch; observed expression. |
| WUTNOTICE4005 | Error | Selected license is not an exact branch of the observed OR expression. |
| WUTNOTICE6001 | Error | Path is outside the declared root or crosses an unsafe link; sanitized relative path. |
| WUTNOTICE7001 | Error | Network is unavailable to this operation; operation name only. |

Messages must not contain credentials, authorization data, sensitive URL queries, user-home paths, or registry tokens. SPDX parser offsets are zero-based UTF-16 string offsets. Path diagnostics report declared relative input, never resolved host paths.

# ADR 0005: First-party diagnostic identities

- Status: Accepted
- Date: 2026-07-22

## Decision

The draft plans reuse short diagnostic prefixes and collide on `CWH`. Because no implementation has shipped, first-party diagnostics move atomically to globally distinguishable `WebUIToolkit` ranges.

| Draft plan identity | Implementation identity |
|---|---|
| `FLOW001`–`FLOW010` | `WUTFLOW0001`–`WUTFLOW0010` |
| `TR0001`–`TR0024`, `TR0099` | `WUTTEXT0001`–`WUTTEXT0024`, `WUTTEXT0099` |
| `CLI####` | `WUTCLI####` with the numeric portion preserved |
| `DN1###`–`DN7###` | `WUTNOTICE1###`–`WUTNOTICE7###` |
| Hosting `CWH####` | `WUTHOST####` with the numeric portion preserved |
| Template/compiler `CWH0001`, `CWH1###`–`CWH7###` | `WUTHTML0001`, `WUTHTML1###`–`WUTHTML7###` |

Every public diagnostic keeps a stable severity, message arguments, source span policy, remediation page, and snapshot test. Domain tasks must reserve exact IDs in the contract registry before accepting fixtures.

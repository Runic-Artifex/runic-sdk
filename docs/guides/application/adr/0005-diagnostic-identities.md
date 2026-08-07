# ADR 0005: Toolkit diagnostic identities

- Status: Accepted
- Updated: 2026-08-05

## Decision

Toolkit-owned diagnostics use distinct ranges:

- `RTKAB0001`–`RTKAB9999` for Application Bridge generator diagnostics;
- `RTKHOST0001`–`RTKHOST9999` for hosting;
- `RTKFE0001`–`RTKFE9999` for the frontend SDK; and
- `RTKDEV1000`–`RTKDEV1999` for `dotnet-runic-toolkit`.

Independent products reserve and publish their own diagnostics. Toolkit does not
allocate aliases for their former monorepo ranges.

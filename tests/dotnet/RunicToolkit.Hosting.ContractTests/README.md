# Hosting lifecycle contracts

This project is an executable, package-free contract suite. Run it with:

```text
dotnet run --project tests/RunicToolkit.Hosting.ContractTests/RunicToolkit.Hosting.ContractTests.csproj
```

The process prints a fixed TAP-compatible scenario list and exits `0` only when every contract passes. Tests use in-memory hosts, validators, participants, and mode runners. Timeouts advance a manual `TimeProvider`; no scenario uses wall-clock sleeps.

## Scenario catalog

| Contract | Observable guarantee |
|---|---|
| Transition graph | Every legal edge succeeds; an illegal edge throws or returns `false` without changing state. |
| Legal lifecycle | A single run traverses validation, host/participant start, mode execution, reverse stop, host stop, and terminal disposal in order. |
| Validation failure | Deterministic validation errors prevent host and participant startup and map to a stable configuration failure. |
| Illegal reuse | `RunAsync` is single-use, including after completion; repeated disposal is harmless. |
| External cancellation | Cancellation enters the same ordered teardown path and maps to the stable cancelled result. |
| Startup failure | Only participants that completed startup are stopped, in reverse completion order. |
| First failure precedence | An execution or startup failure remains primary when later teardown also fails; teardown failures remain observable as secondary. |
| Startup timeout | Advancing the injected time provider expires startup without any real delay and still performs eligible teardown. |
| Stop timeout | Advancing the injected time provider advances cleanup past a hung stop operation without replacing an earlier primary failure. |
| Total shutdown timeout | One deadline caps active-mode close and all remaining teardown, even when collaborators ignore cancellation. |
| Stop races | The first stop request wins, all callers share completion, and throwing consumer cancellation callbacks cannot strand teardown. |
| Stable failures | Every built-in failure category maps to its documented process exit code, and kernel-produced failures retain stable machine-readable IDs. |

The scenario identifiers and expected event logs are intentionally exact: changing either is a contract change, not a test implementation detail.

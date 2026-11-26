# Protocol-3 fixtures

These fixtures have protocol-3 shapes, such as `"state": {}` in a failure reply. They are **not normative for
protocol 4**, and an implementation of protocol 4 does not run them. The normative cases are the
[conformance corpus](../conformance/README.md), whose `state-changes` and `errors` scenarios converted these fixtures
(each converted scenario's `source` names its fixture).

| Directory | Kept for |
| --- | --- |
| [`collection-deltas`](collection-deltas) | `TrackerConformanceTests` in `tests/dotnet/Runic.Application.Testing.Tests`, which translates them to protocol-4 pushes ([collection deltas](../collection-deltas.md), protocol 3) |
| [`domain-failures`](domain-failures) | The .NET and TypeScript tests of declared failures (`DomainFailureTests`, `DomainFailureCodegenTests`, `outcomes.test.ts`) |

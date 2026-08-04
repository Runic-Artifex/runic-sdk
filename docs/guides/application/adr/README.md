# Architecture decision records

ADRs record durable decisions and their historical context. They are not a
work queue; use the [product roadmap](../roadmap/README.md) for priorities.

## Product and runtime decisions

- [0001 — WebUIToolkit identity](./0001-webuitoolkit-identity.md)
- [0002 — Dependency direction](./0002-dependency-direction.md)
- [0004 — Publication license pending (superseded)](./0004-license-pending.md)
- [0005 — Diagnostic identities](./0005-diagnostic-identities.md)
- [0006 — Target-framework policy](./0006-target-framework-policy.md)
- [0007 — Hosting lifecycle and failure precedence](./0007-hosting-lifecycle-policy.md)
- [0011 — CsWebUi host boundary](./0011-cs-webui-host-boundary.md)
- [0012 — Native HTML and frontend direction](./0012-native-html-and-frontend-direction.md)
- [0013 — Framework-neutral asset and VFS boundary](./0013-framework-neutral-asset-boundary.md)
- [0014 — MIT repository license](./0014-mit-license.md)

## Delivery and coordination decisions

- [0003 — Parallel task and worktree ownership](./0003-parallel-work-ownership.md)
- [0008 — Adapter delivery waves](./0008-adapter-delivery-waves.md)
- [0009 — Agent execution profiles and workflow pilot](./0009-agent-execution-profiles-and-workflow-pilot.md)
- [0010 — Workflow-first agent orchestration](./0010-workflow-first-agent-orchestration.md)

The wave-oriented records preserve the constraints and reasoning in effect when
they were accepted. Current implementation ordering comes from the roadmap and
current path ownership comes from `eng/ownership.json`.

# Planning baseline

The standalone HTML specifications in the repository root are the source planning baseline. They use the former draft `CsWebUi` identity; ADR 0001 overrides that identity for implementation.

The implementation namespace and package root is `WebUIToolkit`.

The canonical cross-project schedule is `webuitoolkit-orchestration.html`. ADR 0008
overrides adapter ordering in the standalone specifications: Wave C contains
CommunityToolkit and HTMX integration, Wave E contains React/Vue/Svelte, and Wave F
contains Angular/ReactiveUI. The deferred specifications remain design inputs, not
authorization to begin those adapters early.

Planning is user-owned. A user may use these HTML plans, `/plan`, Markdown, or a
conversation before launching a workflow, but the workflow runtime does not require
or approve a separate machine-readable Plan. See ADR 0010 and the
[workflow operating manifest](../../eng/codex-workflows/workflow-operating-manifest.md).

The current bounded work before Wave C is summarized in the
[pre-Wave-C remediation brief](pre-wave-c-remediation.md).

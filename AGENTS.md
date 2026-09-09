# Working on Runic SDK

Use the locked development environment described in CONTRIBUTING.md and .envrc.

Match verification to the changed behavior. Run focused tests and relevant
build/type checks locally; GitHub owns the full CI run. Local `bun run ci` is
available for workflow debugging, not a prerequisite for every change.

Do not introduce release gates, evidence formats, mandatory manual checks, or
repeat full verification without identifying a concrete failure they prevent.
Passing the relevant checks completes verification unless new evidence justifies
more work. Track nonblocking limitations as issues.

Use manual checks for affected native/UI behavior that automation cannot cover.
Soaks and matched benchmarks are tools for lifecycle/performance changes and
investigating regressions, not routine publication prerequisites.

The current release policy is eng/release/README.md. Historical readiness plans,
receipts and organization-wide launch documents do not add SDK release gates.
Keep GitHub releases focused on changes, installation, migration and known issues;
retain diagnostic output in Actions artifacts.

Prefer improving an application that uses Runic over adding speculative SDK
abstractions. Record concrete consumer friction and fix it with a focused change.

# Samples

Samples may use source project references for local development. Dedicated
package-consumer fixtures, not samples, verify isolated packed release boundaries.

Wave C adds executable Hosting samples for combined mode, custom composition,
and UI composition, plus the compiled HTMX/CommunityToolkit vertical sample.
`eng/verify-wave-c.ps1` restores and builds all four as acceptance evidence.

Wave G adds `ReferenceApplication`, a deliberately package-only neutral consumer.
It is excluded from the source solution and can only be restored from the isolated
feed produced by `eng/verify-wave-g.ps1`. The reference app composes Hosting, MVVM,
Flow, Text Resources, Command Line, Dependency Notices, and WebUi without a project
reference.

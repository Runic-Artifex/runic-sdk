# Hosting.UI acceptance fixture

A dependency-ready UI lifecycle fixture using Generic Host, manifest validation, a
fake browser adapter, root-session activation, and close convergence.
Generic Host integration is referenced through `WebUIToolkit.Hosting.GenericHost`, not
the dependency-neutral lifecycle kernel.

The fake browser is deliberate: this executable proves deterministic lifecycle
contracts without opening a native window. Runnable demonstrations use the
`WebUIToolkit.Hosting.CsWebUi` adapter under `samples/`.

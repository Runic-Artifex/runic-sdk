# Hosting.UI

A dependency-ready UI lifecycle sample using Generic Host, manifest validation, the
framework-neutral browser adapter, root-session activation, and close convergence.
Generic Host integration is referenced through `WebUIToolkit.Hosting.GenericHost`, not
the dependency-neutral lifecycle kernel.
Replace the sample browser factory with the pinned native adapter when that owner handoff
is available.

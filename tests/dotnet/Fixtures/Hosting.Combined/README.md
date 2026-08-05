# Hosting.Combined acceptance fixture

A small combined-mode composition. Empty input selects UI, a command token selects
command mode, and reserved help/version tokens select their explicit runners. The
CommandLine bridge adapter can replace the fixture command runner once its package is
available in the orchestrator feed. Generic Host composition comes from the dedicated
`RunicToolkit.Hosting.GenericHost` adapter.

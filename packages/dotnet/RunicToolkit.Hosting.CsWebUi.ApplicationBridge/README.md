# RunicToolkit.Hosting.CsWebUi.ApplicationBridge

Carries all application commands through one bounded binary CS-WebUI binding.
Correlated receipts and any events produced during their dispatch return as one
sequence-ordered binding result; later unsolicited host events use the single
receiver. The adapter pins native client identity, permits explicit
reinitialization after a physical reconnect, and owns exact session teardown.

Use `WebUiAppBuilder.UseApplicationBridge` for ordinary applications. It binds
the bridge to the exact native window created by the high-level host and opens a
fresh logical session for that root lifetime.

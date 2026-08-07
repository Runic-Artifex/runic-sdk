# RunicToolkit.Hosting.CsWebUi.ApplicationBridge

Carries all application commands through one bounded binary CsWebUi binding and
all host messages through one receiver. The adapter pins native client identity,
permits explicit reinitialization after a physical reconnect, and owns exact
session teardown.

Use `WebUiAppBuilder.UseApplicationBridge` for ordinary applications. It binds
the bridge to the exact native window created by the high-level host and opens a
fresh logical session for that root lifetime.

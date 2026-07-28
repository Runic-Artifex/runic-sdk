# WebUIToolkit.Desktop

Frontend-neutral desktop capability contracts for CsWebUi applications and
WPF migrations. ViewModels depend on small typed services rather than WPF,
CsWebUi, browser, DOM, or operating-system objects.

Every host publishes a complete `IDesktopCapabilities` report. Calling an
unsupported, unavailable, or permission-gated service produces a stable
`DesktopCapabilityException`; applications can therefore choose a fallback
before invoking a feature.

The contracts intentionally use file contents rather than platform paths and
semantic element identifiers rather than DOM nodes.

The high-level CsWebUi application builder registers:

- `IDesktopApplicationLifetime`, `IDesktopWindow`, `IDesktopFocus`, and
  `IDesktopDispatcher`;
- keyboard accelerators, text clipboard, bounded open/save file content, and
  drag/drop;
- external HTTP(S) launch and notifications;
- browser profile and local-storage policy; and
- application-owned secondary windows.

Clipboard and notification descriptors may be `PermissionRequired`. Callers
can inspect `IDesktopCapabilities.Report` before invoking optional behavior.
An accepted programmatic close runs every registered guard before canceling
`Stopping`; an already-forced native disconnect cannot be vetoed and begins
the same cancellation and teardown path immediately.

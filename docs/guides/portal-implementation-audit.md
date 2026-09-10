# Portal implementation audit — September 2026

This is a focused comparison of Runic's implemented portal paths with ashpd and
Tmds.DBus, not certification of every desktop or a proposal to expose every portal.

## Generation decision

Use Tmds.DBus.Generator with our existing Tmds.DBus.Protocol runtime, both pinned
to 0.95.1. The first pilot generates Registry and Notification proxies from
upstream 1.22.1 XML. The old reflection-oriented Tmds.DBus library and its legacy
codegen path are not needed. See the provider's `Protocol/README.md` for provenance
and reproduction commands.

Ashpd's update script synchronizes XML; its client also uses handwritten semantic
types and shared request infrastructure. XML only specifies many options as
`a{sv}`. It cannot replace platform policy, version-aware options or lifetime rules.

## Findings

| Area | Runic finding | Action |
| --- | --- | --- |
| Host identity | Owning a bus name did not associate the notification connection with a desktop ID. | Fixed before this pilot; generated Registry proxy preserves registration before portal calls. |
| Notification serialization | Envelope, property and signal readers were handwritten. | Replaced with generated proxies; semantic option dictionaries remain explicit. |
| Early notification actions | Callback can precede AddNotification's reply. | Preserve tracking before invoking the generated method; regression test covers this ordering. |
| Request/Response race | File operations subscribe before submitting, correlate returned handles and support older handle paths. | Keep existing implementation/tests during this pilot. |
| Cancellation | File requests close their request handle; notification submissions drain a bounded reply once sent. | Preserve these distinct contracts; do not blindly forward cancellation into generated calls. |
| File descriptors | Tmds owns the wrapper; Runic retains the caller's SafeHandle through a borrowed wrapper. | Keep explicit ownership and existing FD conformance tests. |
| Versions | OpenFile needs v2; chooser/reveal features need v3. Notification public features use v1. | Preserve guards; generating newer members does not enable them automatically. |
| Window parenting | GTK3/GTK4 adapters export owned X11/Wayland parents; owner changes invalidate requests. | Keep native owner adapters outside generated protocol code. |
| Notification portal restart | A persistent session-bus connection may outlive the portal service and lose registered identity. | Follow-up: observe service-owner changes, recreate/register before the next submission, test replacement with a fake service. Do not claim transparent recovery today. |
| Host identity outside notifications | Settings and per-request file connections do not use shared host identity registration. | Follow-up: design explicit connection identity for portal permissions/attribution before adding more identity-sensitive portals. |
| Settings observation | Runic polls and deduplicates; ashpd offers native SettingChanged streams. | Consider signal observation with reconnect/resync if polling becomes measurable friction. |
| Activation focus | Notification ActivateAction platform data is not exposed; Wayland activation token is discarded. | Follow-up: preserve token through presentation dispatch before promising focus/foreground behavior. |
| Live coverage | The generated pilot passed JIT and NativeAOT protocol tests, then a live JIT KDE action/withdrawal check. Earlier identified history checks also passed. | GNOME/macOS acceptance and real Linux cold relaunch remain separate follow-ups. |

## Interactive checks

Reuse the existing native prototype fixture instead of creating a second product
application. `--native-services` supports isolated Notifications, Open,
ChooseApplication and Reveal via `RUNIC_TEST_SERVICE`; notifications include
opt-in diagnostics and require the actual Open result callback. Supply an
installed desktop identity using `RUNIC_TEST_APP_ID`. The 120-second notification
wait withdraws the item when it ends, so check history before cleanup. The
portal-only test executable also supports `--settings` without a GTK host.

When extending this fixture, keep popup visibility, history retention, action
receipt, replacement, withdrawal and cold relaunch separate observations. A
successful AddNotification reply proves request acceptance only.

## References

- [Tmds.DBus protocol and source-generator documentation](https://tmds.github.io/Tmds.DBus/)
- [ashpd host registration](https://github.com/bilelmoussaoui/ashpd/blob/main/client/src/registry.rs)
- [ashpd shared proxy/version/request handling](https://github.com/bilelmoussaoui/ashpd/blob/main/client/src/proxy.rs)
- [ashpd notification types](https://github.com/bilelmoussaoui/ashpd/blob/main/client/src/desktop/notification.rs)
- [ashpd XML synchronization](https://github.com/bilelmoussaoui/ashpd/blob/main/update-interfaces.py)

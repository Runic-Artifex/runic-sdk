# Native verification status

Recorded for the initial Windows x64 implementation on 2026-09-10. These results
apply to the local development machine, not an AD/server test environment.

## Executed

- Release build with Runic verification analyzers; no warnings.
- Windows x64 NativeAOT publish with the entire administration assembly rooted;
  no warning suppressions for reachable interop.
- Native execution of temporary shortcut roundtrips: arguments, Unicode, UNC
  target, malformed files, collision/replacement, selected updates, preserved
  fields and resolution without rewriting the source.
- Process snapshots, current process/parents, missing lookup; read-only SCM
  enumeration, EventLog configuration/status/PID and already-reached/canceled waits.
- Native Task Scheduler folder/task inspection, missing lookup and validation-only
  XML for all ten typed trigger families. Service-account identity is read back
  through the native definition parser. No task was registered or executed.
- Local firewall profiles/rules/missing lookup; no policy mutation.
- LDAP transport initialization from a NativeAOT executable against an unavailable
  loopback endpoint; expected transport error. This establishes transport execution,
  not AD compatibility.
- Local OS/BIOS, membership, Network List Manager and share enumeration/missing
  lookup. Independent OS/process APIs used where available for comparisons.
- Internal native WMI local query, object read and read-only process-owner method invocation.
- Missing GPMC activation reported the native class-not-registered error as Unavailable; no domain operations executed.
- Public API baseline for 115 exported types and their declared public members.
- A package-only consumer copied outside the checkout, restored from the local NuGet package, published as NativeAOT and executed system/service/task/firewall inspection.
- Managed/native-consumer checks for LDAP escaping and GPO link reorder preservation.
  These do not establish server-side LDAP assertion support.

## Not natively accepted

No disposable administrative or AD/DNS/GPMC fixtures were available. The following
implementations therefore remain **implemented but not natively accepted**:

| Stage | Fixture work still required |
| --- | --- |
| Services/processes | Disposable service configuration/failure-action roundtrips; start/stop/pause/continue, timeout, deletion and denied rights. Validate a stopped service separately from missing/denied. |
| Tasks/firewall | Create/read/update/delete including repeated calls; task run and last outcome; preservation of unsupported XML/native settings; password principals; effective Group Policy restrictions and denied access. |
| Directory/AD | Secure integrated/explicit authentication, certificate failures, paging and ranges, binary values, all mutation types, membership/SPNs/UAC concurrency and password change/reset restrictions. |
| System/networks/shares | SMB creation/security roundtrips, SID and ACE order/inheritance retention; remote access and denied rights. Broader hardware/OS comparisons and malformed/unsupported firmware data. |
| DNS | DNS role provider, remote authentication, zones/AD integration, all eight record types and TTL/record-set changes, unsupported record reporting and denied access. |
| GPMC | Installed component path, selected controller, GPO CRUD, backup/import/restore/report outcomes, links/order/inheritance, filtering/delegation and WMI-filter association. |
| Interop | Required domain-operation thread affinity, repeated server mutations and failure cleanup on the fixtures; other architectures not accepted. |

Use snapshot-restorable Windows x64 VMs, a dedicated test domain/OU, a dedicated
existing DNS zone, a test file-server directory, and unique resource names. Test
against the fixture host itself for local-only firewall/network APIs. Supply
delegated and deliberately restricted identities separately. Record returned
native error codes and read back the actual state after every write; clean up only
resources created by that run and revert the fixture snapshot afterward.

No domain/provider error may be replaced with a mock success. If the selected LDAP
dependency fails NativeAOT on a real AD operation, stop that stage and report the
failure; do not add a parallel transport.

## Scope limits

The SDK integration adds this standalone package to the coordinated package
inventory and native Windows CI. Local read-only tests also passed on the Runic
Windows VM on 2026-09-11. This does not establish the administrative/domain
coverage listed above. Consumer adoption is separate from SDK integration.

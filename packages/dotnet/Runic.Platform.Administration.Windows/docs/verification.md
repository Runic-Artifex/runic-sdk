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

## Local mutation acceptance (2026-10-07)

Fixture: the Windows 11 Pro x64 VM `bootstrap-imgui-win11` (build 26200). A
`virsh` snapshot was taken before the first write and restored afterwards. The
verifier was built on the VM from the SDK branch with .NET SDK 10.0.401, as a
NativeAOT publish (`-p:PublishAot=true -p:IlcTreatWarningsAsErrors=true`, no
warnings) and as a framework-dependent JIT publish. The `main` verifier at
`b1c5d5af` gave the same write-suite results.

| Run | Identity | Command | Result |
| --- | --- | --- | --- |
| NativeAOT | elevated (High integrity) | `Runic.AdminVerify.exe local --allow-changes` | 19 passed, exit 0 |
| JIT | elevated | `Runic.AdminVerify.exe local --allow-changes` | 17 passed; `services.lifecycle` and `cleanup.service` failed with Win32 1061 (see below), exit 1. With the cleanup fix, only `services.lifecycle` fails |
| NativeAOT | non-elevated (interactive, `RunLevel Limited`) | `Runic.AdminVerify.exe local --expect-denied` | 13 passed, exit 0 |
| JIT | non-elevated | `Runic.AdminVerify.exe local --expect-denied` | 13 passed, exit 0 |
| NativeAOT | elevated | `Runic.AdminVerify.exe local --expect-denied` | refused as intended (usage error, exit 2) |

Accepted on this fixture:

- Service: create with LocalSystem, duplicate creation reported as `Conflict`,
  description and failure-policy update, start/pause/continue/stop with a live
  PID, delete, and repeated delete reported as absent.
- Scheduled task: folder and SYSTEM task creation, duplicate `Conflict`, a
  description update that preserves the action XML, disable and enable, a real
  run with `LastTaskResult` 0, and repeated deletion.
- Firewall rule: disabled-rule creation, duplicate `Conflict`, an update that
  preserves the other settings, identity-checked deletion, and repeated
  deletion.
- SMB share: creation with an explicit security descriptor, ACL roundtrip, a
  metadata update that preserves the descriptor, ACL replacement, and repeated
  deletion.
- Denied access from a non-elevated process. Each owned write failed with
  `AccessDenied` and left nothing behind:
  - service: `OpenSCManager`, Win32 5;
  - task: `RegisterTaskDefinition`, `0x80070005`;
  - firewall: `INetFwRules.Add`, `0x80070005`;
  - share: `NetShareAdd`, Win32 5.

  A standard user could still create a task folder in the root folder: in
  run `b80751bf` (`main` verifier, non-elevated, `--allow-changes`),
  `CreateFolder` succeeded and the folder was deleted by `cleanup.task-folder`,
  while the task registration was denied. The denied check therefore targets
  the SYSTEM task registration itself.

Independent cleanup check, using `Get-Service`, `Get-ScheduledTask`, the Task
Scheduler root folders, `Get-NetFirewallRule` and `Get-SmbShare` for
`RunicVerify-*`. In the first session, the only resources left were three
stopped services from the JIT runs; their cleanup had skipped `DeleteService`
after `Stop` failed. They were deleted by exact name. The cleanup now always
deletes the service and ends the verifier's own fixture process when stopping
fails. A rerun with that change (`125793e8`) left nothing behind:

- JIT, elevated, `--only services`: `cleanup.service` passed and
  `services.lifecycle` failed as before.
- NativeAOT, elevated, `--allow-changes`: 19 passed.
- NativeAOT, non-elevated, `--expect-denied`: 13 passed.

JIT verifier limitation: when the JIT apphost hosts the verifier's disposable
service, the service registers its control handler and reports `Running` with
stop and pause accepted. The test service's diagnostic log
(`service-diagnostics/<name>.log`) shows what happens next.
`StartServiceCtrlDispatcherW` returns success about 0.2 to 2 seconds after the
start, when the first control arrives, while `ServiceMain` is still waiting.
The control handler is never invoked. The Service Control Manager rejects
that control and the following ones, including interrogate, with
`ERROR_SERVICE_CANNOT_ACCEPT_CTRL` (1061). The process then ends and the
service is reported `Stopped`.

The same source works under NativeAOT. Why the dispatcher returns under JIT is
not yet known. This is a defect in the verifier's test service, not in
`WindowsServiceClient`, which reported the native error correctly. Validate
the service lifecycle with the NativeAOT verifier until the test service is
fixed.

## Not natively accepted

Local service, task, firewall and share writes are accepted above. Without AD,
DNS or GPMC fixtures, the following remain **implemented but not natively
accepted**:

| Stage | Fixture work still required |
| --- | --- |
| Services/processes | Failure actions other than none; reboot actions; timeouts; per-service denied rights beyond SCM creation; remote SCM. |
| Tasks/firewall | Preservation of unsupported XML/native settings; password principals; trigger executions; effective Group Policy restrictions; firewall protocol transitions. |
| Directory/AD | Secure integrated/explicit authentication, certificate failures, paging and ranges, binary values, all mutation types, membership/SPNs/UAC concurrency and password change/reset restrictions. |
| System/networks/shares | ACE order/inheritance retention beyond the tested descriptors; remote shares and remote denied rights. Broader hardware/OS comparisons and malformed/unsupported firmware data. |
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

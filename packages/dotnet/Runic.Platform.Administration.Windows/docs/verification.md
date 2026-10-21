# Native verification status

The package is supported as of 0.10.0. The sections below record what was
verified, on which fixtures, and which implemented features fall outside the
verified scope. Windows CI runs the read-only native checks (JIT and NativeAOT)
on every change; the local write, denied-access and domain suites need the
disposable VMs and run manually (see "Rerunning the suites" below).

## Initial implementation (2026-09-10)

These results apply to the local development machine, not an AD/server test
environment.

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

JIT verifier limitation, fixed on 2026-10-21: when the JIT apphost hosted the
verifier's disposable service, the service registered its control handler and
reported `Running` with stop and pause accepted, but the handler registered with
`RegisterServiceCtrlHandlerW` was never invoked. The Service Control Manager
rejected every control, including interrogate, with
`ERROR_SERVICE_CANNOT_ACCEPT_CTRL` (1061), and the dispatcher returned when the
service was stopped. Neither disabling tiered compilation nor copying the
service table and name to native memory changed that. Registering the handler
with `RegisterServiceCtrlHandlerExW`, as `System.ServiceProcess.ServiceBase`
does, fixed it. The defect was in the verifier's test service, not in
`WindowsServiceClient`, which reported the native error correctly. After the
fix, `local --allow-changes` passed 19/19 under both JIT and NativeAOT on the
Windows 11 VM (build 26200, .NET SDK 10.0.401).

## 0.9 API changes (W319-003)

The 0.9 API review made the SCM, NetShare, domain-membership and DC-locator calls
asynchronous and added the `NotFound` error category. Windows CI (win-x64, JIT and
NativeAOT) runs the read-only native checks against the new shapes, including a
missing service reported as `NotFound` and pre-canceled calls that never start. The
domain acceptance below (W319-009) reran the local write suites and the domain suite
on the final 1.0 shape.

## Domain acceptance (2026-10-20, W319-009)

Fixture: a disposable Windows Server 2025 Standard (build 26100) domain controller
`runic-dc1.runic.test`. It is the forest root of `runic.test` at functional level
Windows2025, with the DNS role, GPMC and an Enterprise Root CA. The CA issued the
DC's LDAPS/StartTLS certificate, and LDAP signing is required. Domain-related
state was restored from a `virsh` snapshot. The client was the Windows 11 VM
from the local acceptance above, joined to the domain, with RSAT GPMC, AD and
DNS tools. Base DN `OU=RunicTests,DC=runic,DC=test` and the AD-integrated zone
`runic-tests.test`. Identities:

- `runic-delegate`: full control of the OU and the zone object, WMI DNS rights
  and GPO creation. It is not an administrator.
- `runic-restricted`: a Domain User with explicit denies on the OU and the zone.

The verifier was built on the client with .NET SDK 10.0.401, as NativeAOT and as
JIT.

| Run | Identity and logon | Command | NativeAOT | JIT |
| --- | --- | --- | --- | --- |
| Domain writes | delegate, own logon (scheduled task) | `domain … --allow-changes` | 39 passed, 2 skipped (Basic binds need an explicit credential), exit 0 | same |
| Domain writes | delegate, explicit `NetworkCredential` | `domain … --allow-changes --only ldap,dns --credential-stdin` | 33 passed, exit 0 | same |
| Denied writes | restricted, own logon | `domain … --expect-denied` | 11 passed, 2 skipped, exit 0 | same |
| Denied writes | restricted, explicit credential | `domain … --expect-denied --credential-stdin` | 13 passed, exit 0 | same |
| Local writes | elevated | `local --allow-changes` | 19 passed, exit 0 | 18 passed; `services.lifecycle` failed with 1061 (JIT test-service limitation above, since fixed) |
| Local denied | non-elevated | `local --expect-denied --only services,tasks,firewall,shares` | 8 passed, exit 0 | same |

Accepted on this fixture:

- Directory/AD:
  - Negotiate binds, signed and sealed, over TLS (636) and over StartTLS, with
    the current identity or an explicit credential. Basic binds over TLS and
    StartTLS with an explicit credential.
  - A TLS connection to the DC's IP address, whose certificate names its DNS
    name, is refused (`Unavailable`, LDAP 81).
  - Paged search with page size 1.
  - Ranged retrieval of a 3100-member group. A plain read returns
    `member;range=0-1499`, and `ReadAllAttributeValuesAsync` and
    `GetMembersAsync` return every member.
  - User/group/computer/OU creation, membership, SPNs, a Unicode attribute,
    rename, binary SID, password reset and change, account enablement and
    deletion.
  - `DiscoverControllerAsync` and `GetMembershipAsync` from the joined client.
  - Schema-typed reads (#447), rerun the same day with the NativeAOT and JIT
    LDAP suite, a full delegate run and a restricted denied run, all passing.
    `description`, `whenCreated`, `userAccountControl`, `distinguishedName`,
    `member` and RootDSE values are `Text`; `objectSid` and `objectGUID` are
    `Binary`.
- DNS: all eight record types through WMI/DCOM with the caller's identity and with
  an explicit `NetworkCredential`. Each type covers create, duplicate `Conflict`,
  an explicit TTL or the server default, a TTL-only update, an in-place data
  change, and deletion with repeated deletion reported as absent.
- GPMC: create, disable, two backups with metadata enumeration, import, restore, an
  XML report, permission read, grant, upgrade and removal with the other
  trustees preserved, rename, clearing the WMI filter, links with order and
  flags, blocked inheritance, link deletion and GPO deletion.
- Denied access for the restricted identity. Each write failed with
  `AccessDenied`, and a read-back showed no change:
  - OU creation and a description change: LDAP 50;
  - GPO creation and a GPO link on the base OU: `0x80070005`;
  - DNS record creation: WMI `0x80041003`.

Defects found and fixed in this run:

- `WindowsDnsClient.UpdateAsync` deleted the record on a TTL-only update. The
  MicrosoftDNS provider deletes a record when `Modify` receives its unchanged data.
- SRV used the zone property `DomainName` instead of `SRVDomainName`.
- Update and delete now read the result back. A provider that reports success
  without applying the change, as `DeleteInstance` does for TXT text that
  contains quotes, is reported as `NativeFailure`.

Independent cleanup check after the runs, on the DC: `OU=RunicTests` had no
children and an unchanged description and `gPLink`. The test zone held only
SOA/NS, and only the two default GPOs existed. On the client, no `RunicVerify-*`
service, task, task folder, firewall rule or share remained.

Limits:

- GPMC uses the calling thread's identity. Under a network-only logon
  (`LOGON32_LOGON_NEW_CREDENTIALS`, as `runas /netonly` uses), GPO creation,
  backup and links work, but report generation fails with `0x80072020`.
  `Get-GPOReport` fails the same way with `0x80070005`. Run Group Policy
  operations under the identity's own logon.
- Not covered: WMI filter associations other than clearing one, more than one
  controller or site, cross-domain trusts, other Windows Server versions, and
  remote DNS servers that are not domain controllers.

## 0.10 typed models and graduation (2026-10-21, W319-010)

The W319-009 fixture (snapshots `clean-promoted` and the joined client) was run again
on the 0.10 shape: typed task boundaries, `WindowsSecurityDescriptor` for share
security, `FirewallProtocol`, `ServiceAcceptedControls`, and
`ActiveDirectoryObjectIdentity` returned by the AD create methods. The verifier was
built on the client with .NET SDK 10.0.401 as NativeAOT (no warnings) and as JIT.

| Run | Identity and logon | Command | NativeAOT | JIT |
| --- | --- | --- | --- | --- |
| Unit tests | — | `Runic.Platform.Administration.Windows.Tests` | 33 passed | 33 passed |
| Local writes | elevated | `local --allow-changes` | 19 passed, exit 0 | 19 passed, exit 0 |
| Local denied | non-elevated | `local --expect-denied --only services,tasks,firewall,shares` | 8 passed, exit 0 | same |
| Domain writes | delegate, own logon (scheduled task) | `domain … --allow-changes` | 39 passed, 2 skipped (Basic binds need an explicit credential), exit 0 | same |
| Domain writes | delegate, explicit `NetworkCredential` | `domain … --allow-changes --only ldap,dns --credential-stdin` | 33 passed, exit 0 | same |
| Denied writes | restricted, own logon | `domain … --expect-denied` | 11 passed, 2 skipped, exit 0 | same |
| Denied writes | restricted, explicit credential | `domain … --expect-denied --credential-stdin` | 13 passed, exit 0 | same |

New in these runs:

- The create methods returned the stored DN and a distinct non-empty `objectGUID` for
  the user, group and computer. The renamed user's `objectGUID` equals the GUID
  `CreateUserAsync` returned.
- The share ACL roundtrip compares `WindowsSecurityDescriptor` values; a
  metadata-only update and a repeated security update kept the exact binary form.
- `services.lifecycle` passed under JIT as well, after the I-080 test-service fix.

Independent cleanup check afterwards: `OU=RunicTests` had no children, the test
zone held only SOA/NS, only the two default GPOs existed, and the client had no
`RunicVerify-*` service, task, firewall rule or share. Both VMs were reverted to
their snapshots.

## Outside the verified scope

Local service, task, firewall and share writes, and the domain scenarios, are
accepted above. The following are implemented and supported, but were not run
on a fixture. Report problems with them like any other defect:

| Stage | Not run on a fixture |
| --- | --- |
| Services/processes | Failure actions other than none; reboot actions; timeouts; per-service denied rights beyond SCM creation; remote SCM. |
| Tasks/firewall | Preservation of unsupported XML/native settings; password principals; trigger executions; effective Group Policy restrictions; firewall protocol transitions. |
| Directory/AD | All mutation types beyond those accepted above, membership/SPN/UAC concurrency, and password change/reset restrictions under a stricter policy. |
| System/networks/shares | ACE order/inheritance retention beyond the tested descriptors; remote shares and remote denied rights. Broader hardware/OS comparisons and malformed/unsupported firmware data. |
| DNS | Non-DC DNS servers, file-backed zones, unsupported record reporting, TXT text that contains quotes (the provider cannot delete it), and sibling-record concurrency. |
| GPMC | Real WMI-filter association, migration tables, multiple controllers, and report generation under a network-only logon (not supported by GPMC). |
| Interop | Required domain-operation thread affinity, repeated server mutations and failure cleanup on the fixtures. Other architectures are not supported (x64 only, by policy). |

## Rerunning the suites

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

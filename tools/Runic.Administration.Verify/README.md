# Runic Windows Administration verifier

Copy this folder to a **Windows x64 Server VM**, preferably under
C:\RunicVerify. Run Runic.AdminVerify.exe from PowerShell or Windows Terminal.
This is a self-contained NativeAOT executable: no .NET SDK/runtime, browser,
Runic Desktop or UI host installation is required.

The tool exercises the current administration library. It is a diagnostic fixture
runner, not a production health check or certification of every library operation.
It does not automatically elevate or accept passwords on the command line.

## Start here

~~~powershell
# Inspection plus owned temporary shortcut files:
.\Runic.AdminVerify.exe local

# After taking a snapshot of a disposable VM, from an elevated terminal:
.\Runic.AdminVerify.exe local --allow-changes
~~~

The write suite creates a uniquely named Windows service, scheduled task/folder,
disabled firewall rule and SMB share, verifies behavior and attempts cleanup.
The executable itself provides the disposable service and task-marker action;
there is no separate service executable to install.

Keep the executable at the same path until the run and its cleanup finish.
Use a local NTFS directory readable/executable by LocalSystem. Avoid a UNC path
or a redirected profile directory for task/service tests.

Use --only to isolate a failing capability:

~~~powershell
.\Runic.AdminVerify.exe local --allow-changes --only services
.\Runic.AdminVerify.exe local --allow-changes --only tasks,firewall
.\Runic.AdminVerify.exe local --only system,processes,networks
~~~

Available local selections: shortcuts, services, tasks, firewall, shares, system,
processes, networks. No --only means every capability in the selected suite.

## Denied access

From a non-elevated terminal, `--expect-denied` attempts the same owned writes
and requires Windows to refuse each one:

~~~powershell
.\Runic.AdminVerify.exe local --expect-denied --only services,tasks,firewall,shares
~~~

The run attempts to create a service, a scheduled task that runs as SYSTEM, a
disabled firewall rule and an SMB share. Each attempt must fail with
`AccessDenied`. A lookup then confirms that the resource is absent. The report's
resource journal records the native code of each denial. The option refuses to
run elevated, because an elevated run would create the resources. It cannot be
combined with `--allow-changes`, and an `--only` selection must include services,
tasks, firewall or shares. If a write unexpectedly succeeds (for example for an
account with delegated rights), the verifier deletes that uniquely named
resource again (the firewall rule only after an identity check), and the check
still fails. Shortcut and inspection checks still run.

## Command line

The verifier follows the Runic command-line conventions:

- `--help` works for the tool and for each command (`local --help`, `domain --help`).
- `--version` prints the version.
- `--output json` writes one `runic.commandline/1` envelope to stdout, with a
  `runic.administration.verify/1` summary as its payload: run ID, report folder
  and the number of passed, failed, skipped and canceled checks. Progress then
  goes to stderr.
- `completion powershell` prints tab completions.

Usage errors return a `RAV1xxx` code. A failed or canceled run returns `RAV2001`
or `RAV2002`. The `--service` and `--task-marker` arguments are internal entry
points for the Service Control Manager and Task Scheduler. They are not commands.

## Generated bindings

All Windows interop uses pinned CsWin32 0.3.346 generated bindings with preserved
HRESULTs; LDAP retains System.DirectoryServices.Protocols. No runtime/SDK
installation is needed on the VM, and no generated native type is public.

## Domain, DNS and Group Policy

Use a disposable test domain and the current Windows account's delegated/admin
rights. GPMC/RSAT is required on the machine running the CLI. DNS checks require a
Windows DNS server with its MicrosoftDNS WMI provider and permitted remote access.

~~~powershell
# Read-only domain inspection:
.\Runic.AdminVerify.exe domain --server dc1.example.test --domain example.test

# Explicit disposable OU and EXISTING DNS test zone; no zone is created:
.\Runic.AdminVerify.exe domain --server dc1.example.test --domain example.test --base-dn "OU=RunicTests,DC=example,DC=test" --dns-zone example.test --allow-changes

# Optional DNS server separate from the controller:
.\Runic.AdminVerify.exe domain --server dc1.example.test --domain example.test --dns-server dns1.example.test --dns-zone example.test --base-dn "OU=RunicTests,DC=example,DC=test" --allow-changes --only dns
~~~

Domain selections: ldap, gpo, dns. Without `--credential-stdin`, the CLI uses
the current Windows identity, with signed/sealed integrated LDAP.

### Explicit and restricted identities

`--credential-stdin` reads two lines from standard input: a user name
(`DOMAIN\user` or a UPN) and its password. Pipe them in; never put a password on
the command line. The option refuses an interactive console (`RAV1009`), which
would echo the password. Only the user name appears in the output and reports. LDAP and DNS receive them as an explicit `NetworkCredential`,
and the LDAP checks then include Basic binds over TLS and StartTLS. GPMC takes no
credential, so the GPO checks run under a network-only logon (`LogonUser` with
`LOGON32_LOGON_NEW_CREDENTIALS`) for that identity. GPMC report generation fails
under such a logon (`0x80072020`; `Get-GPOReport` fails the same way), so run the
`gpo` selection under the identity's own logon, for example from a scheduled
task that runs as that user, and use `--credential-stdin` with `--only ldap,dns`.

~~~powershell
# A restricted identity: every owned write below --base-dn and in --dns-zone must be denied.
.\Runic.AdminVerify.exe domain --server dc1.example.test --domain example.test --base-dn "OU=RunicTests,DC=example,DC=test" --dns-zone example.test --expect-denied
~~~

With `--expect-denied`, the domain suite attempts to create an OU below
`--base-dn`, change its description, create a GPO, link the Default Domain Policy
to `--base-dn` and create an A record in `--dns-zone`. Each must fail with
`AccessDenied`, and an independent read must show that nothing changed. A write
that unexpectedly succeeds is rolled back at once, and the check fails. The
restricted identity may not be able to read DNS records; the report then says
that record absence must be verified independently.

### Domain writes

For writes, it creates its own child OU under --base-dn. All AD objects and GPO
links belong to that child OU; existing OU settings are not changed. GPO names,
GUIDs and DNS owners are unique to the run. The GPOs are empty and disabled.

Generated test-account passwords remain only in memory. Password-change checks
may fail because of domain minimum-password-age or other policy restrictions;
the report retains the native code for diagnosis. It does not treat a policy
rejection as proof of an interop defect.

DNS checks use synthetic unique owners and documentation IP addresses. PTR is a
record-data roundtrip in the supplied test zone, not a reverse-resolution test.
No production hostname, zone apex, existing record or server configuration is
replaced. Do not use a production domain or DNS zone.

## Reports and cleanup

Every run creates a new folder under .\runic-results. Change the parent with
--out C:\RunicReports. Reports are updated after every check:

- report.txt: readable summary, native error identities and resource journal.
- report.json: timings, error type/stack, native category/domain/code and targets.
- gpo-backup: retained fixture backups when GPMC backup was tested.

Bring the entire report folder back for diagnosis. Review before sharing: reports
include machine/domain names, paths, object identities and error stack traces,
but do not intentionally record credentials, account passwords or LDAP payloads.

Cleanup uses only successfully created run-owned identities. Its checks appear
separately. A failed native creation can have partially succeeded before throwing;
the resource journal therefore lists attempted resources too. If cleanup fails,
or the process/VM is terminated, inspect those exact resources and revert the
fixture snapshot. Do not delete unrelated resources by broad name searches.

Ctrl+C requests cancellation and still permits cleanup. A blocking native call can
finish after cancellation; no rollback is promised. Force-closing the process
can leave fixture resources behind.

Exit codes:
- 0: no failures/cancellations among selected checks. SKIP is not acceptance.
- 1: a check, cleanup or cancellation failed.
- 2: invalid command line or unsupported platform.

Unavailable components and denied permissions are reported as failures when
selected, not silently converted to passes. The exception is `--expect-denied`:
there a denial is the expected result, and a write that succeeds fails the check
(the created resource is deleted again first). Capabilities excluded with --only
are not run. Without --allow-changes, the mutation checks are explicitly skipped,
or replaced by the denial checks under `--expect-denied`.

## Coverage

Local: OS/BIOS/membership, process snapshots, network enumeration, shortcut
metadata/Unicode/UNC/collision/malformed data, service configuration and
start/pause/continue/stop/PID, task XML preservation and real execution/exit result,
disabled firewall-rule updates and duplicate handling, cleanup/absence checks even
when firewall creation throws, optional stored share security, explicit owner/group/ACL
roundtrips, security-descriptor preservation and repeated deletion.

Domain: RootDSE and explicit OU lookup; DC discovery from the joined client;
LDAP over TLS and StartTLS with Negotiate and (with an explicit credential) Basic
binds, and rejection of a TLS certificate that does not name the requested host;
page-size-one search, user/group/computer creation, membership and SPNs, attribute
edits/rename, binary SID, password reset, account enablement and password change;
ranged retrieval of a 3100-member group; GPO backup/import/restore/XML report,
permission grant/upgrade/removal, rename, no-filter association, link
ordering/flags and inheritance; eight DNS record types with TTL and data updates
and cleanup; and, with `--expect-denied`, denied LDAP, GPO and DNS writes for a
restricted identity.

Not covered as full acceptance: all task trigger
executions, task password identities, real WMI
filter associations, denied-rights matrices beyond the listed writes,
all firewall protocol transitions, DNS sibling-record concurrency, service
failure/reboot actions, other architectures and all Windows Server versions.

The local write and denied-access suites passed on the Windows 11 VM on
2026-10-07. See the
[verification guide](../../packages/dotnet/Runic.Platform.Administration.Windows/docs/verification.md).

The test service registers its control handler with
`RegisterServiceCtrlHandlerExW`. With the older `RegisterServiceCtrlHandlerW`, a
JIT build (`dotnet publish` without `-p:PublishAot=true`) never received a
control: the Service Control Manager rejected each one with
`ERROR_SERVICE_CANNOT_ACCEPT_CTRL` (1061), and `services.lifecycle` failed. The
service lifecycle now passes in JIT and NativeAOT builds.

Service cleanup always calls `DeleteService`. If stopping fails, it ends the
service's process (only after checking that its image is this verifier) and
waits until the service is gone. If cleanup still fails, delete the service with
`sc.exe delete <name>`, using the name from the resource journal.

To investigate the test service, create a `service-diagnostics` directory next
to the executable. Each test service then appends its dispatcher,
`SetServiceStatus` and control events to `service-diagnostics/<name>.log`.

## Retesting firewall and share failures

On the disposable VM, run the rebuilt executable from an elevated terminal:

~~~powershell
.\Runic.AdminVerify.exe local --allow-changes --only firewall,shares
~~~

Keep both report.json and report.txt. A failed firewall Add now includes native
protocol/port/profile details, and cleanup checks whether the attempted rule exists.
The `shares.explicit-security` fixture creates a share with the required explicit
ACL and verifies it, metadata preservation, ACL updates and cleanup. These mutation checks must still run on the VM;
a successful NativeAOT build or read-only local run does not validate them.

## Build from the SDK checkout

~~~powershell
dotnet publish tools/Runic.Administration.Verify -c Release -r win-x64 -p:PublishAot=true -p:RunicApplicationBuildMode=Verification -o artifacts/administration/verifier-win-x64
~~~

The CLI is a non-shipping repository tool and references the administration
project directly. The published artifact has no source-tree references. No
application adoption, commit, push or package publication is performed.

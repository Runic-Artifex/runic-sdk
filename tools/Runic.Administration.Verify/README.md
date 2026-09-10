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

Domain selections: ldap, gpo, dns. The CLI uses signed/sealed integrated LDAP.
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
selected, not silently converted to passes. Capabilities excluded with --only
are not run. Mutation checks are explicitly skipped without --allow-changes.

## Coverage

Local: OS/BIOS/membership, process snapshots, network enumeration, shortcut
metadata/Unicode/UNC/collision/malformed data, service configuration and
start/pause/continue/stop/PID, task XML preservation and real execution/exit result,
disabled firewall-rule updates and duplicate handling, share security-descriptor
preservation and repeated deletion.

Domain: RootDSE and explicit OU lookup; page-size-one search, user/group/computer
creation, membership and SPNs, attribute edits/rename, binary SID, password reset,
account enablement and password change; GPO backup/import/restore/XML report,
permission inspection, no-filter association, link ordering/flags and inheritance;
eight DNS record types with TTL updates and cleanup.

Not covered as full acceptance: large ranged LDAP attributes, all task trigger
executions, task password identities, GPO security-delegation writes or real WMI
filter associations, denied-rights matrices, all firewall protocol transitions,
DNS sibling-record concurrency, service failure/reboot actions, other
architectures and all Windows Server versions. The VM runs themselves have not
been executed on the development machine.

## Build from the SDK checkout

~~~powershell
dotnet publish tools/Runic.Administration.Verify -c Release -r win-x64 -p:PublishAot=true -p:RunicToolkitBuildMode=Verification -o artifacts/administration/verifier-win-x64
~~~

The CLI is a non-shipping repository tool and references the administration
project directly. The published artifact has no source-tree references. No
application adoption, commit, push or package publication is performed.

# Runic.Platform.Administration.Windows

Host-independent Windows administration for .NET 10. Construct a capability client
directly; no Runic Desktop, Application host, UI framework, dependency injection,
automatic elevation or helper process is required.

## Support

The package is supported and follows the Runic compatibility rules
([support policy](https://github.com/Runic-Artifex/runic-sdk/blob/main/eng/release/README.md)).
Its behaviour is accepted on disposable fixtures: local writes and denied
writes on Windows 11 x64, and LDAP/AD, DNS and Group Policy against a Windows
Server 2025 domain controller with a delegated and a restricted identity, under
NativeAOT and JIT. [Verification](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform.Administration.Windows/docs/verification.md)
lists the accepted scenarios and the implemented features that fall outside
them, such as remote SCM and remote shares, reboot failure actions, multiple
domain controllers and non-DC DNS servers.

- **Architecture: Windows x64 only, by policy.** No arm64 or x86 support is
  planned. The COM-based clients (tasks, firewall, networks, DNS and Group Policy)
  throw `PlatformNotSupportedException` in a process of another architecture; the
  others are not supported there either. On Windows on Arm, publish for `win-x64`,
  which runs under emulation.
- **Operating systems:** Windows 10/11 and Windows Server 2016 or later, within the
  versions the consuming .NET 10 runtime supports. Not every server version and
  role combination is tested.

## Capabilities and prerequisites

| Namespace / client | Implemented scope | Target and privileges |
| --- | --- | --- |
| Shortcuts / WindowsShellLinkClient | Read/create/selected update; explicit bounded resolution; arguments, working directory, icon, description, show state, hotkey | Local link files, including UNC targets; file permissions. Reading never resolves the target. |
| Services / WindowsServiceClient | Enumeration, configuration/status/PID; create/update/delete; lifecycle, waits; description, delayed start, failure actions | Local or explicit SCM machine, caller identity; operation-specific SCM/service rights. Reboot failure actions need the appropriate privilege. |
| Processes / WindowsProcessClient | PID/parent snapshots, lookup and children | Local read-only Toolhelp snapshot; no launch or termination API. A service snapshot supplies its process ID. |
| Tasks / WindowsTaskSchedulerClient | Folders/tasks; typed definitions, XML import/export, selected updates, validation, enable/run/stop/delete; ten trigger families | Local or explicit machine, caller identity; Task Scheduler service and task/folder permissions. Registration password supplied separately. |
| Firewall / WindowsFirewallClient | Rules, identity checks, selected updates/deletion and effective profile inspection | Local Windows Firewall service; writes need policy access. Ambiguous native names are rejected. No global disable API. |
| DirectoryServices / WindowsDomainClient | Domain membership/role and controller discovery | Local or explicit computer, native NetAPI access. |
| DirectoryServices / WindowsDirectoryClient | RootDSE, paged searches, ranged values, typed attributes, create/modify/delete/rename/move | Explicit LDAP endpoint, caller or explicit credentials; delegated directory rights. Signed/sealed Negotiate, LDAPS or StartTLS. |
| DirectoryServices / ActiveDirectoryClient | User/computer/group/OU creation; memberships, account enablement, password operations and SPNs | Explicit directory client; appropriate AD rights. Newly created user/computer accounts are disabled. No installation/domain naming policy. |
| SystemInformation / WindowsSystemInformationClient | OS version/product/language and BIOS | Local, normally read-only access; registry, version and SMBIOS APIs. Domain membership and role are on WindowsDomainClient. |
| Networks / WindowsNetworkClient | Network identities, categories, connectivity and domain type | Local Network List Manager COM; read-only. |
| Shares / WindowsShareClient | SMB enumeration, configuration, creation/update/deletion and self-relative security descriptors | Local or explicit server; NetShare permissions. Security reads/writes may require elevated rights. Filesystem ACLs are untouched. |
| Dns / WindowsDnsClient | Zones/AD integration; A, AAAA, CNAME, PTR, MX, TXT, SRV and NS record operations | Explicit Windows DNS server; MicrosoftDNS WMI provider, DCOM/WMI access and DNS administration rights. No zone/server configuration writes. |
| GroupPolicy / WindowsGroupPolicyClient | GUID-based GPO operations; GPMC backup/import/restore/report; links/order/inheritance; permissions and WMI-filter associations | Explicit domain/controller, caller identity; GPMC/RSAT installed and delegated AD/GPO/SYSVOL permissions. |

System.DirectoryServices.Protocols 10.0.10 is the only package dependency. All other
Windows interop uses private CsWin32 bindings generated at build time; no Windows SDK
or generated type appears in the public API.

Network snapshots include `AdapterIds` from Network List Manager connections. Match
them to `System.Net.NetworkInformation.NetworkInterface.Id` to find the adapters of a
domain-authenticated network; the network category alone does not establish adapter
membership.

## API semantics

- Clients are concrete sealed classes without interfaces, like `ServiceController`
  and `LdapConnection`. To substitute them in tests, wrap the members you use.
- Operations that can reach another machine or block on I/O are
  `…Async(…, CancellationToken)`: services, shares, domain membership and controller
  discovery, tasks, DNS, LDAP/AD and Group Policy, as well as the local COM-based
  firewall, network and shell-link clients. Local snapshot reads stay synchronous:
  `WindowsProcessClient` and `GetOperatingSystem`/`GetBiosInformation`. SCM, NetAPI
  and DC-locator calls run off the caller's thread. All async members return `Task`.
- Find returns null only for native absence. Enumeration errors are failures, not
  successful empty lists. Read models use immutable collections; write specifications
  are separate and selected updates use null to preserve a setting.
- Operational failures throw WindowsAdministrationException; this package does not
  return `Runic.Platform.PlatformResult`. That is deliberate: like
  `ServiceController`, `System.DirectoryServices.Protocols` and the CIM client, it
  wraps multi-step, non-transactional native operations whose callers need the
  native error identity. `PlatformResult` describes optional host capabilities, where
  "unavailable" is a normal outcome. In this README, "Unavailable" always means
  `AdministrationErrorCategory.Unavailable`.
- WindowsAdministrationException retains operation, error category, native domain
  and numeric code. LDAP exceptions remain available as inner exceptions, including
  native diagnostic context. Do not log request models or credentials.
- `AdministrationErrorCategory` is open: a minor release may add categories, so
  handle unknown values. Its zero value, `Unknown`, is never reported.

  | Category | Meaning | Examples |
  | --- | --- | --- |
  | AccessDenied | Windows denied access. | Win32 5, 1314, 1326; WMI access denied; LDAP 49, 50. |
  | NotFound | The named target does not exist. | Win32 2, 3; a missing service (1060) or share (2310); a missing directory object (8240, LDAP 32); a missing WMI instance. |
  | Unavailable | The machine, endpoint or a required component is unavailable. | Network path or RPC server unavailable (53, 64, 67, 1722, 1726); GPMC not installed; missing WMI namespace or class; LDAP server down. |
  | Conflict | The operation conflicts with current state. | An existing object or share name; a running service; a changed LDAP assertion. |
  | InvalidData | Windows returned, or rejected, invalid data. | Win32 13, 24, 87; invalid task XML; LDAP schema violations. |
  | NativeFailure | Any other native error. | Codes no other category describes. |
- Argument exceptions describe caller mistakes; PlatformNotSupportedException
  describes unsupported platforms. GPMC missing registration and missing WMI
  namespaces/classes are identifiable Unavailable failures.
- Creation fails on collisions except explicit replacement options. Deletion
  returns whether the resource existed. GPO display names are labels, not unique
  identities; creating a GPO allocates a new GUID.
- Mutations are not transactions. Multiple property setters may partially succeed.
  Inspect state after a failure before deciding what to retry. Cancellation before
  native execution prevents that operation; once an indivisible native mutation
  starts, its actual result is returned. A cancellation request never means rollback.
- Native COM runs on an owned apartment. The implementation does not set global
  COM security. WMI uses explicit per-proxy authentication and packet privacy.
  Native RPC/COM calls can outlive a cancellation request; LDAP timeouts and WMI
  query wait bounds are not universal hard deadlines.
- Process IDs are `int`, as in `System.Diagnostics.Process`. Raw native numbers
  carry a `Native` prefix (`NativeFlags`, `NativeAddressType`, `NativeRecordClass`).
  Native values that have a typed form use an enum that passes unknown values
  through: `ServiceStatus.AcceptedControls` is the `[Flags]` enum
  `ServiceAcceptedControls`, and `FirewallRuleSpecification.Protocol` is
  `FirewallProtocol`, an open enum of IANA protocol numbers with `Any` (256); cast
  other numbers, for example `(FirewallProtocol)47`.
- Group Policy times (`GroupPolicySnapshot.CreationTime`/`ModificationTime`,
  `GroupPolicyBackup.Timestamp`) are `DateTimeOffset` values in the local time zone of
  the computer running the client, where GPMC runs. Task Scheduler times
  (`ScheduledTaskSnapshot.LastRunTime`/`NextRunTime`) are `DateTime` wall-clock times
  of the target machine with `Kind=Unspecified`; for a remote machine their offset is
  not known.
- Task trigger boundaries (`TaskTrigger.StartBoundary`/`EndBoundary`) are `DateTime`
  values with whole seconds, and their `Kind` selects how Task Scheduler reads them:
  `Unspecified` is a wall-clock time of the target machine, written without an offset
  (Task Scheduler's default); `Utc` is written with `Z`, and `Local` with the offset of
  the computer running the client at that time. Both are points in time ("synchronize
  across time zones").
- GetOperatingSystem exposes RegistryProductName verbatim; Windows can retain a Windows 10 compatibility label on Windows 11. GetOperatingSystemNameAsync reads the display name from native Windows inventory.
- Service BinaryCommandLine is the configured command line, not an executable path.
  Writes require a quoted executable path when the command line contains
  whitespace, and new services require an explicit account; there is no
  implicit LocalSystem default.
  Process observations are snapshots: a process may disappear after enumeration.
- Task snapshots retain complete native XML; selected updates preserve untouched
  XML subtrees. Replacing actions/triggers intentionally replaces those subtrees.
  Registration, scheduler state and last execution outcome are separate values.
- Firewall names can be ambiguous and have no public persistent GUID in this API.
  Use the returned identity for writes; changed/ambiguous identity fails. Updates
  retain unrelated native settings by editing the existing rule.
- DNS keys include full record data, not just owner name. A and AAAA addresses are
  `IPAddress` values; TTLs are `TimeSpan` values in whole seconds, and a
  specification's `TimeToLive = null` lets the server choose the TTL. Updating one record does
  not replace sibling records. Unsupported types are returned as Unsupported with
  native class and text; they cannot be written through the curated typed API.
  TXT data follows the DNS WMI provider's quoted textual representation.
- Share security descriptors are `WindowsSecurityDescriptor` values: immutable,
  compared by their self-relative binary form, with `Sddl`, `FromSddl`,
  `FromBinaryForm`, `From(RawSecurityDescriptor)` and `ToRawSecurityDescriptor()`.
  They keep SID/ACE information and ordering exactly. They represent share
  permissions, never filesystem ACLs.
- The Active Directory create methods return the new object's
  `ActiveDirectoryObjectIdentity` (stored distinguished name and `objectGUID`), read on
  the same connection after the add. If that read fails, the exception's operation is
  "Read created directory object" and the object exists.
- GPMC operations return `GroupPolicyOperationOutcome<T>`, a success value with
  GPMC's detailed status collection; a failed overall HRESULT throws
  `GroupPolicyOperationException`. Import overwrites settings in an explicitly selected destination GPO;
  restore uses the backup's original identity. No backup-selection heuristics,
  migration orchestration or SYSVOL file-copy implementation is included.
- `WindowsGroupPolicyClient.EnumerateBackupsAsync(directory)` enumerates all GPMC
  backups in a directory, with IDs, display names, timestamps and comments. It needs
  local GPMC but no domain connection. Pass the selected backup's ID to `ImportAsync`.
- Link reordering uses an LDAP assertion against the read gpLink value, preserving
  each entry's flags and rejecting concurrent changes instead of overwriting them.

The read surfaces are curated to the listed capabilities, not arbitrary native
object cloning APIs. Use task XML for task settings outside the typed model.
Selected updates must not be used to recreate an object from a partial snapshot.

## Console examples

[Examples](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform.Administration.Windows/docs/examples.md) cover each capability. The repository's
examples/dotnet/Runic.Administration.Console is a package-only consumer with
read-only commands. It can be copied outside this repository and restored from a
local package; it has no source-tree or desktop-host references.

## Local package and verification

From the SDK root:

~~~powershell
dotnet run --project tests/dotnet/Runic.Platform.Administration.Windows.Tests -c Release -p:RunicApplicationBuildMode=Verification
dotnet publish tests/dotnet/Runic.Platform.Administration.Windows.Tests -c Release -r win-x64 -p:PublishAot=true -p:RunicApplicationBuildMode=Verification -o artifacts/administration/native-tests
& ./artifacts/administration/native-tests/Runic.Platform.Administration.Windows.Tests.exe

dotnet pack packages/dotnet/Runic.Platform.Administration.Windows -c Release -p:RunicApplicationBuildMode=Verification -p:PackageVersion=0.2.0-administration.local.2 -o artifacts/administration/packages
dotnet restore examples/dotnet/Runic.Administration.Console --source ./artifacts/administration/packages --source https://api.nuget.org/v3/index.json
dotnet run --project examples/dotnet/Runic.Administration.Console --no-restore -- system
~~~

`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` record the public API. Native
verification roots the entire administration
assembly so unexecuted public capabilities still receive AOT analysis. That
analysis does not substitute for executing those capabilities on their fixtures.

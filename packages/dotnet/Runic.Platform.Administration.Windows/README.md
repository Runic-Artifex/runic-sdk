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
| Services / WindowsServiceClient | Enumeration, configuration/status/PID and parsed executable path; create/update/delete; lifecycle, stop with dependents, waits; description, delayed start, failure actions | Local or explicit SCM machine, caller identity; operation-specific SCM/service rights. Reboot failure actions need the appropriate privilege. |
| Processes / WindowsProcessClient | PID/parent/image-path snapshots, lookup by ID or image path and children; close-or-kill, terminate, start-and-wait (optionally elevated) | Local; caller identity. Closing or terminating another user's process needs the rights to it. A service snapshot supplies its process ID. |
| Tasks / WindowsTaskSchedulerClient | Folders/tasks; typed definitions, XML import/export, selected updates, validation, enable/run/stop/delete; ten trigger families | Local or explicit machine, caller identity; Task Scheduler service and task/folder permissions. Registration password supplied separately. |
| Firewall / WindowsFirewallClient | Rules, identity checks, selected updates/deletion, deletion of every rule of a name, local policy state and effective profile inspection | Local Windows Firewall service; writes need policy access. Ambiguous native names are rejected for single-rule operations. No global disable API. |
| Accounts / WindowsLocalGroupClient | Local group membership: add, remove and enumerate members; groups and members by name or SID; group names on the target computer | Local or explicit computer, caller identity; administrative rights to change membership. "Already a member" and "not a member" are results, not failures. |
| DirectoryServices / WindowsDomainClient | Domain membership/role with the domain's flat, DNS and forest names; controller discovery with DNS or flat names | Local or explicit computer, native NetAPI access. Membership is read without contacting a controller. |
| DirectoryServices / WindowsDirectoryClient | RootDSE, paged searches, ranged values, typed attributes, create/modify/delete/rename/move | Explicit LDAP endpoint, caller or explicit credentials; delegated directory rights. Signed/sealed Negotiate, LDAPS or StartTLS. |
| DirectoryServices / ActiveDirectoryClient | User/computer/group/OU creation; memberships, account enablement, password operations and SPNs | Explicit directory client; appropriate AD rights. Newly created user/computer accounts are disabled. No installation/domain naming policy. |
| SystemInformation / WindowsSystemInformationClient | OS version/product/language, BIOS and the console session's user | Local, normally read-only access; registry, version and SMBIOS APIs. Domain membership and role are on WindowsDomainClient. |
| Networks / WindowsNetworkClient | Network identities, categories, connectivity and domain type | Local Network List Manager COM; read-only. |
| Shares / WindowsShareClient | SMB enumeration, configuration, creation/update/deletion and self-relative security descriptors | Local or explicit server; NetShare permissions. Security reads/writes may require elevated rights. Filesystem ACLs are untouched. |
| Dns / WindowsDnsClient | Zones/AD integration; A, AAAA, CNAME, PTR, MX, TXT, SRV and NS record operations | Explicit Windows DNS server; MicrosoftDNS WMI provider, DCOM/WMI access and DNS administration rights. No zone/server configuration writes. |
| Dns / WindowsDnsResolverClient | Resolver queries (A, AAAA, CNAME, PTR, MX, TXT, SRV, NS; other types untyped) and reverse lookups against one chosen DNS server, with the response code | Any DNS server by IPv4 or IPv6 address, port 53; network access only, no administration rights. |
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
- `WindowsDirectoryClient` opens and binds an LDAP connection for each operation by
  default. Set `DirectoryConnectionOptions.ReuseConnection` to keep one bound
  connection for the client's lifetime: operations then run one at a time on it, a
  connection failure closes it (the failed operation is not retried and the next one
  binds again), and `Dispose()` closes it. The client is `IDisposable` in both modes;
  after `Dispose()` its operations throw `ObjectDisposedException`.
  `ActiveDirectoryClient` uses the directory client it is given and does not dispose it.
- Operations that can reach another machine or block on I/O are
  `…Async(…, CancellationToken)`: services, shares, domain membership and controller
  discovery, tasks, DNS, LDAP/AD and Group Policy, as well as the local COM-based
  firewall, network and shell-link clients. Local snapshot reads stay synchronous:
  `WindowsProcessClient.Enumerate`/`Find`/`FindByImagePath`/`GetChildren` and
  `GetOperatingSystem`/`GetBiosInformation`/`GetActiveConsoleUser`; closing,
  terminating and starting processes are asynchronous. SCM, NetAPI
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
  | Unavailable | The machine, endpoint or service is unreachable or not running. | Network path or RPC server unavailable (53, 64, 67, 1722, 1726); Task Scheduler service not running; LDAP server down. |
  | NotInstalled | A required component is not installed or registered. | GPMC not installed (class not registered, 0x80040154); missing WMI namespace or class, such as the MicrosoftDNS provider on a computer without the DNS role. |
  | Conflict | The operation conflicts with current state. | An existing object or share name; a running service; a changed LDAP assertion. |
  | InvalidData | Windows returned, or rejected, invalid data. | Win32 13, 24, 87; invalid task XML; LDAP schema violations. |
  | NativeFailure | Any other native error. | Codes no other category describes. |
- Argument exceptions describe caller mistakes; PlatformNotSupportedException
  describes unsupported platforms. GPMC missing registration and missing WMI
  namespaces/classes are identifiable NotInstalled failures, distinct from an
  unreachable controller or server (Unavailable).
- Creation fails on collisions except explicit replacement options. Deletion
  returns whether the resource existed. GPO display names are labels, not unique
  identities; creating a GPO allocates a new GUID.
- Mutations are not transactions. Multiple property setters may partially succeed.
  Inspect state after a failure before deciding what to retry. Cancellation before
  native execution prevents that operation; once an indivisible native mutation
  starts, its actual result is returned. A cancellation request never means rollback.
- Native COM runs on an owned apartment. The implementation does not set global
  COM security. WMI uses explicit per-proxy authentication and packet privacy. A
  remote WMI connection (`WindowsDnsClient` with another server) authenticates with
  Kerberos to the server's `host/<name>` principal from the connect call on, so it
  never falls back to NTLM: name the server by its DNS or NetBIOS name, not an IP
  address, and the server must be in a domain or trust the caller's realm.
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
- Service BinaryCommandLine is the configured command line; `ExecutablePath` is its
  executable as the SCM parses it: a quoted path as written, and for an unquoted
  path with spaces the first prefix that exists as a file (on the local computer;
  remotely the shortest prefix ending in `.exe`).
  `StopWithDependentsAsync(name, timeout)` stops the active dependents first, in
  dependency order, and returns them; `StopAsync` alone fails with 1051 (`Conflict`)
  while a dependent runs.
  Writes require a quoted executable path when the command line contains
  whitespace, and new services require an explicit account; there is no
  implicit LocalSystem default.
  Process observations are snapshots: a process may disappear after enumeration,
  and Windows reuses process IDs. `ProcessSnapshot.ImagePath` is the full path, or
  null when Windows denies access. `CloseOrKillAsync(id, timeout)` posts `WM_CLOSE`
  to the process's visible, unowned top-level windows (never to a console window,
  which would signal every process on that console), waits, and terminates it
  otherwise, reporting `ProcessCloseOutcome`; a process without such a window is
  terminated at once. `TerminateAsync` is `taskkill /F`; once termination is
  requested, its result is returned even if cancellation is requested.
  `StartAndWaitAsync` returns the exit code; `Elevated` starts through the shell's
  `runas` verb. Cancellation stops waiting and never kills or closes anything.
- `GetActiveConsoleUser()` returns the user signed in at the physical console, also
  from a service running as SYSTEM, or null.
- Task snapshots retain complete native XML; selected updates preserve untouched
  XML subtrees. Replacing actions/triggers intentionally replaces those subtrees.
  Registration, scheduler state and last execution outcome are separate values.
- Local groups are addressed by `AccountReference.Sid` or `AccountReference.Name`.
  Use the SID of a well-known group, for example
  `new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)`
  (`S-1-5-32-544`), because group names are localized ("Administratoren");
  `GetGroupNameAsync` returns the name on the target computer. `AddMemberAsync`
  returns false when the account already is a member and `RemoveMemberAsync` when
  it is not; a missing group or account is `NotFound`. Members are returned with
  SID, `DOMAIN\name` (empty when the SID cannot be resolved) and `AccountKind`.
- Firewall names can be ambiguous and have no public persistent GUID in this API.
  Use the returned identity for writes; changed/ambiguous identity fails. Updates
  retain unrelated native settings by editing the existing rule. Neither create nor
  update produces an inbound allow rule without a program, service, local port or
  ICMP protocol (`ArgumentException`); an existing rule of that kind can still be
  updated, for example disabled. Delete renames the rule it checked to a unique
  temporary name and removes that name, so a rule that takes the name concurrently
  is not removed. `DeleteAllAsync(name)` deletes every rule of that name in the
  same way, as `netsh advfirewall firewall delete rule name=…` does, and returns
  the number deleted.
- Firewall writes are refused (`AccessDenied`) while `GetLocalPolicyStateAsync()`
  is not `FirewallLocalPolicyState.Effective`, that is, while Group Policy
  overrides local rules or inbound traffic is blocked. An installer that maintains
  the local store anyway opts in with
  `new WindowsFirewallClient(new FirewallClientOptions { WriteWhenLocalPolicyOverridden = true })`
  and reports the state; the rules apply again once the policy allows local rules.
- DNS keys include full record data, not just owner name. A and AAAA addresses are
  `IPAddress` values; TTLs are `TimeSpan` values in whole seconds, and a
  specification's `TimeToLive = null` lets the server choose the TTL. Updating one record does
  not replace sibling records. Unsupported types are returned as Unsupported with
  native class and text; they cannot be written through the curated typed API.
  TXT data follows the DNS WMI provider's quoted textual representation.
- `WindowsDnsResolverClient` asks the server it was created for (`DnsQueryEx` with a
  server list), not the computer's configured resolvers, and uses neither the
  resolver cache, the hosts file, LLMNR, NetBIOS nor DNS suffixes. A name error
  (NXDOMAIN) or another response code is a `DnsResolution` result with no answers;
  a server that does not answer is `Unavailable` (1460). Answers are the answer
  section with owner name, type, TTL and `DnsRecordData`; a CNAME chain returns
  each step. `ReverseLookupName(address)` builds the `in-addr.arpa`/`ip6.arpa` name.
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
examples/dotnet/Runic.Administration.Console is a console consumer with
read-only commands and no desktop-host references. In this repository it
references the package's project, and CI runs its `system` and `services`
commands on Windows x64. To use it outside the repository, replace the
`ProjectReference` with
`<PackageReference Include="Runic.Platform.Administration.Windows" Version="..." />`.

## Local package and verification

From the SDK root:

~~~powershell
dotnet run --project tests/dotnet/Runic.Platform.Administration.Windows.Tests -c Release -p:RunicApplicationBuildMode=Verification
dotnet publish tests/dotnet/Runic.Platform.Administration.Windows.Tests -c Release -r win-x64 -p:PublishAot=true -p:RunicApplicationBuildMode=Verification -o artifacts/administration/native-tests
& ./artifacts/administration/native-tests/Runic.Platform.Administration.Windows.Tests.exe

dotnet pack packages/dotnet/Runic.Platform.Administration.Windows -c Release -p:RunicApplicationBuildMode=Verification -o artifacts/administration/packages
dotnet run --project examples/dotnet/Runic.Administration.Console -c Release -- system
~~~

`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` record the public API. Native
verification roots the entire administration
assembly so unexecuted public capabilities still receive AOT analysis. That
analysis does not substitute for executing those capabilities on their fixtures.

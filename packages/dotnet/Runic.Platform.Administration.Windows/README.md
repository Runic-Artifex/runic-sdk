# Runic.Platform.Administration.Windows

Host-independent Windows administration for .NET 10. Construct a capability client
directly; no Runic Desktop, Application host, UI framework, dependency injection,
automatic elevation or helper process is required.

This is a local implementation under native validation. **Administrative write and
domain scenarios have not been accepted on disposable fixtures.** See
[verification](docs/verification.md) for the distinction between implementation,
executed checks and remaining acceptance work. Do not infer production readiness
from a successful pack or NativeAOT publish.

## Capabilities and prerequisites

The initial supported architecture is Windows x64. Automation COM bindings reject
other architectures. Other native bindings have not been verified on ARM64/x86.
The intended OS baseline is Windows 10/11 or Windows Server 2016 and later, subject
to the consuming .NET 10 runtime's supported OS versions; this is not a claim that
every server/version combination has been tested.

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
| SystemInformation / WindowsSystemInformationClient | OS version/product/language, BIOS and computer/domain role | Local, normally read-only access; registry, version and SMBIOS APIs. |
| Networks / WindowsNetworkClient | Network identities, categories, connectivity and domain type | Local Network List Manager COM; read-only. |
| Shares / WindowsShareClient | SMB enumeration, configuration, creation/update/deletion and self-relative security descriptors | Local or explicit server; NetShare permissions. Security reads/writes may require elevated rights. Filesystem ACLs are untouched. |
| Dns / WindowsDnsClient | Zones/AD integration; A, AAAA, CNAME, PTR, MX, TXT, SRV and NS record operations | Explicit Windows DNS server; MicrosoftDNS WMI provider, DCOM/WMI access and DNS administration rights. No zone/server configuration writes. |
| GroupPolicy / WindowsGroupPolicyClient | GUID-based GPO operations; GPMC backup/import/restore/report; links/order/inheritance; permissions and WMI-filter associations | Explicit domain/controller, caller identity; GPMC/RSAT installed and delegated AD/GPO/SYSVOL permissions. |

System.DirectoryServices.Protocols 10.0.10 is the only package dependency. Its native
transport initialized and reported an unavailable endpoint from a NativeAOT
consumer. LDAP paging/authentication/write behavior still needs the domain fixture.

## API semantics

- Find returns null only for native absence. Enumeration errors are failures, not
  successful empty lists. Read models use immutable collections; write specifications
  are separate and selected updates use null to preserve a setting.
- WindowsAdministrationException retains operation, error category, native domain
  and numeric code. LDAP exceptions remain available as inner exceptions, including
  native diagnostic context. Do not log request models or credentials.
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
- GetOperatingSystem exposes RegistryProductName verbatim; Windows can retain a Windows 10 compatibility label on Windows 11. GetOperatingSystemNameAsync reads the display name from native Windows inventory.
- Service BinaryCommandLine is the configured command line, not an executable path.
  Process observations are snapshots: a process may disappear after enumeration.
- Task snapshots retain complete native XML; selected updates preserve untouched
  XML subtrees. Replacing actions/triggers intentionally replaces those subtrees.
  Registration, scheduler state and last execution outcome are separate values.
- Firewall names can be ambiguous and have no public persistent GUID in this API.
  Use the returned identity for writes; changed/ambiguous identity fails. Updates
  retain unrelated native settings by editing the existing rule.
- DNS keys include full record data, not just owner name. Updating one record does
  not replace sibling records. Unsupported types are returned as Unsupported with
  native class and text; they cannot be written through the curated typed API.
  TXT data follows the DNS WMI provider's quoted textual representation.
- Share security descriptors retain SID/ACE information and ordering as native
  self-relative bytes. They represent share permissions, never filesystem ACLs.
- GPMC operation outcomes include its detailed status collection and checked overall
  HRESULT. Import overwrites settings in an explicitly selected destination GPO;
  restore uses the backup's original identity. No backup-selection heuristics,
  migration orchestration or SYSVOL file-copy implementation is included.
- Link reordering uses an LDAP assertion against the read gpLink value, preserving
  each entry's flags and rejecting concurrent changes instead of overwriting them.

The read surfaces are curated to the listed capabilities, not arbitrary native
object cloning APIs. Use task XML for task settings outside the typed model.
Selected updates must not be used to recreate an object from a partial snapshot.

## Console examples

[Examples](docs/examples.md) cover each capability. The repository's
examples/dotnet/Runic.Administration.Console is a package-only consumer with
read-only commands. It can be copied outside this repository and restored from a
local package; it has no source-tree or desktop-host references.

## Local package and verification

From the SDK root:

~~~powershell
dotnet run --project tests/dotnet/Runic.Platform.Administration.Windows.ApiTests -c Release -p:RunicToolkitBuildMode=Verification
dotnet run --project tests/dotnet/Runic.Platform.Administration.Windows.Tests -c Release -p:RunicToolkitBuildMode=Verification
dotnet publish tests/dotnet/Runic.Platform.Administration.Windows.Tests -c Release -r win-x64 -p:PublishAot=true -p:RunicToolkitBuildMode=Verification -o artifacts/administration/native-tests
& ./artifacts/administration/native-tests/Runic.Platform.Administration.Windows.Tests.exe

dotnet pack packages/dotnet/Runic.Platform.Administration.Windows -c Release -p:RunicToolkitBuildMode=Verification -p:PackageVersion=0.2.0-administration.local.2 -o artifacts/administration/packages
dotnet restore examples/dotnet/Runic.Administration.Console --source ./artifacts/administration/packages --source https://api.nuget.org/v3/index.json
dotnet run --project examples/dotnet/Runic.Administration.Console --no-restore -- system
~~~

The API baseline records signatures, default parameters, enum values and init
accessors; it is managed-only. Review intentional changes before running its
--write-baseline option. Native verification roots the entire administration
assembly so unexecuted public capabilities still receive AOT analysis. That
analysis does not substitute for executing those capabilities on their fixtures.

Implementation provenance: new SDK implementations against Microsoft Windows SDK
headers and documentation. Application repositories supplied behavioral
requirements; application source/dependencies were not copied or modified.

## Generated Windows bindings

All public capability clients now use CsWin32 for their Win32/COM interop: shell
links, services/processes, tasks, firewall, domain discovery, inventory, networks,
shares, internal WMI (including DNS), and GPMC. LDAP transport remains
`System.DirectoryServices.Protocols`; generated Win32 declarations do not replace
an LDAP transport. Runic's public models, validation and exception contracts remain
handwritten and unchanged by this migration.

`NativeMethods.txt` lists the requested APIs. `NativeMethods.json` selects internal,
unmanaged bindings with preserved HRESULTs. CsWin32 0.3.333 is a private build
dependency. Generated source stays in build output and does not expose Windows
SDK types in the public API. Calls use named methods, native structures and safe
handle ownership; no runtime COM wrappers or warning suppressions were added.

The previous shares/firewall implementations remain internal comparison backends.
The verifier defaults to `--backend cswin32`; `--backend handwritten` selects only
those two comparison implementations. Other capabilities always use generated
bindings (or the LDAP transport). There is no automatic fallback. Shared validation,
security-descriptor interpretation and COM initialization mean parity alone is not
independent proof of Windows semantics.

The GPMC migration also corrects collection `get_Item` outputs to native VARIANTs,
queries returned objects for their expected interface, and passes trustee strings
to `RemoveTrustee`. These paths still require domain fixture execution.

Verification after migration: managed native checks and the 115-type public API
baseline pass. The Windows x64 NativeAOT verifier publishes without warnings and
its local run passed 9 checks, with 5 administrative checks explicitly skipped.
This includes temporary shortcut roundtrips but no machine administration writes.
Earlier VM shares/firewall runs passed for both backends; rerun the full local
mutation suite for this migrated build. GPO/LDAP/DNS fixture operations remain
unaccepted until executed on a disposable domain.

## Applicability to Runic.Platform.Windows

The sibling package was inspected without modifying it. Use the same centrally
pinned generator and private unmanaged bindings there for:

- `WindowsFilePicker`: Common Item Dialog and shell-item interfaces; retain owner
  STA dispatch, cancellation/Close coordination and modal lifetime handling.
- `WindowsFileLauncher`: shell functions, `OPENASINFO` and PIDL ownership.
- `Win32Clipboard`: clipboard/global-memory APIs; retain bounded text decoding and
  the explicit ownership transfer after successful `SetClipboardData`.
- `WindowsDesktopSettings`: `SystemParametersInfoW` and native flags.

Keep generation local to each package instead of adding an administration
reference or exporting a shared low-level binding assembly. Share the dependency
version and conventions, not unrelated capability implementations.

`WindowsDesktopNotifications` and its activation callback implement Windows Runtime
interfaces. CsWin32 may cover their Win32 support functions, but the WinRT object
projection and parameterized event callback need separate evaluation. Microsoft's
[CsWinRT](https://github.com/microsoft/CsWinRT) is the corresponding WinRT projection
project; adopting it would require separate NativeAOT, activation and callback
lifetime checks. Do not bundle that change into a Win32 binding substitution.

using System.Collections.Immutable;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;
using Windows.Win32.Security;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Accounts;

/// <summary>Local group membership on the local or an explicit remote computer, through NetAPI and the current Windows
/// identity.</summary>
/// <remarks>Address a group by <see cref="AccountReference.Sid"/> to stay independent of the UI language. Native calls
/// run off the caller's thread; cancellation before a call starts prevents it, and once a call starts its actual result
/// is returned. Changing membership requires administrative rights on the target computer.</remarks>
public sealed class WindowsLocalGroupClient
{
    private readonly string? _machineName;

    /// <summary>Creates a client; null targets the local computer.</summary>
    public WindowsLocalGroupClient(string? machineName = null)
    {
        NativeError.Windows();
        if (machineName is not null) NativeError.Text(machineName, nameof(machineName));
        _machineName = machineName;
    }

    /// <summary>Adds a member to a local group. Returns false when it already was a member (native 1378), which is not
    /// a failure.</summary>
    /// <remarks>A group or member that does not exist is <see cref="AdministrationErrorCategory.NotFound"/>.</remarks>
    public Task<bool> AddMemberAsync(AccountReference group, AccountReference member, CancellationToken cancellationToken = default)
    {
        Validate(group, nameof(group));
        Validate(member, nameof(member));
        return NativeWork.RunAsync(() => Change(group, member, add: true), cancellationToken);
    }

    /// <summary>Removes a member from a local group. Returns false when it was not a member (native 1377), which is not
    /// a failure.</summary>
    public Task<bool> RemoveMemberAsync(AccountReference group, AccountReference member, CancellationToken cancellationToken = default)
    {
        Validate(group, nameof(group));
        Validate(member, nameof(member));
        return NativeWork.RunAsync(() => Change(group, member, add: false), cancellationToken);
    }

    /// <summary>Enumerates the direct members of a local group. A missing group is
    /// <see cref="AdministrationErrorCategory.NotFound"/>, never an empty list.</summary>
    public Task<ImmutableArray<LocalGroupMember>> EnumerateMembersAsync(AccountReference group, CancellationToken cancellationToken = default)
    {
        Validate(group, nameof(group));
        return NativeWork.RunAsync(() => Members(group), cancellationToken);
    }

    /// <summary>Reads the name of a local group on the target computer, such as "Administratoren" for
    /// <c>S-1-5-32-544</c> on a German Windows.</summary>
    public Task<string> GetGroupNameAsync(AccountReference group, CancellationToken cancellationToken = default)
    {
        Validate(group, nameof(group));
        return NativeWork.RunAsync(() => GroupName(group), cancellationToken);
    }

    internal static void Validate(AccountReference reference, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(reference, parameterName);
        switch (reference)
        {
            case AccountReference.Name name: NativeError.Text(name.Value, parameterName); break;
            case AccountReference.Sid sid: ArgumentNullException.ThrowIfNull(sid.Value, parameterName); break;
            default: throw new ArgumentException("Unsupported account reference.", parameterName);
        }
    }

    private string GroupName(AccountReference group) => group switch
    {
        AccountReference.Name name => name.Value,
        AccountReference.Sid sid => Lookup(sid.Value).Name,
        _ => throw new ArgumentException("Unsupported account reference.", nameof(group))
    };

    private unsafe (string Name, string Domain) Lookup(SecurityIdentifier sid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var binary = new byte[sid.BinaryLength];
        sid.GetBinaryForm(binary, 0);
        uint nameLength = 256, domainLength = 256;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var name = new char[nameLength];
            var domain = new char[domainLength];
            fixed (byte* pointer = binary)
            {
                if (PInvoke.LookupAccountSid(_machineName, new PSID(pointer), name, ref nameLength, domain, ref domainLength, out _))
                    return (new string(name, 0, (int)nameLength), new string(domain, 0, (int)domainLength));
            }
            var error = Marshal.GetLastPInvokeError();
            if (error != 122) throw NativeError.Win32("Resolve account SID", error);
        }
        throw NativeError.Win32("Resolve account SID", 122);
    }

    private unsafe bool Change(AccountReference group, AccountReference member, bool add)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var groupName = GroupName(group);
        uint status;
        switch (member)
        {
            case AccountReference.Sid sid:
            {
                var binary = new byte[sid.Value.BinaryLength];
                sid.Value.GetBinaryForm(binary, 0);
                fixed (byte* pointer = binary)
                {
                    var info = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = new PSID(pointer) };
                    var buffer = new Span<byte>(&info, sizeof(LOCALGROUP_MEMBERS_INFO_0));
                    status = add ? PInvoke.NetLocalGroupAddMembers(_machineName, groupName, 0, buffer, 1)
                        : PInvoke.NetLocalGroupDelMembers(_machineName, groupName, 0, buffer, 1);
                }
                break;
            }
            case AccountReference.Name name:
            {
                fixed (char* text = name.Value)
                {
                    var info = new LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = new PWSTR(text) };
                    var buffer = new Span<byte>(&info, sizeof(LOCALGROUP_MEMBERS_INFO_3));
                    status = add ? PInvoke.NetLocalGroupAddMembers(_machineName, groupName, 3, buffer, 1)
                        : PInvoke.NetLocalGroupDelMembers(_machineName, groupName, 3, buffer, 1);
                }
                break;
            }
            default: throw new ArgumentException("Unsupported account reference.", nameof(member));
        }
        return MembershipResult(status, add);
    }

    /// <summary>Maps a NetLocalGroupAddMembers/DelMembers status: ERROR_MEMBER_IN_ALIAS (1378) on add and
    /// ERROR_MEMBER_NOT_IN_ALIAS (1377) on removal are results, not failures.</summary>
    internal static bool MembershipResult(uint status, bool add) => (status, add) switch
    {
        (0, _) => true,
        (1378, true) or (1377, false) => false,
        _ => throw NativeError.Win32(add ? "Add local group member" : "Remove local group member", unchecked((int)status))
    };

    private unsafe ImmutableArray<LocalGroupMember> Members(AccountReference group)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var groupName = GroupName(group);
        var rows = ImmutableArray.CreateBuilder<LocalGroupMember>();
        nuint resume = 0;
        uint status;
        do
        {
            // MAX_PREFERRED_LENGTH: NetAPI allocates what the members need; ERROR_MORE_DATA (234) still pages.
            status = PInvoke.NetLocalGroupGetMembers(_machineName, groupName, 2, out var buffer, uint.MaxValue, out var count, out _, ref resume);
            try
            {
                if (status is not (0 or 234)) throw NativeError.Win32("Enumerate local group members", unchecked((int)status));
                var items = (LOCALGROUP_MEMBERS_INFO_2*)buffer;
                for (var i = 0u; i < count; i++)
                {
                    if (items[i].lgrmi2_sid.Value == null) throw NativeError.Win32("Read local group member", 13);
                    rows.Add(new(new SecurityIdentifier((nint)items[i].lgrmi2_sid.Value), items[i].lgrmi2_domainandname.ToString() ?? "",
                        (AccountKind)items[i].lgrmi2_sidusage));
                }
                if (status == 234 && count == 0) throw NativeError.Win32("Read local group member page", 13);
            }
            finally { if (buffer != null) _ = PInvoke.NetApiBufferFree(buffer); }
        } while (status == 234);
        return rows.ToImmutable();
    }
}

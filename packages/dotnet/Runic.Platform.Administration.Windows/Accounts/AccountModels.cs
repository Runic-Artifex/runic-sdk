using System.Security.Principal;

namespace Runic.Platform.Administration.Windows.Accounts;

/// <summary>A Windows account or group, named or identified by its security identifier.</summary>
/// <remarks>A SID is independent of the UI language: <c>S-1-5-32-544</c> is the local Administrators group whether
/// Windows calls it "Administrators" or "Administratoren". Build well-known SIDs with
/// <see cref="SecurityIdentifier(WellKnownSidType, SecurityIdentifier?)"/>.</remarks>
public abstract record AccountReference
{
    private AccountReference() { }
    /// <summary>An account by name. A group member is <c>DOMAIN\name</c> (or a bare local name); a local group is its
    /// name on the target computer, without a computer or <c>BUILTIN</c> prefix.</summary>
    public sealed record Name(string Value) : AccountReference;
    /// <summary>An account by security identifier, resolved on the target computer.</summary>
    public sealed record Sid(SecurityIdentifier Value) : AccountReference;
}

/// <summary>The kind of account a security identifier names (the native <c>SID_NAME_USE</c>).</summary>
/// <remarks>This set is open: values without a name here are passed through unchanged.</remarks>
public enum AccountKind
{
    /// <summary>A user account.</summary>
    User = 1,
    /// <summary>A domain group.</summary>
    Group = 2,
    /// <summary>A domain.</summary>
    Domain = 3,
    /// <summary>A local group (alias), such as <c>BUILTIN\Administrators</c>.</summary>
    Alias = 4,
    /// <summary>A well-known group, such as <c>Everyone</c> or <c>NT AUTHORITY\INTERACTIVE</c>.</summary>
    WellKnownGroup = 5,
    /// <summary>A deleted account.</summary>
    DeletedAccount = 6,
    /// <summary>An invalid SID.</summary>
    Invalid = 7,
    /// <summary>A SID of unknown kind, such as one that could not be resolved.</summary>
    Unknown = 8,
    /// <summary>A computer account.</summary>
    Computer = 9,
    /// <summary>A mandatory integrity label.</summary>
    Label = 10,
    /// <summary>A logon session.</summary>
    LogonSession = 11
}

/// <summary>A member of a local group.</summary>
/// <param name="Sid">The member's security identifier.</param>
/// <param name="Name">The member as <c>DOMAIN\name</c>; empty when Windows could not resolve the SID, for example for a
/// deleted account or an unreachable domain.</param>
/// <param name="Kind">The kind of account.</param>
public sealed record LocalGroupMember(SecurityIdentifier Sid, string Name, AccountKind Kind);

using System.Collections.Immutable;

namespace Runic.Platform.Administration.Windows.DirectoryServices;

/// <summary>Transport protection for LDAP. Certificate validation is never bypassed.</summary>
public enum DirectoryTransport
{
    /// <summary>Negotiate authentication with LDAP signing and sealing.</summary>
    SignedAndSealed,
    /// <summary>TLS from connection establishment.</summary>
    Tls,
    /// <summary>Upgrade LDAP to TLS before binding.</summary>
    StartTls
}
/// <summary>LDAP authentication mechanism.</summary>
public enum DirectoryAuthentication
{
    /// <summary>Windows integrated authentication.</summary>
    Negotiate,
    /// <summary>Basic bind, permitted only over TLS and with explicit credentials.</summary>
    Basic
}
/// <summary>Explicit directory endpoint and bounded connection behavior. Credentials are supplied separately.</summary>
public sealed record DirectoryConnectionOptions(string Server)
{
    /// <summary>Port; zero selects 636 for TLS or 389 otherwise.</summary>
    public int Port { get; init; }
    /// <summary>Connection protection.</summary>
    public DirectoryTransport Transport { get; init; } = DirectoryTransport.SignedAndSealed;
    /// <summary>Authentication mode.</summary>
    public DirectoryAuthentication Authentication { get; init; } = DirectoryAuthentication.Negotiate;
    /// <summary>Native request timeout, between one second and five minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
/// <summary>Scope of an LDAP search.</summary>
public enum DirectorySearchScope
{
    /// <summary>The specified object.</summary>
    Base,
    /// <summary>Direct children.</summary>
    OneLevel,
    /// <summary>The complete subtree.</summary>
    Subtree
}
/// <summary>A typed LDAP attribute value.</summary>
public abstract record DirectoryValue
{
    private DirectoryValue() { }
    /// <summary>A directory text value.</summary>
    public sealed record Text(string Value) : DirectoryValue;
    /// <summary>A directory binary value, including GUIDs and SIDs.</summary>
    public sealed record Binary(ImmutableArray<byte> Value) : DirectoryValue;
}
/// <summary>An immutable directory object; attribute names compare without case.</summary>
public sealed record DirectoryObject(string DistinguishedName, ImmutableDictionary<string, ImmutableArray<DirectoryValue>> Attributes);
/// <summary>A paged search specification with explicit requested attributes.</summary>
public sealed record DirectorySearch(string BaseDistinguishedName, string Filter, DirectorySearchScope Scope, ImmutableArray<string> Attributes)
{
    /// <summary>Server page size.</summary>
    public int PageSize { get; init; } = 500;
}
/// <summary>An LDAP attribute modification operation.</summary>
public enum DirectoryModificationKind
{
    /// <summary>Add the supplied values.</summary>
    Add,
    /// <summary>Delete supplied values; an empty list deletes the attribute.</summary>
    Delete,
    /// <summary>Replace all values; an empty list removes the attribute.</summary>
    Replace
}
/// <summary>An explicit modification retaining LDAP multi-value semantics.</summary>
public sealed record DirectoryModification(string AttributeName, DirectoryModificationKind Kind, ImmutableArray<DirectoryValue> Values);

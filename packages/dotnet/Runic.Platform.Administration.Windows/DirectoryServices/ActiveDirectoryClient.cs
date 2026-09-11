using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.DirectoryServices;

/// <summary>AD group scope.</summary>
public enum ActiveDirectoryGroupScope
{
    /// <summary>Global group.</summary>
    Global = 2,
    /// <summary>Domain-local group.</summary>
    DomainLocal = 4,
    /// <summary>Universal group.</summary>
    Universal = 8
}

/// <summary>Curated Active Directory operations built on explicit, protected LDAP access.</summary>
public sealed class ActiveDirectoryClient
{
    private readonly WindowsDirectoryClient _directory;

    /// <summary>Creates a client using a configured directory transport.</summary>
    public ActiveDirectoryClient(WindowsDirectoryClient directory) =>
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>Creates a disabled user. Assign a password and enable it explicitly afterward.</summary>
    public Task CreateUserAsync(string parentDistinguishedName, string commonName, string accountName, string? userPrincipalName = null,
        CancellationToken cancellationToken = default)
    {
        NativeError.Text(accountName, nameof(accountName));
        var attributes = Attributes(("objectClass", "user"), ("cn", commonName), ("sAMAccountName", accountName), ("userAccountControl", "514"));
        if (userPrincipalName is not null) attributes = attributes.Add("userPrincipalName", [new DirectoryValue.Text(userPrincipalName)]);
        return _directory.CreateAsync(Dn(parentDistinguishedName, "CN", commonName), attributes, cancellationToken);
    }

    /// <summary>Creates a disabled workstation computer account. accountName must include its trailing dollar sign.</summary>
    public Task CreateComputerAsync(string parentDistinguishedName, string commonName, string accountName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(accountName, nameof(accountName));
        if (!accountName.EndsWith('$')) throw new ArgumentException("Computer account names must end in '$'.", nameof(accountName));
        return _directory.CreateAsync(Dn(parentDistinguishedName, "CN", commonName),
            Attributes(("objectClass", "computer"), ("cn", commonName), ("sAMAccountName", accountName), ("userAccountControl", "4098")), cancellationToken);
    }

    /// <summary>Creates a group with explicit scope and security/distribution semantics.</summary>
    public Task CreateGroupAsync(string parentDistinguishedName, string commonName, string accountName,
        ActiveDirectoryGroupScope scope, bool securityEnabled, CancellationToken cancellationToken = default)
    {
        NativeError.Text(accountName, nameof(accountName));
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        var groupType = (int)scope | (securityEnabled ? int.MinValue : 0);
        return _directory.CreateAsync(Dn(parentDistinguishedName, "CN", commonName),
            Attributes(("objectClass", "group"), ("cn", commonName), ("sAMAccountName", accountName),
                ("groupType", groupType.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    /// <summary>Creates an organizational unit with an escaped relative name.</summary>
    public Task CreateOrganizationalUnitAsync(string parentDistinguishedName, string name, CancellationToken cancellationToken = default) =>
        _directory.CreateAsync(Dn(parentDistinguishedName, "OU", name), Attributes(("objectClass", "organizationalUnit"), ("ou", name)), cancellationToken);

    /// <summary>Retrieves all member DNs, including ranged attributes on large groups.</summary>
    public Task<ImmutableArray<DirectoryValue>> GetMembersAsync(string groupDistinguishedName, CancellationToken cancellationToken = default) =>
        _directory.ReadAllAttributeValuesAsync(groupDistinguishedName, "member", cancellationToken);

    /// <summary>Adds a group member. An existing value is reported by LDAP as a conflict.</summary>
    public Task AddMemberAsync(string groupDistinguishedName, string memberDistinguishedName, CancellationToken cancellationToken = default) =>
        _directory.ModifyAsync(groupDistinguishedName, [new("member", DirectoryModificationKind.Add, [new DirectoryValue.Text(memberDistinguishedName)])], cancellationToken);

    /// <summary>Removes a group member. A missing value remains distinguishable as an LDAP error.</summary>
    public Task RemoveMemberAsync(string groupDistinguishedName, string memberDistinguishedName, CancellationToken cancellationToken = default) =>
        _directory.ModifyAsync(groupDistinguishedName, [new("member", DirectoryModificationKind.Delete, [new DirectoryValue.Text(memberDistinguishedName)])], cancellationToken);

    /// <summary>Enables or disables an account while retaining other control bits and asserting that the observed value has not changed.</summary>
    public async Task SetAccountEnabledAsync(string distinguishedName, bool enabled, CancellationToken cancellationToken = default)
    {
        var account = await _directory.FindAsync(distinguishedName, ["userAccountControl"], cancellationToken).ConfigureAwait(false)
            ?? throw NativeError.Win32("Read account control", 2);
        if (!account.Attributes.TryGetValue("userAccountControl", out var values) || values.Length != 1)
            throw NativeError.Win32("Read account control", 13);
        var text = ValueText(values[0]);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oldValue))
            throw NativeError.Win32("Read account control", 13);
        var newValue = enabled ? oldValue & ~2 : oldValue | 2;
        if (newValue == oldValue) return;
        await _directory.CompareAndReplaceTextAsync(distinguishedName, "userAccountControl", text,
            newValue.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets an account password. Requires reset rights; the configured LDAP connection always uses TLS or signing/sealing.</summary>
    public Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken = default) =>
        _directory.ModifyAsync(distinguishedName, [new("unicodePwd", DirectoryModificationKind.Replace, [Password(newPassword)])], cancellationToken);

    /// <summary>Changes a password through one LDAP delete/add request, preserving the user's change-password authorization semantics.</summary>
    public Task ChangePasswordAsync(string distinguishedName, string oldPassword, string newPassword, CancellationToken cancellationToken = default) =>
        _directory.ModifyAsync(distinguishedName, [
            new("unicodePwd", DirectoryModificationKind.Delete, [Password(oldPassword)]),
            new("unicodePwd", DirectoryModificationKind.Add, [Password(newPassword)])
        ], cancellationToken);

    /// <summary>Reads all service principal names.</summary>
    public Task<ImmutableArray<DirectoryValue>> GetServicePrincipalNamesAsync(string distinguishedName, CancellationToken cancellationToken = default) =>
        _directory.ReadAllAttributeValuesAsync(distinguishedName, "servicePrincipalName", cancellationToken);

    /// <summary>Adds explicit SPNs; does not generate names or choose an account.</summary>
    public Task AddServicePrincipalNamesAsync(string distinguishedName, ImmutableArray<string> names, CancellationToken cancellationToken = default) =>
        _directory.ModifyAsync(distinguishedName, [new("servicePrincipalName", DirectoryModificationKind.Add, TextValues(names))], cancellationToken);

    /// <summary>Removes only explicitly supplied SPNs.</summary>
    public Task RemoveServicePrincipalNamesAsync(string distinguishedName, ImmutableArray<string> names, CancellationToken cancellationToken = default) =>
        _directory.ModifyAsync(distinguishedName, [new("servicePrincipalName", DirectoryModificationKind.Delete, TextValues(names))], cancellationToken);

    private static ImmutableArray<DirectoryValue> TextValues(ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty) throw new ArgumentException("Supply at least one value.", nameof(values));
        foreach (var value in values) NativeError.Text(value, nameof(values));
        return [.. values.Select(value => (DirectoryValue)new DirectoryValue.Text(value))];
    }

    private static DirectoryValue.Binary Password(string password)
    {
        NativeError.Text(password, nameof(password));
        return new([.. Encoding.Unicode.GetBytes('"' + password + '"')]);
    }

    private static string ValueText(DirectoryValue value) => value switch
    {
        DirectoryValue.Text text => text.Value,
        DirectoryValue.Binary binary => new UTF8Encoding(false, true).GetString(binary.Value.AsSpan()),
        _ => throw NativeError.Win32("Read directory text value", 13)
    };

    private static string Dn(string parent, string type, string name)
    {
        NativeError.Text(parent, nameof(parent));
        NativeError.Text(name, nameof(name));
        return type + "=" + DirectoryNames.EscapeRdnValue(name) + "," + parent;
    }

    private static ImmutableDictionary<string, ImmutableArray<DirectoryValue>> Attributes(params (string Name, string Value)[] values) =>
        values.ToImmutableDictionary(pair => pair.Name, pair => ImmutableArray.Create<DirectoryValue>(new DirectoryValue.Text(pair.Value)), StringComparer.OrdinalIgnoreCase);
}

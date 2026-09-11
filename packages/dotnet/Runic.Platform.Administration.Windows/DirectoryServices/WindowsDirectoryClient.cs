using System.Collections.Immutable;
using System.DirectoryServices.Protocols;
using System.Net;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.DirectoryServices;

/// <summary>Explicit LDAP operations with typed values and no ADSI runtime COM dependency.</summary>
/// <remarks>Each operation owns its connection. Cross-operation transactions and automatic retries are not provided.</remarks>
public sealed class WindowsDirectoryClient
{
    private readonly DirectoryConnectionOptions _options;
    private readonly NetworkCredential? _credential;

    /// <summary>Creates a directory client. Explicit credentials are retained privately and never included in diagnostic models.</summary>
    public WindowsDirectoryClient(DirectoryConnectionOptions options, NetworkCredential? credential = null)
    {
        NativeError.Windows();
        ArgumentNullException.ThrowIfNull(options);
        NativeError.Text(options.Server, nameof(options.Server));
        if (options.Port is < 0 or > 65535 || options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromMinutes(5) ||
            !Enum.IsDefined(options.Transport) || !Enum.IsDefined(options.Authentication))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Authentication == DirectoryAuthentication.Basic &&
            (credential is null || options.Transport == DirectoryTransport.SignedAndSealed))
            throw new ArgumentException("Basic LDAP authentication requires explicit credentials and TLS.", nameof(options));
        _options = options;
        _credential = credential is null ? null : new(credential.UserName, credential.Password, credential.Domain);
    }

    private LdapConnection Connect()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_options.Server,
            _options.Port == 0 ? (_options.Transport == DirectoryTransport.Tls ? 636 : 389) : _options.Port))
        {
            AuthType = _options.Authentication == DirectoryAuthentication.Negotiate ? AuthType.Negotiate : AuthType.Basic,
            Timeout = _options.Timeout
        };
        try
        {
            if (_credential is not null) connection.Credential = _credential;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            if (_options.Transport == DirectoryTransport.Tls) connection.SessionOptions.SecureSocketLayer = true;
            else if (_options.Transport == DirectoryTransport.StartTls) connection.SessionOptions.StartTransportLayerSecurity(null);
            else { connection.SessionOptions.Signing = true; connection.SessionOptions.Sealing = true; }
            connection.Bind();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    /// <summary>Reads selected RootDSE attributes from the configured server.</summary>
    public Task<DirectoryObject?> ReadRootDseAsync(ImmutableArray<string> attributes, CancellationToken cancellationToken = default) =>
        FindAsync("", attributes, cancellationToken);

    /// <summary>Reads an object by distinguished name; null means no such object.</summary>
    public Task<DirectoryObject?> FindAsync(string distinguishedName, ImmutableArray<string> attributes, CancellationToken cancellationToken = default)
    {
        ValidateAttributes(attributes);
        NativeError.Text(distinguishedName, nameof(distinguishedName), true);
        return Execute(connection =>
        {
            try
            {
                var response = (SearchResponse)connection.SendRequest(new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base, attributes.ToArray()));
                if (response.Entries.Count == 0) return null;
                return ReadObject(response.Entries[0]);
            }
            catch (DirectoryOperationException error) when (error.Response?.ResultCode == ResultCode.NoSuchObject) { return null; }
        }, "Find directory object", cancellationToken);
    }

    /// <summary>Performs a paged search. Cancellation is checked between bounded native requests; server failures never return partial success.</summary>
    public Task<ImmutableArray<DirectoryObject>> SearchAsync(DirectorySearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        NativeError.Text(search.BaseDistinguishedName, nameof(search.BaseDistinguishedName), true);
        NativeError.Text(search.Filter, nameof(search.Filter));
        ValidateAttributes(search.Attributes);
        if (!Enum.IsDefined(search.Scope) || search.PageSize is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(search));
        return Execute(connection =>
        {
            var result = ImmutableArray.CreateBuilder<DirectoryObject>();
            var request = new SearchRequest(search.BaseDistinguishedName, search.Filter, (SearchScope)search.Scope, search.Attributes.ToArray());
            var page = new PageResultRequestControl(search.PageSize);
            request.Controls.Add(page);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (SearchResponse)connection.SendRequest(request);
                foreach (SearchResultEntry entry in response.Entries) result.Add(ReadObject(entry));
                var responsePage = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault();
                if (responsePage is null) throw NativeError.Win32("Read LDAP paging response", 13);
                page.Cookie = responsePage.Cookie;
            } while (page.Cookie.Length != 0);
            return result.ToImmutable();
        }, "Search directory", cancellationToken);
    }

    /// <summary>Retrieves every range of a large multi-valued AD attribute without confusing it with LDAP search paging.</summary>
    public Task<ImmutableArray<DirectoryValue>> ReadAllAttributeValuesAsync(string distinguishedName, string attributeName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(distinguishedName, nameof(distinguishedName));
        DirectoryNames.Attribute(attributeName);
        if (attributeName.Contains(';')) throw new ArgumentException("Supply the base attribute name without range options.", nameof(attributeName));
        return Execute(connection =>
        {
            var result = ImmutableArray.CreateBuilder<DirectoryValue>();
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (SearchResponse)connection.SendRequest(new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base,
                    $"{attributeName};range={offset}-*"));
                if (response.Entries.Count == 0) throw NativeError.Win32("Read ranged directory attribute", 2);
                var attributes = ReadObject(response.Entries[0]).Attributes;
                if (attributes.TryGetValue(attributeName, out var complete)) { if (offset != 0) throw NativeError.Win32("Directory attribute changed during range retrieval", 183); result.AddRange(complete); break; }
                var range = attributes.FirstOrDefault(pair => pair.Key.StartsWith(attributeName + ";range=", StringComparison.OrdinalIgnoreCase));
                if (range.Key is null) { if (offset != 0) throw NativeError.Win32("Directory attribute disappeared during range retrieval", 183); break; }
                result.AddRange(range.Value);
                var parts = range.Key[(attributeName.Length + 7)..].Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var start) || start != offset) throw NativeError.Win32("Read ranged directory attribute", 13);
                if (parts[1] == "*") break;
                if (!int.TryParse(parts[1], out var end) || end < offset) throw NativeError.Win32("Read ranged directory attribute", 13);
                offset = checked(end + 1);
            }
            return result.ToImmutable();
        }, "Read ranged directory attribute", cancellationToken);
    }

    /// <summary>Creates an object. objectClass and all required schema attributes must be supplied explicitly.</summary>
    public Task CreateAsync(string distinguishedName, ImmutableDictionary<string, ImmutableArray<DirectoryValue>> attributes, CancellationToken cancellationToken = default)
    {
        NativeError.Text(distinguishedName, nameof(distinguishedName));
        ArgumentNullException.ThrowIfNull(attributes);
        var request = new AddRequest(distinguishedName);
        foreach (var pair in attributes)
        {
            DirectoryNames.Attribute(pair.Key);
            var attribute = new DirectoryAttribute { Name = pair.Key };
            AddValues(attribute, pair.Value);
            request.Attributes.Add(attribute);
        }
        return Send(request, "Create directory object", cancellationToken);
    }

    /// <summary>Applies explicit LDAP modifications in one server request.</summary>
    public Task ModifyAsync(string distinguishedName, ImmutableArray<DirectoryModification> modifications, CancellationToken cancellationToken = default)
    {
        NativeError.Text(distinguishedName, nameof(distinguishedName));
        if (modifications.IsDefaultOrEmpty) throw new ArgumentException("At least one modification is required.", nameof(modifications));
        var request = new ModifyRequest(distinguishedName);
        foreach (var modification in modifications)
        {
            ArgumentNullException.ThrowIfNull(modification);
            DirectoryNames.Attribute(modification.AttributeName);
            if (!Enum.IsDefined(modification.Kind)) throw new ArgumentOutOfRangeException(nameof(modifications));
            var item = new DirectoryAttributeModification { Name = modification.AttributeName, Operation = (DirectoryAttributeOperation)modification.Kind };
            AddValues(item, modification.Values);
            request.Modifications.Add(item);
        }
        return Send(request, "Modify directory object", cancellationToken);
    }

    /// <summary>Deletes a single object without recursive deletion. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(distinguishedName, nameof(distinguishedName));
        return Execute(connection =>
        {
            try { _ = connection.SendRequest(new DeleteRequest(distinguishedName)); return true; }
            catch (DirectoryOperationException error) when (error.Response?.ResultCode == ResultCode.NoSuchObject) { return false; }
        }, "Delete directory object", cancellationToken);
    }

    /// <summary>Renames or moves an object. newRdn must be a complete escaped relative distinguished name.</summary>
    public Task MoveAsync(string distinguishedName, string newParentDistinguishedName, string newRdn, CancellationToken cancellationToken = default)
    {
        NativeError.Text(distinguishedName, nameof(distinguishedName));
        NativeError.Text(newParentDistinguishedName, nameof(newParentDistinguishedName));
        NativeError.Text(newRdn, nameof(newRdn));
        return Send(new ModifyDNRequest(distinguishedName, newParentDistinguishedName, newRdn) { DeleteOldRdn = true }, "Move directory object", cancellationToken);
    }


    internal Task CompareAndReplaceTextAsync(string distinguishedName, string attributeName, string expected, string replacement, CancellationToken cancellationToken)
    {
        var modification = new DirectoryAttributeModification { Name = attributeName, Operation = DirectoryAttributeOperation.Replace };
        modification.Add(replacement);
        var request = new ModifyRequest(distinguishedName, modification);
        // RFC 4528 assertion: equalityMatch [3] AttributeValueAssertion.
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.BER);
        var tag = new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 3, true);
        writer.PushSequence(tag);
        writer.WriteOctetString(System.Text.Encoding.UTF8.GetBytes(attributeName));
        writer.WriteOctetString(System.Text.Encoding.UTF8.GetBytes(expected));
        writer.PopSequence(tag);
        request.Controls.Add(new DirectoryControl("1.3.6.1.1.12", writer.Encode(), true, true));
        return Send(request, "Conditionally update directory attribute", cancellationToken);
    }

    private Task<bool> Send(DirectoryRequest request, string operation, CancellationToken cancellationToken) =>
        Execute(connection => { _ = connection.SendRequest(request); return true; }, operation, cancellationToken);

    private Task<T> Execute<T>(Func<LdapConnection, T> action, string operation, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var connection = Connect();
                cancellationToken.ThrowIfCancellationRequested();
                return action(connection);
            }
            catch (DirectoryOperationException error)
            {
                var code = (int)(error.Response?.ResultCode ?? ResultCode.Other);
                throw Failure(operation, code, error);
            }
            catch (LdapException error) { throw Failure(operation, error.ErrorCode, error); }
        }, cancellationToken);

    private static WindowsAdministrationException Failure(string operation, int code, Exception error) =>
        new(operation, code switch
        {
            49 or 50 => AdministrationErrorCategory.AccessDenied,
            32 or 51 or 52 or 81 or 85 or 91 => AdministrationErrorCategory.Unavailable,
            20 or 68 or 122 => AdministrationErrorCategory.Conflict,
            17 or 18 or 19 or 21 or 34 or 65 => AdministrationErrorCategory.InvalidData,
            _ => AdministrationErrorCategory.NativeFailure
        }, NativeErrorDomain.Ldap, code, $"{operation} failed (LDAP: {code}).", error);

    private static void ValidateAttributes(ImmutableArray<string> attributes)
    {
        if (attributes.IsDefaultOrEmpty) throw new ArgumentException("Request explicit LDAP attributes.", nameof(attributes));
        foreach (var attribute in attributes) DirectoryNames.Attribute(attribute);
    }

    private static void AddValues(DirectoryAttribute attribute, ImmutableArray<DirectoryValue> values)
    {
        if (values.IsDefault) throw new ArgumentException("Attribute values must be initialized.", nameof(values));
        foreach (var value in values)
            switch (value)
            {
                case DirectoryValue.Text text: NativeError.Text(text.Value, nameof(values), true); attribute.Add(text.Value); break;
                case DirectoryValue.Binary binary when !binary.Value.IsDefault: attribute.Add(binary.Value.ToArray()); break;
                default: throw new ArgumentException("Unsupported directory value.", nameof(values));
            }
    }

    private static DirectoryObject ReadObject(SearchResultEntry entry)
    {
        var attributes = ImmutableDictionary.CreateBuilder<string, ImmutableArray<DirectoryValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entry.Attributes.AttributeNames)
        {
            var values = ImmutableArray.CreateBuilder<DirectoryValue>();
            foreach (var value in entry.Attributes[name])
                values.Add(value switch
                {
                    string text => new DirectoryValue.Text(text),
                    byte[] bytes => new DirectoryValue.Binary([.. bytes]),
                    _ => throw NativeError.Win32("Read LDAP attribute type", 13)
                });
            attributes.Add(name, values.ToImmutable());
        }
        return new(entry.DistinguishedName, attributes.ToImmutable());
    }
}

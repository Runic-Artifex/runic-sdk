using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.DirectoryServices;

/// <summary>Explicit LDAP operations with typed values and no ADSI runtime COM dependency.</summary>
/// <remarks>Each operation owns its connection. Cross-operation transactions and automatic retries are not provided.
/// Read values are typed by the attribute's schema syntax: string syntaxes are <see cref="DirectoryValue.Text"/>;
/// octet-string, SID and security-descriptor syntaxes, and attributes the schema does not define, are
/// <see cref="DirectoryValue.Binary"/>. RootDSE values are text. The client caches the syntaxes it has read.</remarks>
public sealed class WindowsDirectoryClient
{
    private readonly DirectoryConnectionOptions _options;
    private readonly NetworkCredential? _credential;
    // lDAPDisplayName -> true when values are binary. Filled from the schema on first use of each attribute.
    private readonly ConcurrentDictionary<string, bool> _binaryAttributes = new(StringComparer.OrdinalIgnoreCase);
    private string? _schemaNamingContext;

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
    /// <remarks>Every RootDSE value is returned as <see cref="DirectoryValue.Text"/> without a schema lookup: the schema's
    /// location is itself a RootDSE attribute, and Active Directory defines RootDSE values as strings.</remarks>
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
                if (distinguishedName.Length == 0) return ReadObject(response.Entries[0], static _ => false);
                ResolveSyntaxes(connection, response.Entries.Cast<SearchResultEntry>());
                return ReadObject(response.Entries[0], IsBinary);
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
                ResolveSyntaxes(connection, response.Entries.Cast<SearchResultEntry>());
                foreach (SearchResultEntry entry in response.Entries) result.Add(ReadObject(entry, IsBinary));
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
                ResolveSyntaxes(connection, response.Entries.Cast<SearchResultEntry>());
                var attributes = ReadObject(response.Entries[0], IsBinary).Attributes;
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
        NativeError.Text(expected, nameof(expected));
        NativeError.Text(replacement, nameof(replacement));
        // Active Directory does not support the RFC 4528 assertion control. Deleting the exact observed value and adding
        // the replacement in one request is atomic: the server rejects the whole request if the stored value changed.
        var delete = new DirectoryAttributeModification { Name = attributeName, Operation = DirectoryAttributeOperation.Delete };
        delete.Add(expected);
        var add = new DirectoryAttributeModification { Name = attributeName, Operation = DirectoryAttributeOperation.Add };
        add.Add(replacement);
        var request = new ModifyRequest(distinguishedName, delete, add);
        const string operation = "Conditionally update directory attribute";
        return Execute(connection =>
        {
            try { _ = connection.SendRequest(request); return true; }
            catch (DirectoryOperationException error) when (error.Response?.ResultCode == ResultCode.NoSuchAttribute)
            {
                throw new WindowsAdministrationException(operation, AdministrationErrorCategory.Conflict, NativeErrorDomain.Ldap,
                    (int)ResultCode.NoSuchAttribute, $"{operation} failed: the observed value changed (LDAP: 16).", error);
            }
        }, operation, cancellationToken);
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
        new(operation, Categorize(code), NativeErrorDomain.Ldap, code, $"{operation} failed (LDAP: {code}).", error);

    internal static AdministrationErrorCategory Categorize(int ldapResult) => ldapResult switch
    {
        49 or 50 => AdministrationErrorCategory.AccessDenied,
        // noSuchObject: the named entry does not exist.
        32 => AdministrationErrorCategory.NotFound,
        51 or 52 or 81 or 85 or 91 => AdministrationErrorCategory.Unavailable,
        20 or 68 or 122 => AdministrationErrorCategory.Conflict,
        17 or 18 or 19 or 21 or 34 or 65 => AdministrationErrorCategory.InvalidData,
        _ => AdministrationErrorCategory.NativeFailure
    };

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

    private bool IsBinary(string attributeName) => _binaryAttributes.TryGetValue(attributeName, out var binary) ? binary : true;

    // Reads attributeSyntax and oMSyntax for every returned attribute that is not cached yet. An attribute the
    // schema does not define (or a server without a schema naming context) is treated as binary.
    private void ResolveSyntaxes(LdapConnection connection, IEnumerable<SearchResultEntry> entries)
    {
        var missing = entries.SelectMany(entry => entry.Attributes.AttributeNames.Cast<string>()).Select(BaseAttributeName)
            .Where(name => !_binaryAttributes.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length == 0) return;
        var schema = _schemaNamingContext ??= ReadSchemaNamingContext(connection);
        foreach (var chunk in missing.Chunk(64))
        {
            if (schema.Length != 0)
            {
                var filter = "(&(objectClass=attributeSchema)(|" + string.Concat(chunk.Select(name => "(lDAPDisplayName=" + DirectoryNames.EscapeFilterValue(name) + ")")) + "))";
                var response = (SearchResponse)connection.SendRequest(new SearchRequest(schema, filter, SearchScope.OneLevel, "lDAPDisplayName", "attributeSyntax", "oMSyntax"));
                foreach (SearchResultEntry entry in response.Entries)
                {
                    var name = SchemaText(entry, "lDAPDisplayName");
                    var syntax = SchemaText(entry, "attributeSyntax");
                    if (name is null || syntax is null) continue;
                    _ = int.TryParse(SchemaText(entry, "oMSyntax"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var omSyntax);
                    _binaryAttributes[name] = IsBinarySyntax(syntax, omSyntax);
                }
            }
            foreach (var name in chunk) _binaryAttributes.TryAdd(name, true);
        }
    }

    private static string ReadSchemaNamingContext(LdapConnection connection)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "schemaNamingContext"));
        return response.Entries.Count == 0 ? "" : SchemaText(response.Entries[0], "schemaNamingContext") ?? "";
    }

    private static string? SchemaText(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0 ? entry.Attributes[name].GetValues(typeof(string))[0] as string : null;

    // A ranged value arrives as "member;range=0-1499"; its syntax is that of "member".
    private static string BaseAttributeName(string name) => name.IndexOf(';', StringComparison.Ordinal) is var index and >= 0 ? name[..index] : name;

    /// <summary>Whether an Active Directory attribute syntax carries binary values (MS-ADTS 3.1.1.2.2.2).</summary>
    /// <remarks>Octet string (2.5.5.10, including replica links), NT security descriptor (2.5.5.15) and SID (2.5.5.17)
    /// are binary, as is an unknown syntax. The string, number, time, boolean and DN syntaxes are text.</remarks>
    internal static bool IsBinarySyntax(string attributeSyntax, int omSyntax) => attributeSyntax switch
    {
        "2.5.5.10" or "2.5.5.15" or "2.5.5.17" => true,
        "2.5.5.1" or "2.5.5.2" or "2.5.5.3" or "2.5.5.4" or "2.5.5.5" or "2.5.5.6" or "2.5.5.7" or "2.5.5.8"
            or "2.5.5.9" or "2.5.5.11" or "2.5.5.12" or "2.5.5.13" or "2.5.5.14" or "2.5.5.16" => omSyntax is 4 or 66,
        _ => true
    };

    /// <summary>Converts one raw LDAP value. Text syntaxes must be valid UTF-8.</summary>
    internal static DirectoryValue ReadValue(byte[] raw, bool binary)
    {
        if (binary) return new DirectoryValue.Binary([.. raw]);
        try { return new DirectoryValue.Text(new UTF8Encoding(false, true).GetString(raw)); }
        catch (DecoderFallbackException) { throw NativeError.Win32("Read directory text value", 13); }
    }

    private static DirectoryObject ReadObject(SearchResultEntry entry, Func<string, bool> isBinary)
    {
        var attributes = ImmutableDictionary.CreateBuilder<string, ImmutableArray<DirectoryValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entry.Attributes.AttributeNames)
        {
            var binary = isBinary(BaseAttributeName(name));
            var values = ImmutableArray.CreateBuilder<DirectoryValue>();
            // GetValues(byte[]) returns the raw values; the attribute's enumerator does not decode them either.
            foreach (byte[] raw in entry.Attributes[name].GetValues(typeof(byte[]))) values.Add(ReadValue(raw, binary));
            attributes.Add(name, values.ToImmutable());
        }
        return new(entry.DistinguishedName, attributes.ToImmutable());
    }
}

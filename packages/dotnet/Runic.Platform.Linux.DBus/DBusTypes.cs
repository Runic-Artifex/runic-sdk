using System.Collections.Immutable;

namespace Runic.Platform.Linux.DBus;

/// <summary>A message bus an application connects to.</summary>
public enum DBusBus
{
    /// <summary>The user's session bus, for desktop services such as notifications, media players and portals.</summary>
    Session,
    /// <summary>The system bus, for system services such as UDisks2, UPower and NetworkManager.</summary>
    System,
}

/// <summary>A D-Bus object path, such as <c>/org/freedesktop/UDisks2</c>.</summary>
/// <remarks>A path is <c>/</c>, or slash-separated elements of ASCII letters, digits and underscores without a trailing slash.</remarks>
public readonly record struct DBusObjectPath
{
    private readonly string? _value;

    /// <summary>Creates a path after checking its syntax.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid object path.</exception>
    public DBusObjectPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!DBusNames.IsObjectPath(value)) throw new ArgumentException($"\"{value}\" is not a D-Bus object path.", nameof(value));
        _value = value;
    }

    /// <summary>The root path, <c>/</c>.</summary>
    public static DBusObjectPath Root { get; } = new("/");

    /// <summary>The path as text. The default value is <c>/</c>.</summary>
    public string Value => _value ?? "/";

    /// <summary>Converts a string to a path.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid object path.</exception>
    public static implicit operator DBusObjectPath(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A D-Bus type signature, such as <c>s</c>, <c>a{sv}</c> or <c>(ii)</c>; empty for no values.</summary>
public readonly record struct DBusSignature
{
    private readonly string? _value;

    /// <summary>Creates a signature after checking its syntax.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid signature.</exception>
    public DBusSignature(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!DBusSignatures.IsValid(value)) throw new ArgumentException($"\"{value}\" is not a D-Bus signature.", nameof(value));
        _value = value;
    }

    internal DBusSignature(string value, bool trusted) => _value = value;

    /// <summary>The signature as text. The default value is empty.</summary>
    public string Value => _value ?? "";

    /// <summary>Whether the signature describes exactly one complete type, as a value's signature does.</summary>
    public bool IsSingleCompleteType => DBusSignatures.IsSingleCompleteType(Value);

    /// <summary>Converts a string to a signature.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid signature.</exception>
    public static implicit operator DBusSignature(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>An error reply from a D-Bus peer, or a call that could not complete.</summary>
/// <remarks>
/// Branch on <see cref="ErrorName"/>, such as <c>org.freedesktop.DBus.Error.ServiceUnknown</c> or a service's own
/// <c>org.freedesktop.UDisks2.Error.NotAuthorized</c>. <see cref="Exception.Message"/> is the peer's text, for logs; it
/// may contain paths or other details and is not meant for display. A closed connection reports
/// <see cref="Disconnected"/>. A service handler throws this exception to reply with an error.
/// </remarks>
public sealed class DBusException : Exception
{
    /// <summary>The error name of a closed or failed connection.</summary>
    public const string Disconnected = "org.freedesktop.DBus.Error.Disconnected";
    /// <summary>The error name of a generic failure.</summary>
    public const string Failed = "org.freedesktop.DBus.Error.Failed";
    /// <summary>The error name of arguments that do not match a method's signature.</summary>
    public const string InvalidArgs = "org.freedesktop.DBus.Error.InvalidArgs";
    /// <summary>The error name of a method an object does not implement.</summary>
    public const string UnknownMethod = "org.freedesktop.DBus.Error.UnknownMethod";
    /// <summary>The error name of an interface an object does not implement.</summary>
    public const string UnknownInterface = "org.freedesktop.DBus.Error.UnknownInterface";
    /// <summary>The error name of a property an object does not have.</summary>
    public const string UnknownProperty = "org.freedesktop.DBus.Error.UnknownProperty";
    /// <summary>The error name of a write to a read-only property.</summary>
    public const string PropertyReadOnly = "org.freedesktop.DBus.Error.PropertyReadOnly";
    /// <summary>The error name of a denied request.</summary>
    public const string AccessDenied = "org.freedesktop.DBus.Error.AccessDenied";

    /// <summary>Creates an error with a D-Bus error name, such as <c>org.example.Error.NotFound</c>, and a message.</summary>
    /// <exception cref="ArgumentException"><paramref name="errorName"/> is not a valid D-Bus error name.</exception>
    public DBusException(string errorName, string message) : this(errorName, message, null) { }

    /// <summary>Creates an error with a D-Bus error name, a message and the exception that caused it.</summary>
    /// <exception cref="ArgumentException"><paramref name="errorName"/> is not a valid D-Bus error name.</exception>
    public DBusException(string errorName, string message, Exception? innerException) : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(errorName);
        if (!DBusNames.IsInterfaceName(errorName)) throw new ArgumentException($"\"{errorName}\" is not a D-Bus error name.", nameof(errorName));
        ErrorName = errorName;
    }

    /// <summary>The D-Bus error name.</summary>
    public string ErrorName { get; }
}

/// <summary>How <see cref="DBusConnection.RequestNameAsync"/> asks for a well-known name.</summary>
[Flags]
public enum DBusRequestNameOptions
{
    /// <summary>Queue behind the current owner, without replacing it.</summary>
    None = 0,
    /// <summary>Let a later request with <see cref="ReplaceExisting"/> take the name.</summary>
    AllowReplacement = 1,
    /// <summary>Take the name from an owner that allows replacement.</summary>
    ReplaceExisting = 2,
    /// <summary>Fail instead of queueing when the name is owned.</summary>
    DoNotQueue = 4,
}

/// <summary>The outcome of <see cref="DBusConnection.RequestNameAsync"/>.</summary>
public enum DBusRequestNameReply
{
    /// <summary>The connection owns the name now.</summary>
    PrimaryOwner = 1,
    /// <summary>The connection waits in the queue for the name.</summary>
    InQueue = 2,
    /// <summary>Another connection owns the name and the request did not queue.</summary>
    Exists = 3,
    /// <summary>The connection already owned the name.</summary>
    AlreadyOwner = 4,
}

/// <summary>Options of a method call.</summary>
[Flags]
public enum DBusCallOptions
{
    /// <summary>No options.</summary>
    None = 0,
    /// <summary>Do not start the destination service if it is not running.</summary>
    NoAutoStart = 1,
    /// <summary>Let the service ask the user for authorization, such as a Polkit password prompt, before it replies.</summary>
    AllowInteractiveAuthorization = 2,
}

/// <summary>A method call: its destination, object, interface, member and arguments.</summary>
/// <param name="Destination">The bus name of the service, such as <c>org.freedesktop.UDisks2</c>, or <see langword="null"/> on a peer-to-peer connection.</param>
/// <param name="Path">The object.</param>
/// <param name="Interface">The interface, such as <c>org.freedesktop.UDisks2.Filesystem</c>.</param>
/// <param name="Member">The method name.</param>
public sealed record DBusMethodCall(string? Destination, DBusObjectPath Path, string Interface, string Member)
{
    /// <summary>The arguments, each carrying its own signature.</summary>
    public ImmutableArray<DBusValue> Arguments { get; init; } = [];
    /// <summary>The call's options.</summary>
    public DBusCallOptions Options { get; init; }
}

/// <summary>A received signal.</summary>
/// <param name="Sender">The unique bus name of the connection that emitted it.</param>
/// <param name="Path">The object that emitted it.</param>
/// <param name="Interface">The signal's interface.</param>
/// <param name="Member">The signal's name.</param>
/// <param name="Arguments">The signal's arguments.</param>
public sealed record DBusSignal(string? Sender, DBusObjectPath Path, string Interface, string Member, ImmutableArray<DBusValue> Arguments);

/// <summary>Which signals a subscription receives. Every set field must match.</summary>
public sealed record DBusSignalFilter
{
    /// <summary>The sender's bus name; a well-known name matches its current owner.</summary>
    public string? Sender { get; init; }
    /// <summary>The emitting object.</summary>
    public DBusObjectPath? Path { get; init; }
    /// <summary>An object and every object below it.</summary>
    public DBusObjectPath? PathNamespace { get; init; }
    /// <summary>The signal's interface.</summary>
    public string? Interface { get; init; }
    /// <summary>The signal's name.</summary>
    public string? Member { get; init; }
    /// <summary>The first argument, when it is a string.</summary>
    public string? Arg0 { get; init; }
}

/// <summary>Properties of one interface that changed, from <c>org.freedesktop.DBus.Properties.PropertiesChanged</c>.</summary>
/// <param name="Interface">The interface whose properties changed.</param>
/// <param name="Changed">The new values, unwrapped from their variants.</param>
/// <param name="Invalidated">Properties that changed without their new value; read them again if needed.</param>
public sealed record DBusPropertyChanges(string Interface, ImmutableDictionary<string, DBusValue> Changed, ImmutableArray<string> Invalidated);

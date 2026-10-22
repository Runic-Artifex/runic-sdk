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

    /// <summary>Compares two paths by <see cref="Value"/>, so the default value equals <see cref="Root"/>.</summary>
    public bool Equals(DBusObjectPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

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

    /// <summary>Compares two signatures by <see cref="Value"/>, so the default value equals the empty signature.</summary>
    public bool Equals(DBusSignature other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

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
    /// <summary>The error name of a call that got no reply in time: the call's <see cref="DBusMethodCall.Timeout"/>
    /// passed, or the bus gave up waiting for the service.</summary>
    public const string NoReply = "org.freedesktop.DBus.Error.NoReply";
    /// <summary>The error name of a request over a limit, such as a call to an exported object that already has as many
    /// calls as <see cref="DBusObject.MaxConcurrentCalls"/> allows running and waiting.</summary>
    public const string LimitsExceeded = "org.freedesktop.DBus.Error.LimitsExceeded";

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
/// <remarks>Calls compare by content: their names, options and arguments, which compare as <see cref="DBusValue"/> does.</remarks>
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

    /// <summary>How long to wait for the reply; <see langword="null"/>, the default, waits until the bus or the service
    /// replies or the call is cancelled.</summary>
    /// <remarks>When the time passes, the call throws <see cref="DBusException"/> with <see cref="DBusException.NoReply"/>
    /// and a later reply is ignored. The service may still have carried out the call. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// also waits without a limit.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative other than
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, or longer than <see cref="Task.WaitAsync(TimeSpan)"/> accepts.</exception>
    public TimeSpan? Timeout
    {
        get;
        init => field = DBusTimeouts.Check(value);
    }

    /// <summary>Compares two calls by content.</summary>
    public bool Equals(DBusMethodCall? other) =>
        ReferenceEquals(this, other) || other is not null && Destination == other.Destination && Path == other.Path && Interface == other.Interface
            && Member == other.Member && Options == other.Options && Timeout == other.Timeout && DBusContent.SequenceEqual(Arguments, other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Destination, Path, Interface, Member, Options, Timeout, DBusContent.SequenceHash(Arguments));
}

internal static class DBusTimeouts
{
    // Task.WaitAsync accepts at most uint.MaxValue - 1 milliseconds.
    private static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    internal static TimeSpan? Check(TimeSpan? value) =>
        value is not { } timeout || timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout > TimeSpan.Zero && timeout <= Longest ? value
            : throw new ArgumentOutOfRangeException(nameof(value), timeout, "A D-Bus call timeout is positive, Timeout.InfiniteTimeSpan or null.");
}

/// <summary>A received signal.</summary>
/// <remarks>The connection creates signals. They compare by content: their origin, names and arguments, which compare as
/// <see cref="DBusValue"/> does.</remarks>
public sealed class DBusSignal : IEquatable<DBusSignal>
{
    internal DBusSignal(string? sender, DBusObjectPath path, string @interface, string member, ImmutableArray<DBusValue> arguments)
    {
        Sender = sender;
        Path = path;
        Interface = @interface;
        Member = member;
        Arguments = arguments.IsDefault ? [] : arguments;
    }

    /// <summary>The unique bus name of the connection that emitted it.</summary>
    public string? Sender { get; }
    /// <summary>The object that emitted it.</summary>
    public DBusObjectPath Path { get; }
    /// <summary>The signal's interface.</summary>
    public string Interface { get; }
    /// <summary>The signal's name.</summary>
    public string Member { get; }
    /// <summary>The signal's arguments.</summary>
    public ImmutableArray<DBusValue> Arguments { get; }

    /// <summary>Compares two signals by content.</summary>
    public bool Equals(DBusSignal? other) =>
        ReferenceEquals(this, other) || other is not null && Sender == other.Sender && Path == other.Path && Interface == other.Interface
            && Member == other.Member && DBusContent.SequenceEqual(Arguments, other.Arguments);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DBusSignal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Sender, Path, Interface, Member, DBusContent.SequenceHash(Arguments));

    /// <inheritdoc />
    public override string ToString() => $"{Interface}.{Member} from {Sender ?? "(no sender)"} at {Path} ({Arguments.Length} arguments)";

    /// <summary>Compares two signals by content.</summary>
    public static bool operator ==(DBusSignal? left, DBusSignal? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two signals by content.</summary>
    public static bool operator !=(DBusSignal? left, DBusSignal? right) => !(left == right);
}

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

    /// <summary>String arguments by position, from 0 to 63, as the match rule's <c>argN</c> keys: the argument at each
    /// position must be a string equal to the value.</summary>
    /// <remarks>Position 0 is the same key as <see cref="Arg0"/>; setting both to different values is an error.</remarks>
    public ImmutableDictionary<int, string> Arguments { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        ImmutableDictionary<int, string>.Empty;

    /// <summary>Path arguments by position, from 0 to 63, as the match rule's <c>argNpath</c> keys: the argument at each
    /// position must be a string or object path that equals the value, or that is a prefix of the other ending in
    /// <c>/</c>. <c>/aa/</c> matches <c>/aa/</c>, <c>/aa/bb</c> and <c>/</c>, but not <c>/aab</c>.</summary>
    public ImmutableDictionary<int, string> ArgumentPaths { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        ImmutableDictionary<int, string>.Empty;

    /// <summary>A namespace of bus or interface names, as the match rule's <c>arg0namespace</c> key: the first argument
    /// must be a string that equals it or starts with it followed by a dot. <c>org.example</c> matches
    /// <c>org.example</c> and <c>org.example.Player</c>, but not <c>org.examples</c>.</summary>
    public string? Arg0Namespace { get; init; }

    /// <summary>Compares two filters by their keys.</summary>
    public bool Equals(DBusSignalFilter? other) =>
        ReferenceEquals(this, other) || other is not null && Sender == other.Sender && Path == other.Path && PathNamespace == other.PathNamespace
            && Interface == other.Interface && Member == other.Member && Arg0 == other.Arg0 && Arg0Namespace == other.Arg0Namespace
            && DBusContent.DictionaryEqual(Arguments, other.Arguments) && DBusContent.DictionaryEqual(ArgumentPaths, other.ArgumentPaths);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Sender, Path, PathNamespace, Interface, Member, Arg0, Arg0Namespace,
        HashCode.Combine(DBusContent.DictionaryHash(Arguments), DBusContent.DictionaryHash(ArgumentPaths)));
}

/// <summary>Properties of one interface that changed, from <c>org.freedesktop.DBus.Properties.PropertiesChanged</c>.</summary>
/// <remarks><see cref="DBusProxy.ReadPropertyChanges"/> creates them. Changes compare by content: the same interface, the
/// same changed names with equal values, and the same invalidated names in the same order.</remarks>
public sealed class DBusPropertyChanges : IEquatable<DBusPropertyChanges>
{
    internal DBusPropertyChanges(string @interface, ImmutableDictionary<string, DBusValue> changed, ImmutableArray<string> invalidated)
    {
        Interface = @interface;
        Changed = changed;
        Invalidated = invalidated.IsDefault ? [] : invalidated;
    }

    /// <summary>The interface whose properties changed.</summary>
    public string Interface { get; }
    /// <summary>The new values, unwrapped from their variants.</summary>
    public ImmutableDictionary<string, DBusValue> Changed { get; }
    /// <summary>Properties that changed without their new value; read them again if needed.</summary>
    public ImmutableArray<string> Invalidated { get; }

    /// <summary>Compares two sets of changes by content.</summary>
    public bool Equals(DBusPropertyChanges? other) =>
        ReferenceEquals(this, other) || other is not null && Interface == other.Interface && DBusContent.DictionaryEqual(Changed, other.Changed)
            && DBusContent.SequenceEqual(Invalidated, other.Invalidated);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DBusPropertyChanges);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Interface, DBusContent.DictionaryHash(Changed), DBusContent.SequenceHash(Invalidated));

    /// <inheritdoc />
    public override string ToString() => $"{Interface}: {Changed.Count} changed, {Invalidated.Length} invalidated";

    /// <summary>Compares two sets of changes by content.</summary>
    public static bool operator ==(DBusPropertyChanges? left, DBusPropertyChanges? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two sets of changes by content.</summary>
    public static bool operator !=(DBusPropertyChanges? left, DBusPropertyChanges? right) => !(left == right);
}

// Content equality for the collections the message records carry; default arrays equal empty ones.
internal static class DBusContent
{
    internal static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right) where T : IEquatable<T> =>
        left.IsDefaultOrEmpty ? right.IsDefaultOrEmpty : !right.IsDefault && left.AsSpan().SequenceEqual(right.AsSpan());

    internal static int SequenceHash<T>(ImmutableArray<T> items)
    {
        var hash = new HashCode();
        if (!items.IsDefault) foreach (var item in items) hash.Add(item);
        return hash.ToHashCode();
    }

    internal static bool DictionaryEqual(ImmutableDictionary<string, DBusValue>? left, ImmutableDictionary<string, DBusValue>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || value != other) return false;
        return true;
    }

    internal static int DictionaryHash(ImmutableDictionary<string, DBusValue>? entries)
    {
        if (entries is null) return 0;
        int hash = entries.Count;
        // Order-independent, since dictionaries with the same entries are equal in any order.
        foreach (var (key, value) in entries) hash ^= HashCode.Combine(key, value);
        return hash;
    }

    internal static bool DictionaryEqual(ImmutableDictionary<int, string>? left, ImmutableDictionary<int, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal)) return false;
        return true;
    }

    internal static int DictionaryHash(ImmutableDictionary<int, string>? entries)
    {
        if (entries is null) return 0;
        int hash = entries.Count;
        foreach (var (key, value) in entries) hash ^= HashCode.Combine(key, value);
        return hash;
    }

    internal static bool InterfacesEqual(ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> left,
        ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || !DictionaryEqual(value, other)) return false;
        return true;
    }

    internal static int InterfacesHash(ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> entries)
    {
        int hash = entries.Count;
        foreach (var (key, value) in entries) hash ^= HashCode.Combine(key, DictionaryHash(value));
        return hash;
    }
}

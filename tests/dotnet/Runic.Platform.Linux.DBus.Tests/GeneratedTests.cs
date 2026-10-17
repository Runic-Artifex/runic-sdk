using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.DBus.Tests.Generated;
using TUnit.Core;
using static ValueTests;

// The proxies and the service generated from Interfaces/org.runic.Test.Media.xml talk to each other on a private bus.
internal sealed class GeneratedTests
{
    private static readonly DBusObjectPath PlayerPath = new("/org/runic/Test/Media");

    [Test, DisplayName("the generated service lists both interfaces in its introspection data")]
    public void Introspection()
    {
        string xml = new TestPlayer().Xml;
        Check(xml.Contains("<interface name=\"org.runic.Test.Media\">", StringComparison.Ordinal) && xml.Contains("<interface name=\"org.runic.Test.Media.Player\">", StringComparison.Ordinal),
            "both interfaces");
        Check(!xml.Contains("doc:", StringComparison.Ordinal) && xml.Contains("EmitsChangedSignal", StringComparison.Ordinal), "documentation removed, annotations kept");
        Check(MediaService.MediaInterface == "org.runic.Test.Media" && Player.InterfaceName == "org.runic.Test.Media.Player", "interface name constants");
    }

    [Test, DisplayName("generated property sets read only known names and reject a wrong type")]
    public void PropertySets()
    {
        var properties = PlayerProperties.Read(new Dictionary<string, DBusValue> { ["Volume"] = DBusValue.From(0.5), ["Unknown"] = DBusValue.From(1) }, ["Metadata"]);
        Check(properties.Volume == 0.5 && properties.PlaybackStatus is null && properties.Invalidated.SequenceEqual(["Metadata"]), "known names");
        try { PlayerProperties.Read(new Dictionary<string, DBusValue> { ["Volume"] = DBusValue.From("loud") }); throw new InvalidOperationException("A wrong type was accepted."); }
        catch (DBusException error) when (error.ErrorName == DBusException.InvalidArgs) { }
    }

    [Test, Explicit, Category("DBus"), DisplayName("generated proxy and service: methods with every type, multiple results and a descriptor")]
    public async Task MethodsAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var implementation = new TestPlayer();
        await using var export = service.Export(PlayerPath, implementation);
        var media = new Media(client, service.UniqueName, PlayerPath);
        var player = new Player(client, service.UniqueName, PlayerPath);

        await media.RaiseAsync();
        Check(implementation.Raised == 1, "Raise reached the service");
        Check(await player.SeekAsync(-5) == 95, "Seek returned the new position");
        await player.OpenUriAsync("file:///music/ω.flac");
        Check(implementation.Uri == "file:///music/ω.flac", "OpenUri");

        var tags = ImmutableDictionary<string, DBusValue>.Empty.Add("genre", DBusValue.From("jazz")).Add("year", DBusValue.From(1959));
        ImmutableArray<(DBusObjectPath, string, string)> tracks = [(new("/t/1"), "So What", "Davis"), (new("/t/2"), "Blue in Green", "Evans")];
        var (name, count, lookup) = await player.DescribeAsync(tags, tracks);
        Check(name == "jazz" && count == 2 && lookup.Count == 2 && lookup[new("/t/2")]["title"].GetString() == "Blue in Green", "Describe's three results");

        var pair = (7, (byte)9);
        var names = ImmutableDictionary<string, string>.Empty.Add("a", "b");
        var mirrored = await player.MirrorAsync(1, true, -2, 3, -4, 5, -6, 7, 0.25, new("/m"), new("a{sv}"), DBusValue.StringArray(["x"]), [1, 2, 3], pair, names);
        Check(mirrored.Item1 == 1 && mirrored.Item2 && mirrored.Item3 == -2 && mirrored.Item4 == 3 && mirrored.Item5 == -4 && mirrored.Item6 == 5
            && mirrored.Item7 == -6 && mirrored.Item8 == 7 && mirrored.Item9 == 0.25 && mirrored.Item10.Value == "/m" && mirrored.Item11.Value == "a{sv}"
            && mirrored.Item12 == DBusValue.StringArray(["x"]) && mirrored.Item13.SequenceEqual(new byte[] { 1, 2, 3 }) && mirrored.Item14 == pair
            && mirrored.Item15["a"] == "b", $"Mirror returned every value: {mirrored}");

        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "liner notes");
            using var handle = File.OpenHandle(file);
            Check(await player.ReadFileAsync(handle) == "liner notes", "a descriptor reached the service");
        }
        finally { File.Delete(file); }

        try { await player.CallAsync(Player.InterfaceName, "Seek", [DBusValue.From("far")]); throw new InvalidOperationException("A wrong signature was accepted."); }
        catch (DBusException error) when (error.ErrorName == DBusException.InvalidArgs) { }
        try { await player.CallAsync(Player.InterfaceName, "Fly"); throw new InvalidOperationException("An unknown method was accepted."); }
        catch (DBusException error) when (error.ErrorName == DBusException.UnknownMethod) { }
        // A call that names no interface finds the member on any of the object's interfaces.
        Check((await client.CallMethodAsync(new DBusMethodCall(service.UniqueName, PlayerPath, Player.InterfaceName, "Seek") { Arguments = [DBusValue.From(1L)] }))[0].GetInt64() == 96,
            "a call with the interface");
    }

    [Test, Explicit, Category("DBus"), DisplayName("generated proxy and service: properties, write-only and read-only, and typed changes")]
    public async Task PropertiesAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var implementation = new TestPlayer { Identity = "Runic Player", CanQuit = true, PlaybackStatus = "Paused" };
        await using var export = service.Export(PlayerPath, implementation);
        var media = new Media(client, service.UniqueName, PlayerPath);
        var player = new Player(client, service.UniqueName, PlayerPath);

        Check(await media.GetIdentityAsync() == "Runic Player" && await media.GetCanQuitAsync(), "typed getters");
        var all = await player.GetPropertiesAsync();
        Check(all.PlaybackStatus == "Paused" && all.Volume == 0 && all.Metadata is { Count: 0 }, "GetAll with the initial values");

        await using var changes = await player.SubscribePropertiesAsync(Player.InterfaceName);
        await player.SetVolumeAsync(0.8);
        Check(implementation.Volume == 0.8, "a caller's write");
        await player.SetRateAsync(1.5);
        Check(implementation.RateSet == 1.5, "a write-only property reaches its hook");
        try { await player.SetPropertyAsync(Player.InterfaceName, "PlaybackStatus", DBusValue.From("Playing")); throw new InvalidOperationException("A read-only property was written."); }
        catch (DBusException error) when (error.ErrorName == DBusException.PropertyReadOnly) { }
        try { await player.SetPropertyAsync(Player.InterfaceName, "Volume", DBusValue.From("loud")); throw new InvalidOperationException("A wrong type was written."); }
        catch (DBusException error) when (error.ErrorName == DBusException.InvalidArgs) { }
        implementation.PlaybackStatus = "Playing";
        implementation.Metadata = ImmutableDictionary<string, DBusValue>.Empty.Add("title", DBusValue.From("So What"));

        var received = new List<PlayerProperties>();
        await using (var enumerator = changes.GetAsyncEnumerator())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (received.Count < 3 && await enumerator.MoveNextAsync().AsTask().WaitAsync(deadline.Token))
                if (DBusProxy.ReadPropertyChanges(enumerator.Current) is { } change) received.Add(PlayerProperties.Read(change.Changed, change.Invalidated));
        }
        Check(received[0].Volume == 0.8 && received[1].PlaybackStatus == "Playing" && received[2].Invalidated.SequenceEqual(["Metadata"]) && received[2].Metadata is null,
            "Volume and PlaybackStatus send their values; Metadata invalidates");
        Check((await player.GetMetadataAsync())["title"].GetString() == "So What", "an invalidated property reads its new value");
    }

    [Test, Explicit, Category("DBus"), DisplayName("generated proxy and service: signals without, with one and with several arguments")]
    public async Task SignalsAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var implementation = new TestPlayer();
        await using var export = service.Export(PlayerPath, implementation);
        var player = new Player(client, service.UniqueName, PlayerPath);
        await using var seeked = await player.SubscribeSignalAsync(Player.InterfaceName, "Seeked");
        await using var stopped = await player.SubscribeSignalAsync(Player.InterfaceName, "Stopped");
        await using var changed = await player.SubscribeSignalAsync(Player.InterfaceName, "TrackChanged");
        Check(implementation.Announce(), "the service emitted its signals");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Check(Player.ReadSeeked((await First(seeked, deadline.Token)).Arguments) == 42, "Seeked");
        Check(Player.ReadStopped((await First(stopped, deadline.Token)).Arguments) == default(PlatformUnit), "Stopped");
        var (track, title) = Player.ReadTrackChanged((await First(changed, deadline.Token)).Arguments);
        Check(track.Value == "/t/3" && title == "Freddie Freeloader", "TrackChanged");
    }

    private static async Task<DBusSignal> First(DBusSignalSubscription subscription, CancellationToken cancellationToken)
    {
        await foreach (var signal in subscription.WithCancellation(cancellationToken)) return signal;
        throw new InvalidOperationException("No signal arrived.");
    }

    private static async Task<DBusConnection> ConnectAsync() =>
        await DBusConnection.ConnectAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var connection) ? connection
            : throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private sealed class TestPlayer : MediaService
    {
        private long _position = 100;
        public int Raised;
        public string? Uri;
        public double RateSet;
        public string Xml => IntrospectionXml;

        protected override ValueTask RaiseAsync(DBusMethodContext context) { Interlocked.Increment(ref Raised); return default; }
        protected override ValueTask<long> SeekAsync(long offset, DBusMethodContext context) => ValueTask.FromResult(Interlocked.Add(ref _position, offset));
        protected override ValueTask OpenUriAsync(string uri, DBusMethodContext context) { Uri = uri; return default; }

        protected override ValueTask<(string Name, uint Count, ImmutableDictionary<DBusObjectPath, ImmutableDictionary<string, DBusValue>> Lookup)> DescribeAsync(
            ImmutableDictionary<string, DBusValue> tags, ImmutableArray<(DBusObjectPath, string, string)> tracks, DBusMethodContext context) =>
            ValueTask.FromResult((tags["genre"].GetString(), (uint)tracks.Length,
                tracks.ToImmutableDictionary(static track => track.Item1, static track => ImmutableDictionary<string, DBusValue>.Empty.Add("title", DBusValue.From(track.Item2)))));

        protected override ValueTask<(byte, bool, short, ushort, int, uint, long, ulong, double, DBusObjectPath, DBusSignature, DBusValue, ImmutableArray<byte>, (int, byte), ImmutableDictionary<string, string>)> MirrorAsync(
            byte @byte, bool flag, short small, ushort usmall, int @int, uint @uint, long @long, ulong @ulong, double @double, DBusObjectPath path, DBusSignature signature,
            DBusValue any, ImmutableArray<byte> bytes, (int, byte) pair, ImmutableDictionary<string, string> names, DBusMethodContext context) =>
            ValueTask.FromResult((@byte, flag, small, usmall, @int, @uint, @long, @ulong, @double, path, signature, any, bytes, pair, names));

        protected override async ValueTask<string> ReadFileAsync(System.Runtime.InteropServices.SafeHandle file, DBusMethodContext context)
        {
            using var stream = new FileStream((SafeFileHandle)file, FileAccess.Read);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        protected override ValueTask OnSetRateAsync(double value, DBusMethodContext context)
        {
            RateSet = value;
            return base.OnSetRateAsync(value, context);
        }

        public bool Announce() => EmitSeeked(42) & EmitStopped() & EmitTrackChanged(new DBusObjectPath("/t/3"), "Freddie Freeloader");
    }
}

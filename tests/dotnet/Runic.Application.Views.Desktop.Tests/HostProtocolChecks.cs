using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Platform;
using TUnit.Core;

// The launch lines, proofs and outcome mapping of the Runic host protocol (specs/desktop/host-protocol.md), which the
// guest, the host and RunicTestHost share. The handshake and window flows are checked by HostConformanceTests.
internal sealed class HostProtocolChecks
{
    private const string Key = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8";

    [Test]
    public void ProofsMatchTheSpecificationsExample()
    {
        var nonce = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();
        Check(HostProtocol.Proof(nonce, HostProtocol.HostProofLabel, Key) == "yimS5xcNM_YD_FItXRTnVjS5J28tOoyGrJDDyVNWtLc", "host proof");
        Check(HostProtocol.Proof(nonce, HostProtocol.GuestProofLabel, Key) == "_GCMgx2rs30ncRjekFxud93sLUfLdj2fZ08dXHOFyOI", "guest proof");
        Check(HostProtocol.DecodeSecret(HostProtocol.NewSecret()) is { Length: 32 }, "a new secret is 32 bytes in base64url");
    }

    [Test]
    [Arguments("ws://127.0.0.1:41234/_runic_host", true)]
    [Arguments("ws://[::1]:41234/_runic_host", true)]
    [Arguments("ws://localhost:41234/_runic_host", false)]
    [Arguments("ws://10.0.0.5:41234/_runic_host", false)]
    [Arguments("wss://127.0.0.1:41234/_runic_host", false)]
    [Arguments("ws://127.0.0.1:41234/other", false)]
    [Arguments("ws://127.0.0.1:41234/_runic_host?x=1", false)]
    [Arguments("ws://127.0.0.1/_runic_host", false)]
    [DisplayName("ChannelLineUrl(url: $url, valid: $valid)")]
    public void OnlyALoopbackChannelLineIsValid(string url, bool valid)
    {
        var line = new JsonObject { ["url"] = url, ["key"] = Key, ["protocol"] = 1 };
        Check((HostProtocol.ReadChannel(line) is not null) == valid, $"{url} read as {(valid ? "invalid" : "valid")}");
        Check(HostProtocol.ReadChannel(new JsonObject { ["url"] = "ws://127.0.0.1:41234/_runic_host", ["key"] = "short", ["protocol"] = 1 }) is null,
            "a short key was accepted");
    }

    [Test]
    public void OffersAreCheckedForRange()
    {
        static JsonObject Offer(JsonNode protocols) => new()
        {
            ["protocols"] = protocols,
            ["nonce"] = Key,
            ["host"] = new JsonObject { ["name"] = "Host", ["version"] = "1.0" },
        };
        Check(HostProtocol.ReadOffer(Offer(new JsonArray(1, 2))) is { Protocols: [1, 2] }, "a valid offer");
        Check(HostProtocol.ReadOffer(Offer(new JsonArray())) is null, "an empty protocol list");
        Check(HostProtocol.ReadOffer(Offer(new JsonArray(1, 1))) is null, "a repeated version");
        Check(HostProtocol.ReadOffer(Offer(new JsonArray(0))) is null, "version 0");
        Check(HostProtocol.ReadOffer(Offer(new JsonArray([.. Enumerable.Range(1, 17).Select(static v => (JsonNode)v)]))) is null, "17 versions");
        Check(HostProtocol.ParseLine("RUNIC_HOST_OFFER=[1]", HostProtocol.OfferPrefix) is null, "an array instead of an object");
        Check(HostProtocol.ParseLine(" RUNIC_HOST_OFFER={}", HostProtocol.OfferPrefix) is null, "a prefix not at the start of the line");
    }

    [Test]
    public void RepliesBecomePlatformResults()
    {
        static PlatformResult<PlatformUnit> Outcome(string json) => Runic.Application.Views.Desktop.Hosting.HostedWindowPresentation.OutcomeOf(JsonNode.Parse(json)!.AsObject());
        Check(Outcome("""{"id":1,"ok":true,"outcome":"success"}""") is PlatformResult<PlatformUnit>.Success, "success");
        Check(Outcome("""{"id":1,"ok":true,"outcome":"unavailable","reason":"owner-closed"}""") is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "owner-closed");
        Check(Outcome("""{"id":1,"ok":true,"outcome":"unavailable","reason":"later-reason"}""") is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "an unknown reason");
        Check(Outcome("""{"id":1,"ok":true,"outcome":"failed","code":"io-error"}""") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IOError }, "io-error");
        Check(Outcome("""{"id":1,"ok":true,"outcome":"later"}""") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Unknown }, "an unknown outcome");
        Check(Outcome("""{"id":1,"ok":false,"error":{"kind":"rejected","reason":"busy","message":"m"}}""") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "busy");
        Check(Outcome("""{"id":1,"ok":false,"error":{"kind":"rejected","reason":"refused","message":"m"}}""") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "refused");
        Check(HostProtocol.Kebab(nameof(PlatformFailureCode.IOError)) == "io-error" &&
              HostProtocol.Kebab(nameof(PlatformUnavailableReason.ProviderNotConfigured)) == "provider-not-configured", "kebab tokens");
    }

    // host-protocol.md, Same user: all four uids equal, or refuse; a parent that cannot be read refuses (fail closed).
    [Test]
    public void TheSameUserCheckFailsClosed()
    {
        static Func<string, string[]?> Proc(string? self, string? parent) => path => path switch
        {
            "/proc/self/status" => self?.Split('\n'),
            "/proc/4242/status" => parent?.Split('\n'),
            _ => null,
        };
        const string Self = "Name:\tguest\nPPid:\t4242\nUid:\t1000\t1000\t1000\t1000";
        Check(GuestPrivileges.SameUserOnLinux(Proc(Self, "Name:\thost\nUid:\t1000\t1000\t1000\t1000")), "the same user");
        Check(!GuestPrivileges.SameUserOnLinux(Proc(Self, null)), "a parent hidden by hidepid");
        Check(!GuestPrivileges.SameUserOnLinux(Proc(Self, "Name:\thost\nUid:\t1001\t1001\t1001\t1001")), "another user's parent");
        Check(!GuestPrivileges.SameUserOnLinux(Proc(Self, "Name:\thost\nUid:\t1000\t0\t1000\t1000")), "a parent with another effective uid");
        Check(!GuestPrivileges.SameUserOnLinux(Proc("PPid:\t4242\nUid:\t1000\t1001\t1001\t1001", "Uid:\t1000\t1000\t1000\t1000")),
            "a setuid guest (its effective uid is another user's)");
        Check(!GuestPrivileges.SameUserOnLinux(Proc("PPid:\t1\nUid:\t1000\t1000\t1000\t1000", null)), "a parent that is gone");
        Check(!GuestPrivileges.SameUserOnLinux(Proc(null, null)), "no /proc");
        Check(!GuestPrivileges.SameUserOnLinux(Proc(Self, "Name:\thost\nUid:\t1000")), "a parent status without an effective uid");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Check(GuestPrivileges.Check() is null || Environment.IsPrivilegedProcess, "this test process, started by its own user, passes");
    }

    // host-protocol.md, Same user, on Windows: equal TokenUser SIDs, a parent created before this process (else its id
    // was reused), and refusal on any failure (fail closed).
    [Test]
    public void TheWindowsSameUserCheckFailsClosed()
    {
        byte[] user = [1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0, 1, 2, 3, 4];
        byte[] other = [1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0, 9, 9, 9, 9];
        var self = new GuestPrivileges.WindowsProcessIdentity(1_000, user);
        static Func<int, GuestPrivileges.WindowsProcessIdentity?> Parent(GuestPrivileges.WindowsProcessIdentity? identity) => id => id == 4242 ? identity : null;
        Check(GuestPrivileges.SameUserOnWindows(() => self, () => 4242, Parent(new(900, [.. user]))), "the same user");
        Check(GuestPrivileges.SameUserOnWindows(() => self, () => 4242, Parent(new(1_000, [.. user]))), "a parent created in the same tick");
        Check(!GuestPrivileges.SameUserOnWindows(() => self, () => 4242, Parent(new(900, other))), "another user's parent");
        Check(!GuestPrivileges.SameUserOnWindows(() => self, () => 4242, Parent(new(1_100, [.. user]))), "a process that reused the gone parent's id");
        Check(!GuestPrivileges.SameUserOnWindows(() => self, () => 4242, Parent(null)), "a parent that is gone or whose token cannot be read");
        Check(!GuestPrivileges.SameUserOnWindows(() => self, () => null, Parent(new(900, [.. user]))), "no parent id");
        Check(!GuestPrivileges.SameUserOnWindows(() => self, () => 0, Parent(new(900, [.. user]))), "parent id 0");
        Check(!GuestPrivileges.SameUserOnWindows(() => null, () => 4242, Parent(new(900, [.. user]))), "an unreadable own token");
        Check(!GuestPrivileges.SameUserOnWindows(() => new(1_000, []), () => 4242, Parent(new(900, []))), "empty SIDs");
        if (OperatingSystem.IsWindows())
            Check(Runic.Application.Views.Desktop.Hosting.WindowsProcesses.Self() is { UserSid.Length: > 0 }, "this process's own token user");
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException($"Failed: {what}.");
    }
}

using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// appearance/1 (specs/application/README.md, "Appearance"): WindowContentSession.Appearance is pushed to the window's
// pages after their hello and on each change, and RunicWindowTestHost.AppearancePushes reads the pushes back.
public sealed class AppearanceTests
{
    private static readonly DesktopAccentColor Black = new(0, 0, 0);
    private static readonly DesktopAccentColor White = new(1, 1, 1);

    private static readonly DesktopAppearance Forced = new(DesktopColorScheme.Dark, new(0, 0, 1), ReducedMotion: false)
    {
        Contrast = DesktopContrast.ForcedColors,
        SystemColors = new(Black, White, new(1, 1, 0), new(0, 1, 0), new(0, 1, 1), Black, Black, White),
        TextScale = 1.25,
    };

    [Test]
    public async Task TheWindowsAppearanceIsPushedAfterTheHelloAndOnEachChange()
    {
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(new TranslatedTextViewModel(),
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        host.Content.Appearance = Forced;
        Require(host.Content.Appearance == Forced, "The session did not keep its appearance.");
        host.Transport.HelloWith("old", []);
        // The test host's own connection lists appearance/1 in the hello it sends before its first request.
        await host.NavigateBackAsync();
        await WaitAsync(() => host.AppearancePushes.Count == 1, "the push after the hello");
        Require(host.AppearancePushes[0] == Forced, $"The push read back as {host.AppearancePushes[0]}.");

        var more = new DesktopAppearance(DesktopColorScheme.Light, ReducedMotion: true) { Contrast = DesktopContrast.More };
        host.Content.Appearance = more;
        host.Content.Appearance = more with { };
        await WaitAsync(() => host.AppearancePushes.Count == 2, "the push of the change");
        Require(host.AppearancePushes[1] == more && host.AppearancePushes[1]!.SystemColors is null,
            $"The change read back as {host.AppearancePushes[1]}; a palette is pushed only with forced colours.");

        host.Content.Appearance = null;
        await WaitAsync(() => host.AppearancePushes.Count == 3, "the push of an unknown appearance");
        Require(host.AppearancePushes[2] is null, "An unknown appearance is not every member null.");
        Require(!host.Transport.Messages.Any(message => message.Kind == "appearance" && message.ConnectionKey == "old"),
            "A connection without the feature received an appearance push.");
    }

    [Test]
    public async Task AnUnknownAppearanceIsPushedAfterTheHelloSoAReconnectedPageKeepsNoStaleOne()
    {
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(new TranslatedTextViewModel(),
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        // A window without an appearance tells each page so after its hello.
        await host.NavigateBackAsync();
        await WaitAsync(() => host.AppearancePushes.Count == 1, "the push after the hello");
        Require(host.AppearancePushes[0] is null, $"The window without an appearance pushed {host.AppearancePushes[0]}.");
        // A page that reconnects while the appearance changed gets the current one after its new hello.
        host.Content.Appearance = Forced;
        host.Content.Appearance = null;
        host.Transport.HelloWith("reconnected", ["appearance/1"]);
        await WaitAsync(() => host.Transport.Messages.Any(message => message.Kind == "appearance" && message.ConnectionKey == "reconnected"),
            "the push after the reconnected page's hello");
        var push = host.Transport.Messages.Single(message => message.Kind == "appearance" && message.ConnectionKey == "reconnected").Json;
        Require(push == """{"kind":"appearance","colorScheme":null,"contrast":null,"reducedMotion":null,"textScale":null,"accentColor":null,"systemColors":null}""",
            $"The reconnected page was pushed {push}.");
    }

    [Test]
    public void TheMembersAreWrittenAsThePushSpecifies()
    {
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(new TranslatedTextViewModel(),
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        // A ForcedColors palette with a component out of range is clamped; a palette without forced colours is not pushed.
        host.Content.Appearance = new DesktopAppearance(AccentColor: new(1.5, -1, 0.5)) { SystemColors = Forced.SystemColors, TextScale = 1 };
        host.Transport.HelloWith(InMemoryBridgeTransport.DefaultConnectionKey, ["appearance/1"]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Transport.Messages.Any(message => message.Kind == "appearance") && DateTime.UtcNow < deadline) Thread.Sleep(10);
        var push = host.Transport.Messages.First(message => message.Kind == "appearance").Json;
        Require(push == """{"kind":"appearance","colorScheme":null,"contrast":null,"reducedMotion":null,"textScale":1,"accentColor":"#ff0080","systemColors":null}""",
            $"The push is {push}.");
    }

    // The members the host decided for the window (a hosted region's appearance override) are listed in override, in the
    // order colorScheme, contrast, reducedMotion, and only while the push carries their value; a window without decided
    // members pushes no override member, so the exact pushes of other windows stay as they were.
    [Test]
    public async Task TheHostDecidedMembersAreListedInOverride()
    {
        using var host = new RunicWindowTestHost<TranslatedTextViewModel>(new TranslatedTextViewModel(),
            (transport, content, vm) => new TranslatedTextBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "translatedText" });
        var dark = new DesktopAppearance(DesktopColorScheme.Dark, ReducedMotion: false) { Contrast = DesktopContrast.More, TextScale = 1 };
        host.Content.SetAppearance(dark, HostDecidedAppearance.ReducedMotion | HostDecidedAppearance.Contrast | HostDecidedAppearance.ColorScheme);
        Require(host.Content.DecidedAppearance == (HostDecidedAppearance.ColorScheme | HostDecidedAppearance.Contrast | HostDecidedAppearance.ReducedMotion),
            "The session did not keep the decided members.");
        host.Transport.HelloWith("page", ["appearance/1"]);
        await WaitAsync(() => Pushes(host, "page").Length == 1, "the push after the hello");
        Require(Pushes(host, "page")[0] ==
            """{"kind":"appearance","colorScheme":"dark","contrast":"more","reducedMotion":false,"textScale":1,"accentColor":null,"systemColors":null,"override":["colorScheme","contrast","reducedMotion"]}""",
            $"The first push is {Pushes(host, "page")[0]}.");

        // The same appearance with other decided members is a change; the same both is none.
        host.Content.SetAppearance(dark, HostDecidedAppearance.Contrast);
        host.Content.SetAppearance(dark with { }, HostDecidedAppearance.Contrast);
        await WaitAsync(() => Pushes(host, "page").Length == 2, "the push of the decided members' change");
        Require(Pushes(host, "page")[1].EndsWith("\"systemColors\":null,\"override\":[\"contrast\"]}", StringComparison.Ordinal),
            $"The change is {Pushes(host, "page")[1]}.");

        // A decided member whose value is unknown is not listed, and an empty list is no member at all.
        host.Content.SetAppearance(new DesktopAppearance { TextScale = 2 }, HostDecidedAppearance.ColorScheme | HostDecidedAppearance.ReducedMotion);
        await WaitAsync(() => Pushes(host, "page").Length == 3, "the push of unknown decided members");
        Require(Pushes(host, "page")[2] ==
            """{"kind":"appearance","colorScheme":null,"contrast":null,"reducedMotion":null,"textScale":2,"accentColor":null,"systemColors":null}""",
            $"The push is {Pushes(host, "page")[2]}.");

        // Setting the public Appearance clears the decided members.
        host.Content.SetAppearance(dark, HostDecidedAppearance.Contrast);
        host.Content.Appearance = dark;
        await WaitAsync(() => Pushes(host, "page").Length == 5, "the pushes of the public setter");
        Require(!Pushes(host, "page")[4].Contains("override", StringComparison.Ordinal) && host.Content.DecidedAppearance == HostDecidedAppearance.None,
            $"The public setter pushed {Pushes(host, "page")[4]}.");
    }

    private static string[] Pushes(RunicWindowTestHost<TranslatedTextViewModel> host, string connection) =>
        [.. host.Transport.Messages.Where(message => message.Kind == "appearance" && message.ConnectionKey == connection).Select(static message => message.Json)];

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), $"Timed out waiting for {what}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// --region: the region fixture (Runic.Desktop.WebViewSmoke/AccessibilitySmoke.cs, RegionPage): a host page whose region
// shows a guest's page in a cross-site frame, as region mode does (#866). A screen reader reaches the guest's controls
// inside the region as part of the host's window: the probe finds the guest's button through the platform accessibility
// API, presses it, which the guest's page tells the host page, and then presses the host's Finish.
internal sealed class RegionScenario(AccessibilityClient client, Report report)
{
    internal const string GuestButton = "Run system check";
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(30);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var finish = await FindAsync(Fixture.Finish, cancellationToken);
        report.Pass("region-host-control", $"{finish.Name}: {finish.Role}");
        // The frame loads after the host page: its tree joins the window's once it has.
        var guest = await FindAsync(GuestButton, cancellationToken);
        report.Pass("region-guest-control", $"{guest.Name}: {guest.Role}, inside the region's frame");
        await client.PressAsync(guest, cancellationToken);
        report.Pass("region-guest-press", "pressed through the accessibility API; the fixture checks that the guest's page saw it");
        await client.PressAsync(finish, cancellationToken);
    }

    private async Task<Control> FindAsync(string name, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + TestTimeouts.Scale(FindTimeout);
        while (true)
        {
            if (await client.FindAsync(ControlKind.Button, name, cancellationToken) is { } control) return control;
            if (DateTime.UtcNow >= until)
            {
                Console.WriteLine("Accessibility tree:\n" + await client.DumpAsync(cancellationToken));
                throw new InvalidOperationException($"No button named '{name}' in the accessibility tree of the region fixture.");
            }
            await Task.Delay(250, cancellationToken);
        }
    }
}

using System.Reflection;
using Runic.Application.Testing;
using Runic.Platform;

namespace CounterTool.Tests;

// The counter in hosted mode, without a shell: RunicTestHost starts the built counter with its `hosted` parameter, plays
// the host of the Runic host protocol and answers its menus, message boxes and links. No browser loads the page: the
// counter does not wait for it (Program.cs), and the menu bar's items run the Window's commands without it.
public sealed class HostedCounterTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static Task<RunicTestHost> StartAsync(RunicTestHostOptions? options = null)
    {
        // The counter's own build (CounterTool.Tests.csproj), which the project reference builds first.
        var start = RunicServeOnlyApplication.CreateStartInfo(typeof(HostedCounterTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CounterTool").Value!);
        start.ArgumentList.Add("hosted");
        return RunicTestHost.StartGuestAsync(start, options);
    }

    private static Task WaitForMenuAsync(RunicTestHostedWindow window, params string[] titles) =>
        window.WaitForMenuBarAsync(_ => window.FindMenuItem(titles) is not null, Wait);

    [Test]
    public async Task Shows_its_window_and_menu_bar_in_the_hosts_language()
    {
        await using var host = await StartAsync(new RunicTestHostOptions { Locale = "de" });
        var window = await host.WaitForWindowAsync(nameof(CounterWindow), Wait);

        await WaitForMenuAsync(window, "Zähler", "Zurücksetzen…");
        await Assert.That(host.Guest.Name).IsEqualTo("Counter");
        await Assert.That(window.Locale).IsEqualTo("de");
    }

    [Test]
    public async Task Follows_the_hosts_language_switch()
    {
        await using var host = await StartAsync(new RunicTestHostOptions { Locale = "en" });
        var window = await host.WaitForWindowAsync(nameof(CounterWindow), Wait);
        await WaitForMenuAsync(window, "Counter", "Increment");

        await host.SetLocaleAsync("de");

        await WaitForMenuAsync(window, "Zähler", "Erhöhen");
    }

    [Test]
    public async Task Reset_asks_through_the_hosts_message_box_in_its_language()
    {
        await using var host = await StartAsync(new RunicTestHostOptions
        {
            Locale = "de",
            MessageBox = (_, _) => Task.FromResult(RunicTestDialogAnswer.Choose("cancel")),
        });
        var window = await host.WaitForWindowAsync(nameof(CounterWindow), Wait);
        await WaitForMenuAsync(window, "Zähler", "Zurücksetzen…");

        await window.InvokeMenuItemAsync(window.FindMenuItem("Zähler", "Zurücksetzen…")!);

        using var timeout = new CancellationTokenSource(Wait);
        while (window.DialogRequests.Count == 0) await Task.Delay(50, timeout.Token);
        var question = window.DialogRequests[0];
        await Assert.That(question.DialogKind).IsEqualTo(RunicTestDialogKind.MessageBox);
        await Assert.That(question.Text).IsEqualTo("Den Stand 0 auf 0 zurücksetzen?");
        await Assert.That(question.ButtonIds).IsEquivalentTo(["reset", "cancel"]);
    }

    [Test]
    public async Task Opens_the_documentation_with_the_hosts_launcher()
    {
        await using var host = await StartAsync(new RunicTestHostOptions
        {
            Locale = "en",
            // A shell's policy may refuse the link; the counter then shows that on its page (CounterViewModel.Status).
            Launch = _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied),
        });
        var window = await host.WaitForWindowAsync(nameof(CounterWindow), Wait);
        await WaitForMenuAsync(window, "Help", "Runic documentation");

        await window.InvokeMenuItemAsync(window.FindMenuItem("Help", "Runic documentation")!);

        using var timeout = new CancellationTokenSource(Wait);
        while (window.LaunchRequests.Count == 0) await Task.Delay(50, timeout.Token);
        await Assert.That(window.LaunchRequests[0].Uri).IsEqualTo(CounterViewModel.Documentation);
    }
}

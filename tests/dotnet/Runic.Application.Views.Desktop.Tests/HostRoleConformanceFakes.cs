using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;

// The fakes of the host role of the Runic host protocol corpus (specs/desktop/host-conformance, "Runner seams"), which the
// driver's adapter (HostRoleConformanceAdapter) uses.

// The scenario's policy: the defaults, or one that fails for window services, or one that asks the user before each
// launch in a prompt of the host's own (prompts-launches), which a user step answers with the button allow or deny.
// With embed, the guest's windows that can show in a region and have no owner show in the region of that name
// (region/1).
internal sealed class ScenarioPolicy(RunicHostedLimits limits, bool failsServices,
    Func<CancellationToken, Task<object?>>? prompt = null, string? embed = null, RunicHostedRegionOptions? embedOptions = null,
    bool embedComponent = false, bool limitsPlacement = false) : IRunicHostedWindowPolicy
{
    public RunicHostedLimits Limits => limits;

    // limits-placement: maximize is refused, and a placement's size clamped to 1,200 by 900.
    public RunicHostedPresentationDecision Present(RunicHostedPresentationRequest request) => !limitsPlacement
        ? RunicHostedPresentationDecision.Allow()
        : request.Kind switch
        {
            RunicHostedPresentationKind.Maximize => RunicHostedPresentationDecision.Refuse("The host keeps its windows unmaximized."),
            RunicHostedPresentationKind.SetPlacement when request.Placement is { } placement => RunicHostedPresentationDecision.Apply(request with
            {
                Placement = placement with { Width = Math.Min(placement.Width, 1_200), Height = Math.Min(placement.Height, 900) },
            }),
            _ => RunicHostedPresentationDecision.Allow(),
        };

    public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(embed is not null && request.CanEmbed && request.Owner is null
            // With embedComponent, also for a guest that does not allow it, whose open then fails.
            ? embedComponent
                ? RunicHostedWindowDecision.EmbedComponent(embed, embedOptions ?? RunicHostedRegionOptions.Default)
                : RunicHostedWindowDecision.Embed(embed, embedOptions ?? RunicHostedRegionOptions.Default)
            : RunicHostedWindowDecision.Allow());

    public ValueTask<RunicHostedServiceDecision> ServiceAsync(RunicHostedServiceRequest request, CancellationToken cancellationToken) =>
        failsServices
            ? throw new InvalidOperationException("The scenario's policy fails.")
            : prompt is not null && request.Kind == RunicHostedServiceKind.Launch
                ? new(AskAsync(request, prompt, cancellationToken))
                : ValueTask.FromResult(Default(request));

    private static async Task<RunicHostedServiceDecision> AskAsync(RunicHostedServiceRequest request, Func<CancellationToken, Task<object?>> prompt,
        CancellationToken cancellationToken) =>
        await prompt(cancellationToken) as string == "allow" ? Default(request) : RunicHostedServiceDecision.Refuse();

    private static RunicHostedServiceDecision Default(RunicHostedServiceRequest request) =>
        request.IsAllowedByDefault ? RunicHostedServiceDecision.Allow() : RunicHostedServiceDecision.Refuse();
}

// The host's user: each context menu and dialog the host shows waits until a user step answers it, or until the host
// closes it (its token).
internal sealed class HostUser
{
    internal sealed record Shown(object What, string Kind, TaskCompletionSource<object?> Answer, CancellationToken Closed);

    private readonly System.Threading.Channels.Channel<Shown> _menus = System.Threading.Channels.Channel.CreateUnbounded<Shown>();
    private readonly System.Threading.Channels.Channel<Shown> _dialogs = System.Threading.Channels.Channel.CreateUnbounded<Shown>();
    // What the host shows that no user step answered yet, in the order it showed them.
    private readonly List<Shown> _shownMenus = [];
    private readonly List<Shown> _shownDialogs = [];

    public async Task<DesktopMenuItem?> ContextMenuAsync(ImmutableArray<DesktopMenuEntry> entries, DesktopContextMenuOptions options, CancellationToken cancellationToken)
    {
        var shown = new Shown(entries, "context-menu", new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
        await _menus.Writer.WriteAsync(shown, CancellationToken.None);
        return (DesktopMenuItem?)await shown.Answer.Task.WaitAsync(cancellationToken);
    }

    // A prompt the host's policy shows itself (host-prompt), answered with a button.
    public async Task<object?> PromptAsync(CancellationToken cancellationToken)
    {
        var shown = new Shown("host-prompt", "host-prompt", new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
        await _dialogs.Writer.WriteAsync(shown, CancellationToken.None);
        return await shown.Answer.Task.WaitAsync(cancellationToken);
    }

    public async Task<object?> DialogAsync(FakeHostDialog dialog, CancellationToken cancellationToken)
    {
        var kind = dialog.Kind switch
        {
            RunicHostedDialogKind.OpenFile => "open",
            RunicHostedDialogKind.OpenFiles => "open-multiple",
            RunicHostedDialogKind.SaveFile => "save",
            RunicHostedDialogKind.OpenDirectory => "folder",
            RunicHostedDialogKind.OpenDirectories => "folder-multiple",
            _ => "message-box",
        };
        var shown = new Shown(dialog, kind, new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
        await _dialogs.Writer.WriteAsync(shown, CancellationToken.None);
        return await shown.Answer.Task.WaitAsync(cancellationToken);
    }

    // Waits until the host closes the oldest dialog (of the kind, when given) that a step held open.
    public async Task AwaitClosedAsync(string? kind, TimeSpan timeout)
    {
        var shown = _shownDialogs.FirstOrDefault(shown => kind is null || shown.Kind == kind)
            ?? throw new InvalidOperationException($"No {kind ?? "dialog"} is held open.");
        try { await Task.Delay(Timeout.Infinite, shown.Closed).WaitScaledAsync(timeout); }
        catch (OperationCanceledException) when (shown.Closed.IsCancellationRequested) { }
        catch (TimeoutException) { throw new InvalidOperationException($"The host did not close the {shown.Kind}."); }
        _shownDialogs.Remove(shown);
    }

    // Waits until the host shows a context menu or dialog (of the kind, when given) that no step answered yet; it stays
    // open for a later answer.
    public async Task HoldAsync(bool menu, string? kind, TimeSpan timeout) => _ = await FindAsync(menu, kind, timeout);

    // The oldest context menu or dialog (of the kind, when given) that the host shows and no step answered yet.
    public async Task<Shown> TakeAsync(bool menu, string? kind, TimeSpan timeout)
    {
        var shown = await FindAsync(menu, kind, timeout);
        (menu ? _shownMenus : _shownDialogs).Remove(shown);
        return shown;
    }

    private async Task<Shown> FindAsync(bool menu, string? kind, TimeSpan timeout)
    {
        var list = menu ? _shownMenus : _shownDialogs;
        using var deadline = TestTimeouts.Cancellation(timeout);
        while (true)
        {
            // One the host already closed is gone.
            list.RemoveAll(static shown => shown.Closed.IsCancellationRequested);
            if (list.FirstOrDefault(shown => kind is null || shown.Kind == kind) is { } found) return found;
            try { list.Add(await (menu ? _menus : _dialogs).Reader.ReadAsync(deadline.Token)); }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"The host showed no {(menu ? "context menu" : kind ?? "dialog")}; it shows [{string.Join(", ", list.Select(static shown => shown.Kind))}].");
            }
        }
    }

    public static DesktopMenuItem? Find(IEnumerable<DesktopMenuEntry> entries, string title)
    {
        foreach (var entry in entries)
        {
            if (entry is DesktopMenuItem item && item.Title == title) return item;
            if (entry is DesktopMenu menu && Find(menu.Items, title) is { } found) return found;
        }
        return null;
    }
}

// The native menu bar of a window as entries of the wire format without their ids (README, Steps).
internal static class HostMenuWire
{
    public static JsonArray Entries(IEnumerable<DesktopMenuEntry> entries) => new([.. entries.Select(static entry => (JsonNode)(entry switch
    {
        DesktopMenu menu => new JsonObject
        {
            ["kind"] = "submenu",
            ["title"] = menu.Title,
            ["role"] = menu.Role switch
            {
                DesktopMenuRole.Application => "application",
                DesktopMenuRole.File => "file",
                DesktopMenuRole.Help => "help",
                _ => null,
            },
            ["enabled"] = menu.IsEnabled,
            ["items"] = Entries(menu.Items),
        },
        DesktopMenuItem item => new JsonObject
        {
            ["kind"] = "item",
            ["title"] = item.Title,
            ["shortcut"] = item.Shortcut?.ToString(),
            ["enabled"] = item.IsEnabled,
            ["checked"] = item.IsChecked,
        },
        _ => new JsonObject { ["kind"] = "separator" },
    }))]);
}

using Runic.Application.Views.Desktop;

namespace HostedShell;

// The shell's rules for the counter's windows. Every member it does not implement keeps its default: every window
// opens, and presentation requests and menus apply unchanged.
public sealed class CounterPolicy : IRunicHostedWindowPolicy
{
    // A guest's dialogs show over its window with the guest's own title and text, which nothing marks as the guest's: the
    // shell puts the guest's name in front of each title.
    public ValueTask<RunicHostedDialogDecision> DialogAsync(RunicHostedDialogRequest request, CancellationToken cancellationToken)
    {
        var guest = request.Window.Application.Guest.Name;
        return ValueTask.FromResult(RunicHostedDialogDecision.Apply(request.MessageBox is { } messageBox
            ? request with { MessageBox = messageBox with { Title = Mark(guest, messageBox.Title) } }
            : request with { Title = Mark(guest, request.Title) }));
    }

    // Narrower than the defaults, which open any web or mail link the user's active window asks for: the shell opens only
    // the Runic documentation, and no files. The guest sees a refusal as PermissionDenied.
    public ValueTask<RunicHostedServiceDecision> ServiceAsync(RunicHostedServiceRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.IsAllowedByDefault && (request.Kind != RunicHostedServiceKind.Launch || MayOpen(request.Uri))
            ? RunicHostedServiceDecision.Allow()
            : RunicHostedServiceDecision.Refuse("The shell opens only the Runic documentation."));

    public static string Mark(string guest, string? title) => string.IsNullOrEmpty(title) ? guest : $"{guest}: {title}";

    public static bool MayOpen(Uri? uri) => uri is { Scheme: "https", Host: "docs.runic-artifex.eu" };
}

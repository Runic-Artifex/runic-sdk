using Runic.Application.Views.Desktop;

namespace HostedDashboard;

// The dashboard's rule for a widget's window: show it in its region of the dashboard page, as a component where the
// widget allows it (its RunicGuestOptions.AllowComponentEmbedding names this dashboard), otherwise in a frame.
public sealed class WidgetPolicy(string region) : IRunicHostedWindowPolicy
{
    public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Owner is not null || !request.CanEmbed
            ? RunicHostedWindowDecision.Refuse("The dashboard shows each widget only in its own region.")
            // CanEmbedComponent: region-component/1 is in use, which the widget lists only for a host it allows. A
            // component needs the widget's component build too, and the page's Content-Security-Policy (Program.cs);
            // without them the region shows its fallback content.
            : request.CanEmbedComponent
                ? RunicHostedWindowDecision.EmbedComponent(region)
                : RunicHostedWindowDecision.Embed(region));
}

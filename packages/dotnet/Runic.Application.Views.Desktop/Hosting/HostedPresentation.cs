using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// A guest's presentation request applied to its hosted window (host-protocol.md, Presentation), as a platform outcome.
// The requests a Window's presentation also makes go through the window's DesktopWindowPresentation, which presents
// with the host's activation rights; the others call the DesktopWindow and report what it throws as outcomes.
internal static class HostedPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    internal static async Task<PlatformResult<PlatformUnit>> ApplyAsync(RunicHostedWindow hosted, RunicHostedPresentationRequest request,
        CancellationToken cancellationToken)
    {
        if (hosted.Adapter is not { } adapter || hosted.PresentationOrNull is not { } window) return Closed;
        try
        {
            return request.Kind switch
            {
                RunicHostedPresentationKind.Present => await adapter.PresentAsync(null, cancellationToken).ConfigureAwait(false),
                RunicHostedPresentationKind.Minimize => await adapter.MinimizeAsync(cancellationToken).ConfigureAwait(false),
                RunicHostedPresentationKind.SetVisible => await adapter.SetVisibleAsync(request.Value ?? true, cancellationToken).ConfigureAwait(false),
                RunicHostedPresentationKind.SetFullScreen => await adapter.SetFullScreenAsync(request.Value ?? false, cancellationToken).ConfigureAwait(false),
                RunicHostedPresentationKind.SetDocumentEdited => await adapter.SetDocumentEditedAsync(request.Value ?? false, cancellationToken).ConfigureAwait(false),
                RunicHostedPresentationKind.SetColorScheme => await adapter.SetColorSchemeAsync(request.ColorScheme ?? DesktopColorScheme.NoPreference, cancellationToken).ConfigureAwait(false),
                RunicHostedPresentationKind.ToggleMaximized => await RunAsync(window, () => window.ToggleMaximizedAsync(cancellationToken)).ConfigureAwait(false),
                RunicHostedPresentationKind.Resize when request is { Width: { } width and >= 1, Height: { } height and >= 1 } =>
                    await RunAsync(window, () => window.ResizeAsync(width, height, cancellationToken)).ConfigureAwait(false),
                RunicHostedPresentationKind.Move when request is { X: { } x, Y: { } y } =>
                    await RunAsync(window, () => window.MoveAsync(x, y, cancellationToken)).ConfigureAwait(false),
                RunicHostedPresentationKind.SetRepresentedFile =>
                    await RunAsync(window, () => window.SetRepresentedFileAsync(request.Path, cancellationToken)).ConfigureAwait(false),
                RunicHostedPresentationKind.SetLayoutDirection =>
                    await RunAsync(window, () => window.SetLayoutDirectionAsync(request.LayoutDirection ?? DesktopLayoutDirection.Default, cancellationToken)).ConfigureAwait(false),
                RunicHostedPresentationKind.Announce when request.Text is { Length: > 0 } text =>
                    await RunAsync(window, () => window.AnnounceAsync(text, request.Priority ?? DesktopAnnouncementPriority.Polite, cancellationToken)).ConfigureAwait(false),
                // A policy's change that left out a member the kind needs.
                _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Closed; }
        catch (ArgumentException error)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData, new PlatformDiagnostic("Runic.Desktop", 0, error.Message));
        }
    }

    // DesktopWindow throws where a presentation reports results: a window closed meanwhile, a capability it lacks.
    internal static async Task<PlatformResult<PlatformUnit>> RunAsync(DesktopWindow window, Func<ValueTask> request)
    {
        if (!window.IsOpen) return Closed;
        try
        {
            await request().ConfigureAwait(false);
            return Done;
        }
        catch (ObjectDisposedException) { return Closed; }
        catch (DesktopException error) when (error.Code == "window-capability-unavailable") { return Unsupported; }
        catch (InvalidOperationException) when (!window.IsOpen) { return Closed; }
        catch (InvalidOperationException error)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict, new PlatformDiagnostic("Runic.Desktop", 0, error.Message));
        }
        catch (NotSupportedException) { return Unsupported; }
        catch (DesktopException error)
        {
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown, new PlatformDiagnostic("Runic.Desktop", 0, error.Code));
        }
    }
}

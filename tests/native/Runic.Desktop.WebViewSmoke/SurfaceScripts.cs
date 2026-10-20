using Runic.Desktop;

// DesktopSurface.ExecuteJavaScriptAsync takes only a cancellation token. The smokes bound each step with a time
// limit and report an elapsed limit as a TimeoutException, as they did when the method took a timeout.
internal static class SurfaceScripts
{
    public static async Task<string> ExecuteJavaScriptAsync(this DesktopSurface surface, string script, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await surface.ExecuteJavaScriptAsync(script, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The script did not return within {timeout.TotalSeconds:0.###} s.", error);
        }
    }
}

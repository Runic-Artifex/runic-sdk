using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Bridge;
using Runic.Application.Platform;
using Runic.Application.Platform.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;

internal static class HostIntegrationTests
{
    internal static async Task RunAsync()
    {
        await DispatchClosureAsync();
        var services = new ServiceCollection();
        services.AddRunicPlatform();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var files = first.ServiceProvider.GetRequiredService<IFileDialogs>();
        var clipboard = first.ServiceProvider.GetRequiredService<ITextClipboard>();
        Check(ReferenceEquals(files, first.ServiceProvider.GetRequiredService<IFileDialogs>()), "Files must share their presentation scope.");
        Check(!ReferenceEquals(files, second.ServiceProvider.GetRequiredService<IFileDialogs>()), "Presentations must not share native work.");
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured }, "Browser file picker must report unavailable.");
        Check(await clipboard.ReadTextAsync(1024) is PlatformResult<string?>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured }, "Browser clipboard must report unavailable.");
        var capabilities = first.ServiceProvider.GetRequiredService<IPlatformCapabilities>();
        var snapshot = capabilities.GetSnapshot();
        Check(snapshot.Statuses.Count == 5 && snapshot.Statuses.Values.All(value => value is CapabilityStatus.Unavailable), "Absent providers must report all capabilities unavailable.");
        Check(snapshot.Generation != second.ServiceProvider.GetRequiredService<IPlatformCapabilities>().GetSnapshot().Generation, "Presentation generations must be distinct.");
        var lifetime = first.ServiceProvider.GetRequiredService<IApplicationPresentationLifetime>();
        await Task.WhenAll(lifetime.StopAsync().AsTask(), lifetime.StopAsync().AsTask());
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed }, "Stopped scope must reject files.");
        Check(await clipboard.WriteTextAsync("") is PlatformResult<Unit>.Unavailable { Reason: UnavailableReason.OwnerClosed }, "Stopped scope must reject clipboard writes.");
        Check(await second.ServiceProvider.GetRequiredService<IFileDialogs>().OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured }, "Stopping one scope must not close another.");
        Check(!new DesktopNativeOwner(() => null).IsAvailable, "Missing Desktop owner must be unavailable.");
    }

    private static async Task DispatchClosureAsync()
    {
        foreach (var error in new Exception[] { new ObjectDisposedException("window"), new NotSupportedException(), new OperationCanceledException() })
        {
            bool current = true;
            try
            {
                await DesktopNativeOwner.DispatchVerifiedAsync((_, _) =>
                {
                    current = false;
                    return ValueTask.FromException(error);
                }, () => current, _ => throw new InvalidOperationException("Closed callback ran."), default);
                throw new InvalidOperationException("Pre-callback owner closure escaped normalization.");
            }
            catch (OwnerClosedException) { }

            current = true;
            try
            {
                await DesktopNativeOwner.DispatchVerifiedAsync((callback, _) =>
                {
                    callback(1);
                    return ValueTask.CompletedTask;
                }, () => current, _ => { current = false; throw error; }, default);
                throw new InvalidOperationException("Native callback failure was swallowed.");
            }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { }
        }

        using var canceled = new CancellationTokenSource();
        var callerError = new OperationCanceledException(canceled.Token);
        try
        {
            await DesktopNativeOwner.DispatchVerifiedAsync((_, _) =>
            {
                canceled.Cancel();
                return ValueTask.FromException(callerError);
            }, () => false, _ => { }, canceled.Token);
            throw new InvalidOperationException("Caller cancellation was swallowed.");
        }
        catch (OperationCanceledException actual) when (ReferenceEquals(actual, callerError)) { }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

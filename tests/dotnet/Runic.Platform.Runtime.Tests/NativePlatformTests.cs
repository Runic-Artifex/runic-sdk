using TUnit.Core;

// The former --native-services and --native-select modes: opt-in checks in a real desktop session. Each opens one
// Runic.Desktop window as the platform owner, so run one category per process (see README.md).
internal sealed class NativePlatformTests
{
    [Test, Explicit, Category("NativeServices"), DisplayName("native services: file handoff and notification APIs"), Timeout(600_000)]
    public async Task NativeServicesAsync(CancellationToken cancellationToken)
    {
        if (await NativePlatformSmoke.RunAsync(manualDesktopServices: true, manualSelection: false) != 0)
            throw new InvalidOperationException("The native services check failed.");
    }

    [Test, Explicit, Category("NativeSelect"), DisplayName("native select: selected-file access"), Timeout(600_000)]
    public async Task NativeSelectAsync(CancellationToken cancellationToken)
    {
        if (await NativePlatformSmoke.RunAsync(manualDesktopServices: false, manualSelection: true) != 0)
            throw new InvalidOperationException("The native selection check failed.");
    }
}

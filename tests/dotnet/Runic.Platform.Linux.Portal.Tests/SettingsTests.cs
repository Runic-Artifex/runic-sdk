using Runic.Platform.Linux.Portal;
using TUnit.Core;

internal sealed class SettingsTests
{
    // The former --settings mode: prints what the desktop portal on the current session bus reports. It asserts nothing.
    [Test, Explicit, Category("Settings"), DisplayName("settings: print the session's portal appearance")]
    public async Task ReadSessionSettingsAsync()
    {
        await using var settings = new PortalApplication().CreateSettings();
        Console.WriteLine(await settings.ReadAsync());
    }
}

using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The page scripts that Runic.Desktop compiles in as UTF-8 data (PageScripts.g.cs, eng/generate-page-scripts.mjs; the
// generator's own check is tests/engineering/page-scripts.test.mjs). The scripts embedded as they are equal their source
// files byte for byte, trailing newline included; the bridge is minified, which the engineering test covers.
public sealed class PageScriptsTests
{
    [Test]
    public void TheWindowChromeAndKeyboardScriptsAreTheirSourceFiles()
    {
        var internalDirectory = FindInternalDirectory();
        if (internalDirectory is null) return;
        Assert.Equal(File.ReadAllText(Path.Combine(internalDirectory, "MacOsWindowChrome.js")), MacOsWindowChrome.Script);
        Assert.Equal(File.ReadAllText(Path.Combine(internalDirectory, "WebKitGtkKeyboard.js")), WebKitGtkKeyboard.Script);
    }

    [Test]
    public void TheBridgeIsTheMinifiedScriptWithItsPlaceholders()
    {
        var script = WebUiBridge.Script;
        Assert.StartsWith("(()=>{\"use strict\";", script, StringComparison.Ordinal);
        // One line, ended by esbuild's final newline.
        Assert.Equal(script.Length - 1, script.IndexOf('\n', StringComparison.Ordinal));
        foreach (var placeholder in (string[])["__TOKEN__", "__PORT__", "__BASE_PATH__", "__SESSION_CREDENTIAL__",
            "__CUSTOM_WINDOW_DRAG__", "__FRAME__", "__MEASURE_HEIGHT__"])
            Assert.Contains(placeholder, script, StringComparison.Ordinal);
        Assert.Contains($"const VIEWS_PUSH=\"{WebUiBridge.ViewsPushFunction}\";", script, StringComparison.Ordinal);
        Assert.Contains($"const ANNOUNCE=\"{WebUiBridge.AnnounceFunction}\";", script, StringComparison.Ordinal);
        Assert.Contains($"const REGION=\"{WebUiBridge.RegionFunction}\";", script, StringComparison.Ordinal);
    }

    // packages/dotnet/Runic.Desktop/Internal in this checkout, or null outside one.
    private static string? FindInternalDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.slnx")))
                return Path.Combine(directory.FullName, "packages", "dotnet", "Runic.Desktop", "Internal");
        }
        return null;
    }
}

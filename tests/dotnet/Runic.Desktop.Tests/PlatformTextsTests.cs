using System.Net;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// S4 (L3-7, L3-8, L3-21): the text Runic itself shows comes from DesktopPlatformTexts.Current, which the application
// replaces at run time; the error pages take the window's language and direction.
public sealed class PlatformTextsTests
{
    [Test]
    public void TheTextsDefaultToEnglishAndAreCheckedWhenSet()
    {
        try
        {
            var defaults = DesktopPlatformTexts.Current;
            Assert.Equal("Untitled", defaults.UntitledFileName);
            Assert.Equal("Open file", defaults.OpenFileTitle);
            Assert.Null(defaults.ControlKeyName);
            DesktopPlatformTexts.Current = new DesktopPlatformTexts { UntitledFileName = "Unbenannt" };
            Assert.Equal("Unbenannt", DesktopPlatformTexts.Current.UntitledFileName);
            DesktopPlatformTexts.Current = null!;
            Assert.Equal("Untitled", DesktopPlatformTexts.Current.UntitledFileName);
            Assert.Throws<ArgumentException>(() => DesktopPlatformTexts.Current = new DesktopPlatformTexts { SaveFileTitle = " " });
            Assert.Throws<ArgumentException>(() => DesktopPlatformTexts.Current = new DesktopPlatformTexts { UntitledFileName = "a/b" });
            Assert.Throws<ArgumentException>(() => DesktopPlatformTexts.Current = new DesktopPlatformTexts { ControlKeyName = "St\0rg" });
            Assert.Throws<ArgumentException>(() => DesktopPlatformTexts.Current = new DesktopPlatformTexts { AccessDeniedTitle = new string('x', 1025) });
            Assert.Equal("Untitled", DesktopPlatformTexts.Current.UntitledFileName);
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    [Test]
    public void ShortcutLabelsTakeTheApplicationsModifierNames()
    {
        var shortcut = DesktopShortcut.Parse("Primary+Alt+Shift+S");
        try
        {
            if (!OperatingSystem.IsWindows())
                Assert.Equal("Ctrl+Alt+Shift+S", DesktopMenuPlanner.MapShortcut(shortcut, MenuPlatform.Windows).DisplayText);
            DesktopPlatformTexts.Current = new DesktopPlatformTexts { ControlKeyName = "Strg", ShiftKeyName = "Umschalt", AltKeyName = "Alt" };
            Assert.Equal("Strg+Alt+Umschalt+S", DesktopMenuPlanner.MapShortcut(shortcut, MenuPlatform.Windows).DisplayText);
            Assert.Equal("Umschalt+Strg+Alt+S", DesktopMenuPlanner.MapShortcut(shortcut, MenuPlatform.Linux).DisplayText);
            Assert.Equal("Strg+Alt+Umschalt+S‏".TrimEnd('‏'), DesktopMenuPlanner.MapShortcut(shortcut, MenuPlatform.Windows).DisplayText);
            // macOS draws its symbols; the names do not apply.
            Assert.Equal("⌥⇧⌘S", DesktopMenuPlanner.MapShortcut(shortcut, MenuPlatform.MacOS).DisplayText);
            // The Win32 label of a planned item carries them.
            var plan = DesktopMenuPlanner.PlanBar([new DesktopMenu("File", [new DesktopMenuItem("Save", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S") }])],
                MenuPlatform.Windows);
            Assert.EndsWith("\tStrg+S", plan[0].Children[0].Win32Label, StringComparison.Ordinal);
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }

    [Test]
    public async Task TheResourceUnavailablePageIsInTheWindowsLanguageAndDirection()
    {
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("<h1>page</h1>");
        using var client = new HttpClient { BaseAddress = url };
        try
        {
            var english = await client.GetAsync("missing.txt");
            Assert.Equal(HttpStatusCode.NotFound, english.StatusCode);
            var page = await english.Content.ReadAsStringAsync();
            Assert.StartsWith("<html><head><meta charset=\"utf-8\"><title>Resource Not Available</title>", page, StringComparison.Ordinal);

            DesktopPlatformTexts.Current = new DesktopPlatformTexts
            {
                ResourceUnavailableTitle = "غير متاح", ResourceUnavailableMessage = "المورد <المطلوب> غير متاح.",
            };
            window.PageLanguage = "ar-EG";
            page = await client.GetStringAsyncAllowingNotFound("missing.txt");
            Assert.StartsWith("<html lang=\"ar-EG\" dir=\"rtl\">", page, StringComparison.Ordinal);
            Assert.Contains("<title>غير متاح</title>", page, StringComparison.Ordinal);
            Assert.Contains("المورد &lt;المطلوب&gt; غير متاح.", page, StringComparison.Ordinal);
            window.PageLanguage = "de";
            Assert.StartsWith("<html lang=\"de\" dir=\"ltr\">", await client.GetStringAsyncAllowingNotFound("missing.txt"), StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => window.PageLanguage = "de\"><script>");
        }
        finally { DesktopPlatformTexts.Current = null!; }
    }
}

internal static class NotFoundPages
{
    internal static async Task<string> GetStringAsyncAllowingNotFound(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}

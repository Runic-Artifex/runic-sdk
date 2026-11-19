extern alias cswebui;
using CsWebUi;
using Runic.Platform;
using TUnit.Core;
using CsWebUiPageTexts = cswebui::Runic.Application.Views.CsWebUi.CsWebUiPageTexts;

// cs-webui#32 (T4): every text of CS-WebUI's own pages and banners comes from DesktopPlatformTexts, whose defaults are
// CS-WebUI's English. The probes' --probe-embedded-assets checks the pages and the banner script a window answers.
internal sealed class CsWebUiPageTextsChecks
{
    [Test]
    public void EveryWebUiTextComesFromThePlatformTexts()
    {
        var english = CsWebUiPageTexts.From(new DesktopPlatformTexts(), "en");
        foreach (var key in WebUiTexts.Keys)
            Require(english[key] == WebUiTexts.English[key], $"{key}: '{english[key]}' is not CS-WebUI's English '{WebUiTexts.English[key]}'.");

        var german = CsWebUiPageTexts.From(new DesktopPlatformTexts
        {
            ResourceUnavailableTitle = "1", ResourceUnavailableMessage = "2", ServerErrorTitle = "3", ServerErrorMessage = "4",
            ConnectionErrorLabel = "5", ConnectionFailedMessage = "6", WebSocketUnsupportedMessage = "7", ConnectionLostMessage = "8",
        }, "de");
        Require(german.Language == "de", $"The language is {german.Language}.");
        var values = WebUiTexts.Keys.Select(key => german[key]).Order().ToArray();
        Require(values.SequenceEqual(["1", "2", "3", "4", "5", "6", "7", "8"]), $"A WebUI text is not mapped: {string.Join(", ", values)}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

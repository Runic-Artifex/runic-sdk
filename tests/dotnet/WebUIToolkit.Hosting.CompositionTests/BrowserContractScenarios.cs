using System;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.CompositionTests;

internal static class BrowserContractScenarios
{
    public static ValueTask EnforcesSafeIdentifierGrammar()
    {
        const string validId = "app-1.window_2";
        var hostOptions = new BrowserHostOptions(validId);
        var windowOptions = new BrowserWindowOptions(validId, "Window");

        ContractAssert.Equal(validId, hostOptions.ApplicationId);
        ContractAssert.Equal(validId, windowOptions.WindowId);

        string[] invalidIds =
        [
            ".",
            "..",
            "a..b",
            "-leading",
            "leading.",
            "_leading",
            "trailing_",
            new string('a', 129),
        ];
        foreach (string invalidId in invalidIds)
        {
            ContractAssert.Throws<ArgumentException>(
                () => GC.KeepAlive(new BrowserHostOptions(invalidId)));
            ContractAssert.Throws<ArgumentException>(
                () => GC.KeepAlive(new BrowserWindowOptions(invalidId, "Window")));
        }

        return ValueTask.CompletedTask;
    }
}

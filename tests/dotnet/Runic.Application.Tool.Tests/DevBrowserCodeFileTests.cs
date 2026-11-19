using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// #1187: dotnet runic dev --browser's code file is readable only by the user who runs dev. On Windows that is an access
// list of that user alone with nothing inherited (BrowserEntry.WritePrivateFile), also after an admission rewrites it.
internal sealed partial class ToolTests
{
    // In the WindowsCommands category, which the native job's Windows x64 leg runs, the leg that BrowserEntry.cs plans.
    [Test, Category("WindowsCommands"), DisplayName("on Windows, dev --browser's code file grants only its user, also after an admission")]
    public async Task BrowserCodeFileIsPrivateOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        string codeFile = Path.Combine(workspace.Root, "obj", "runic", BrowserEntry.CodeFileName);
        BrowserEntry entry = BrowserEntry.TryStart(0, codeFile: codeFile)!;
        try
        {
            RequirePrivate(codeFile);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            using HttpResponseMessage admitted = await client.GetAsync(new Uri(BrowserEntry.CodeUrl(entry.Port, 0, entry.Code)));
            Equal(HttpStatusCode.OK, admitted.StatusCode);
            Contains(File.ReadAllText(codeFile), BrowserEntry.CodeUrl(entry.Port, 0, entry.Code));
            RequirePrivate(codeFile);
        }
        finally
        {
            await entry.DisposeAsync();
        }
        False(File.Exists(codeFile), "dev deletes the code file when it stops");
    }
}

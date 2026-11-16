using System;
using System.Collections.Generic;
using System.Linq;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary>
/// The Runic DevTools extension in the Window's WebView2 (#1044 wired into #1043): on Windows dev adds it to
/// RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS in Development, after the developer's folders, once, unless --no-devtools-extension.
/// </summary>
internal sealed partial class ToolTests
{
    private const string RunicExtension = @"C:\Users\dev\.nuget\packages\runic.application.tool\0.21.0\tools\net10.0\any\devtools-extension";

    [Test, DisplayName("dev appends the DevTools extension to the Window's extension list, once, in Development only")]
    public void DevToolsExtensionAppendsToTheWindowList()
    {
        Func<string, string?> Read(params (string Name, string? Value)[] variables) =>
            name => variables.LastOrDefault(variable => variable.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        // Development: the folder alone, or after the developer's folders.
        Equal(RunicExtension, DevToolsExtension.AppendTo(RunicExtension, Read(("DOTNET_ENVIRONMENT", "Development"))));
        Equal(@"C:\Tools\react;C:\Tools\vue;" + RunicExtension, DevToolsExtension.AppendTo(RunicExtension,
            Read(("DOTNET_ENVIRONMENT", "development"), (DevToolsExtension.WindowVariable, @" C:\Tools\react ;;C:\Tools\vue"))));
        // ASPNETCORE_ENVIRONMENT decides when DOTNET_ENVIRONMENT is unset, as the host does.
        Equal(RunicExtension, DevToolsExtension.AppendTo(RunicExtension, Read(("ASPNETCORE_ENVIRONMENT", "Development"))));
        // Listed already, in another case or with a trailing separator: the list stays as it is.
        Equal(null, DevToolsExtension.AppendTo(RunicExtension, Read(("DOTNET_ENVIRONMENT", "Development"),
            (DevToolsExtension.WindowVariable, @"C:\Tools\react;" + RunicExtension.ToUpperInvariant() + @"\"))));
        // Outside Development, nothing: the host would load none either.
        Equal(null, DevToolsExtension.AppendTo(RunicExtension, Read(("DOTNET_ENVIRONMENT", "Production"))));
        Equal(null, DevToolsExtension.AppendTo(RunicExtension, Read(("DOTNET_ENVIRONMENT", "Production"), ("ASPNETCORE_ENVIRONMENT", "Development"))));
        Equal(null, DevToolsExtension.AppendTo(RunicExtension, Read()));
    }

    [Test, DisplayName("the Window's environment gets the DevTools extension after -e and dev's own folders, in Development only")]
    public void DevToolsExtensionInTheWindowEnvironment()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        var development = new Dictionary<string, string?>();

        // dev's default DOTNET_ENVIRONMENT=Development.
        Equal(RunicExtension, HostProcessController.CreateDevelopmentEnvironment(desktop, development, static _ => null,
            devToolsExtension: RunicExtension)[DevToolsExtension.WindowVariable]);
        // -e's list wins over dev's own environment, and the extension follows it.
        Equal(@"C:\Tools\vue;" + RunicExtension, HostProcessController.CreateDevelopmentEnvironment(desktop, development,
            name => name == DevToolsExtension.WindowVariable ? @"C:\Tools\react" : null,
            [new(DevToolsExtension.WindowVariable, @"C:\Tools\vue")], devToolsExtension: RunicExtension)[DevToolsExtension.WindowVariable]);
        // dev's own environment's list, when -e sets none.
        Equal(@"C:\Tools\react;" + RunicExtension, HostProcessController.CreateDevelopmentEnvironment(desktop, development,
            name => name == DevToolsExtension.WindowVariable ? @"C:\Tools\react" : null,
            devToolsExtension: RunicExtension)[DevToolsExtension.WindowVariable]);
        // A production environment, from -e or dev's own: the variable is left alone.
        False(HostProcessController.CreateDevelopmentEnvironment(desktop, development, static _ => null,
            [new("DOTNET_ENVIRONMENT", "Production")], devToolsExtension: RunicExtension).ContainsKey(DevToolsExtension.WindowVariable),
            "dev adds the extension outside Development");
        False(HostProcessController.CreateDevelopmentEnvironment(desktop, development,
            name => name == "ASPNETCORE_ENVIRONMENT" ? "Staging" : null, devToolsExtension: RunicExtension)
            .ContainsKey(DevToolsExtension.WindowVariable), "dev adds the extension outside Development");
        // No extension for the Window: nothing.
        False(HostProcessController.CreateDevelopmentEnvironment(desktop, development, static _ => null)
            .ContainsKey(DevToolsExtension.WindowVariable), "dev sets the variable without an extension");
    }

    [Test, DisplayName("dev loads the DevTools extension only into a Windows WebView2 Window that a development server serves, unless --no-devtools-extension")]
    public void DevToolsExtensionForTheWindow()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        var options = new DevOptions(null, "Debug", true, true, true, false, []);

        Equal(RunicExtension, DevToolsExtension.ForWindow(desktop, options, RunicExtension, windows: true));
        Equal(null, DevToolsExtension.ForWindow(desktop, options, RunicExtension, windows: false));
        Equal(null, DevToolsExtension.ForWindow(desktop, options, null, windows: true));
        Equal(null, DevToolsExtension.ForWindow(desktop, options with { DevToolsExtension = false }, RunicExtension, windows: true));
        Equal(null, DevToolsExtension.ForWindow(desktop, options with { Browser = true }, RunicExtension, windows: true));
        Equal(null, DevToolsExtension.ForWindow(desktop, options with { WatchFrontend = false }, RunicExtension, windows: true));
        Equal(null, DevToolsExtension.ForWindow(desktop with { ApplicationHosts = ["cswebui"] }, options, RunicExtension, windows: true));

        // --no-window: the session file's Window environment carries it for the IDE's Window.
        var ide = options with { StartWindow = false, WindowDevToolsExtension = RunicExtension };
        True(DevSession.WindowEnvironment(desktop, ide).Any(variable =>
            variable.Key == DevToolsExtension.WindowVariable && variable.Value.EndsWith(RunicExtension, StringComparison.Ordinal)),
            "the session file's Window environment lacks the extension");
        False(DevSession.WindowEnvironment(desktop, options with { StartWindow = false }).Any(variable =>
            variable.Key == DevToolsExtension.WindowVariable), "the session file's Window environment names an extension dev does not add");
    }

    [Test, DisplayName("the --no-devtools-extension option turns the WebView2 extension off")]
    public async System.Threading.Tasks.Task NoDevToolsExtensionOption()
    {
        var console = new CapturingConsole();
        int exitCode = await Runic.Application.Tool.Program.RunAsync(["dev", "--help"], console);
        Equal(Runic.Application.Tool.Program.Success, exitCode);
        True(console.Output.Contains("--no-devtools-extension", StringComparison.Ordinal), "dev --help lacks --no-devtools-extension");

        // The --browser hint names the folder for Load unpacked and for Chromium's --load-extension.
        string hint = DevToolsExtension.BrowserHint(RunicExtension);
        True(hint.Contains("Load unpacked and pick " + RunicExtension, StringComparison.Ordinal), hint);
        True(hint.Contains("--load-extension=C:/Users/dev/.nuget/", StringComparison.Ordinal), hint);
    }
}

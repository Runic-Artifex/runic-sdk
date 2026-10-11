using System.Threading.Tasks;
using Microsoft.Testing.Platform.Builder;
using Runic.Application.Tool;

namespace Runic.Application.Tool.Tests;

/// <summary>
/// The entry point is hand-written because the cases start this executable again as a child process: the tool's
/// <see cref="ProcessTreeSignal.SelfCommand"/> relaunches it with <see cref="ProcessTreeSignal.CtrlCHelperArgument"/>
/// to raise Ctrl+C on Windows, and the stop cases launch <see cref="StopTestChild.Argument"/> stand-ins for
/// <c>dotnet watch</c> and the application. Those invocations run before the test platform parses arguments.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also serves the child-process modes.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is [ProcessTreeSignal.CtrlCHelperArgument, string processId]) return ProcessTreeSignal.RunCtrlCHelper(processId);
        if (args is [StopTestChild.Argument, string role, string markers]) return StopTestChild.Run(role, markers);
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}

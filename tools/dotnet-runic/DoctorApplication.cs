using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed record DoctorRun(DoctorProjectConfiguration Project, DoctorReport Report, DoctorTargetRid? Target = null);

internal static class DoctorApplication
{
    internal static async Task<DoctorRun> InspectAsync(
        DoctorOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string dotnetHost = ResolveDotNetHost();
        // From a solution root, the solution's one Window project (devtools-library-dx F9), as dev selects it.
        string project = await ProjectDiscovery.SelectProjectAsync(Environment.CurrentDirectory, options.Project, "doctor", dotnetHost,
            cancellationToken, writeSolution: false).ConfigureAwait(false);
        DoctorProjectConfiguration configuration = await DoctorProjectConfiguration
            .EvaluateAsync(
                dotnetHost,
                project,
                options.Configuration,
                cancellationToken,
                options.Target is null ? null : DoctorProjectConfiguration.PublishProperties(options.Target, options.Aot, options.SelfContained))
            .ConfigureAwait(false);
        DoctorReport report = await DoctorChecks
            .InspectAsync(
                configuration,
                dotnetHost,
                SystemDoctorRuntime.Instance,
                cancellationToken,
                options.Target)
            .ConfigureAwait(false);
        if (options.Bundle is { } bundle)
        {
            report = new DoctorReport([.. report.Checks,
                DoctorWorkspaceChecks.CheckWebBundle(Path.GetFullPath(bundle, Environment.CurrentDirectory), configuration.AssemblyName)]);
        }
        return new DoctorRun(configuration, report, options.Target);
    }

    internal static void WriteReport(
        DoctorProjectConfiguration project,
        DoctorReport report,
        DoctorTargetRid? target = null)
    {
        Console.WriteLine(target is null
            ? $"Runic Application doctor: {project.ProjectPath}"
            : $"Runic Application doctor: {project.ProjectPath} (target {target.Value})");
        foreach (DoctorCheck check in report.Checks)
        {
            Console.WriteLine(
                $"{StatusText(check.Status),-4} {check.Name}: {check.Message}");
            if (!string.IsNullOrWhiteSpace(check.Remediation))
            {
                Console.WriteLine($"     Fix: {check.Remediation}");
            }
        }

        Console.WriteLine(
            $"Summary: {report.Passed} passed, {report.Warnings} warnings, {report.Failed} failed.");
    }

    private static string ResolveDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

    private static string StatusText(DoctorStatus status) =>
        status switch
        {
            DoctorStatus.Pass => "PASS",
            DoctorStatus.Warning => "WARN",
            DoctorStatus.Failure => "FAIL",
            _ => throw new InvalidOperationException($"Unknown doctor status '{status}'."),
        };

}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed record DoctorRun(DoctorProjectConfiguration Project, DoctorReport Report);

internal static class DoctorApplication
{
    internal static async Task<DoctorRun> InspectAsync(
        DoctorOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
        string dotnetHost = ResolveDotNetHost();
        DoctorProjectConfiguration configuration = await DoctorProjectConfiguration
            .EvaluateAsync(
                dotnetHost,
                project,
                options.Configuration,
                cancellationToken)
            .ConfigureAwait(false);
        DoctorReport report = await DoctorChecks
            .InspectAsync(
                configuration,
                dotnetHost,
                SystemDoctorRuntime.Instance,
                cancellationToken)
            .ConfigureAwait(false);
        return new DoctorRun(configuration, report);
    }

    internal static void WriteReport(
        DoctorProjectConfiguration project,
        DoctorReport report)
    {
        Console.WriteLine($"Runic Application doctor: {project.ProjectPath}");
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

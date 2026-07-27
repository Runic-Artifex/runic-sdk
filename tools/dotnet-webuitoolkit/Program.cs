using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal static class Program
{
    internal const int Success = 0;
    internal const int DevelopmentFailure = 1;
    internal const int UsageFailure = 2;
    internal const int InternalFailure = 3;

    public static async Task<int> Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return await (
                arguments.Length > 0
                && StringComparer.Ordinal.Equals(arguments[0], "doctor")
                    ? DoctorApplication.RunAsync(arguments, CancellationToken.None)
                    : DevApplication.RunAsync(arguments, CancellationToken.None))
                .ConfigureAwait(false);
        }
        catch (DevUsageException exception)
        {
            WriteError(exception.Code, exception.Message);
            return UsageFailure;
        }
        catch (DevDevelopmentException exception)
        {
            WriteError(exception.Code, exception.Message);
            return DevelopmentFailure;
        }
        catch (JsonException exception)
        {
            WriteError("WUTDEV1003", $"MSBuild returned invalid configuration JSON: {exception.Message}");
            return InternalFailure;
        }
        catch (IOException exception)
        {
            WriteError("WUTDEV1008", exception.Message);
            return DevelopmentFailure;
        }
        catch (UnauthorizedAccessException exception)
        {
            WriteError("WUTDEV1008", exception.Message);
            return DevelopmentFailure;
        }
        catch (InvalidOperationException exception)
        {
            WriteError("WUTDEV1099", exception.Message);
            return InternalFailure;
        }
    }

    internal static void WriteError(string code, string message) =>
        Console.Error.WriteLine($"dotnet webuitoolkit: {code}: {message}");
}

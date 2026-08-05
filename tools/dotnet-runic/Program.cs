using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

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
            Task<int> command = arguments.Length > 0
                ? arguments[0] switch
                {
                    "doctor" => DoctorApplication.RunAsync(arguments, CancellationToken.None),
                    "inspect" => InspectApplication.RunAsync(arguments, CancellationToken.None),
                    _ => DevApplication.RunAsync(arguments, CancellationToken.None),
                }
                : DevApplication.RunAsync(arguments, CancellationToken.None);
            return await command.ConfigureAwait(false);
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
            WriteError("RTKDEV1003", $"MSBuild returned invalid configuration JSON: {exception.Message}");
            return InternalFailure;
        }
        catch (IOException exception)
        {
            WriteError("RTKDEV1008", exception.Message);
            return DevelopmentFailure;
        }
        catch (UnauthorizedAccessException exception)
        {
            WriteError("RTKDEV1008", exception.Message);
            return DevelopmentFailure;
        }
        catch (InvalidOperationException exception)
        {
            WriteError("RTKDEV1099", exception.Message);
            return InternalFailure;
        }
    }

    internal static void WriteError(string code, string message) =>
        Console.Error.WriteLine($"dotnet runic-toolkit: {code}: {message}");
}

using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes;

/// <summary>Runs local processes without shell interpretation.</summary>
public interface IProcessRunner
{
    /// <summary>Runs one bounded request.</summary>
    /// <param name="request">The immutable process request.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A sanitized terminal result.</returns>
    ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default);
}

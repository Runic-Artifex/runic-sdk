using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes;

/// <summary>Provides shell-free, bounded, cancellable local process execution.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int BufferSize = 81920;
    private static readonly ProcessOutput EmptyOutput = new(string.Empty, 0, false, false);
    private static readonly SearchValues<char> BatchArgumentMetacharacters =
        SearchValues.Create("%!^&|<>\"()\r\n");
    private static readonly SearchValues<char> BatchPathMetacharacters = SearchValues.Create("%!\"\r\n");
    private readonly IExecutablePolicy executablePolicy;
    private readonly IProcessObserver? observer;
    private readonly TimeProvider timeProvider;
    private readonly ProcessPlatform platform;

    /// <summary>Initializes a process runner.</summary>
    /// <param name="executablePolicy">Policy evaluated before every start.</param>
    /// <param name="observer">Optional metadata-only observer.</param>
    /// <param name="timeProvider">Time source used for result timestamps, timeouts and drain grace periods.</param>
    public ProcessRunner(
        IExecutablePolicy executablePolicy,
        IProcessObserver? observer = null,
        TimeProvider? timeProvider = null)
        : this(executablePolicy, observer, timeProvider, ProcessPlatform.Current)
    {
    }

    internal ProcessRunner(
        IExecutablePolicy executablePolicy,
        IProcessObserver? observer,
        TimeProvider? timeProvider,
        ProcessPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(executablePolicy);
        ArgumentNullException.ThrowIfNull(platform);
        this.executablePolicy = executablePolicy;
        this.observer = observer;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.platform = platform;
    }

    /// <inheritdoc />
    public async ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                ProcessState.Cancelled,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                null,
                startedAt);
        }

        ProcessRequest effectiveRequest;
        try
        {
            effectiveRequest = ResolveRequest(request);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartFailed(ProcessStartFailureCategory.InvalidRequest, startedAt);
        }

        CommandFault? batchFault = ValidateBatchFile(effectiveRequest);
        if (batchFault is not null)
        {
            return Complete(
                ProcessState.Rejected,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                batchFault,
                startedAt);
        }

        ExecutablePolicyDecision decision;
        try
        {
            decision = executablePolicy.Evaluate(effectiveRequest);
            if (decision is null || (!decision.IsAllowed && decision.Fault is null))
            {
                return Complete(
                    ProcessState.Rejected,
                    null,
                    EmptyOutput,
                    EmptyOutput,
                    null,
                    new CommandFault(
                        ProcessFaultCodes.InvalidPolicyDecision,
                        "The executable policy returned an invalid decision."),
                    startedAt);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Complete(
                ProcessState.Rejected,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                new CommandFault(
                    ProcessFaultCodes.PolicyEvaluationFailed,
                    "The executable policy could not evaluate the request."),
                startedAt);
        }

        if (!decision.IsAllowed)
        {
            return Complete(
                ProcessState.Rejected,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                NormalizePolicyFault(decision.Fault!),
                startedAt);
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateStartInfo(effectiveRequest);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartFailed(ProcessStartFailureCategory.InvalidRequest, startedAt);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                ProcessState.Cancelled,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                null,
                startedAt);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return StartFailed(ProcessStartFailureCategory.Other, startedAt);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartFailed(ClassifyStartFailure(exception), startedAt);
        }

        NotifyStarted(startedAt);

        Task<CapturedOutput> stdoutTask = DrainAsync(
            process.StandardOutput.BaseStream,
            effectiveRequest.Options.StandardOutputLimitBytes,
            effectiveRequest.Options.StandardOutputEncoding);
        Task<CapturedOutput> stderrTask = DrainAsync(
            process.StandardError.BaseStream,
            effectiveRequest.Options.StandardErrorLimitBytes,
            effectiveRequest.Options.StandardErrorEncoding);

        ProcessState state = ProcessState.Exited;
        var termination = new TerminationRequest(process);
        using CancellationTokenSource? timeout = CreateTimeout(effectiveRequest.Options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout?.Token ?? CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (HasExited(process))
            {
                state = ProcessState.Exited;
            }
            else
            {
                state = cancellationToken.IsCancellationRequested
                    ? ProcessState.Cancelled
                    : ProcessState.TimedOut;
                termination.Request();
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            state = ProcessState.ExecutionFailed;
            termination.Request();
        }

        DrainOutcome drain = state is ProcessState.Cancelled or ProcessState.TimedOut or ProcessState.ExecutionFailed
            ? await AwaitTerminationAndDrainAsync(
                process,
                stdoutTask,
                stderrTask,
                effectiveRequest.Options.DrainGracePeriod).ConfigureAwait(false)
            : await AwaitDrainAsync(
                process,
                stdoutTask,
                stderrTask,
                effectiveRequest.Options.DrainGracePeriod).ConfigureAwait(false);

        ProcessOutput standardOutput = GetOutput(stdoutTask, drain.StandardOutputTimedOut);
        ProcessOutput standardError = GetOutput(stderrTask, drain.StandardErrorTimedOut);
        int? exitCode = state == ProcessState.Exited ? TryGetExitCode(process) : null;
        ProcessStartFailureCategory? failureCategory = null;
        CommandFault? fault = state == ProcessState.ExecutionFailed
            ? new CommandFault(
                ProcessFaultCodes.ExecutionFailed,
                "The process lifecycle could not be observed safely.")
            : null;

        return Complete(
            state,
            exitCode,
            standardOutput,
            standardError,
            failureCategory,
            fault,
            startedAt);
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = request.Options.StandardOutputEncoding,
            StandardErrorEncoding = request.Options.StandardErrorEncoding,
        };

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        for (int index = 0; index < request.Arguments.Count; index++)
        {
            startInfo.ArgumentList.Add(request.Arguments[index]);
        }

        foreach (System.Collections.Generic.KeyValuePair<string, string?> pair in request.Environment)
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private ProcessRequest ResolveRequest(ProcessRequest request)
    {
        string resolvedFileName = ResolveExecutable(request);
        if (string.Equals(resolvedFileName, request.FileName, StringComparison.Ordinal))
        {
            return request;
        }

        return new ProcessRequest(
            resolvedFileName,
            request.Arguments,
            request.WorkingDirectory,
            request.Environment,
            request.Options);
    }

    private string ResolveExecutable(ProcessRequest request)
    {
        if (Path.IsPathFullyQualified(request.FileName))
        {
            return Path.GetFullPath(request.FileName);
        }

        if (request.FileName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            request.FileName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Path.GetFullPath(request.FileName);
        }

        string? pathValue = GetEffectiveEnvironmentValue(request, "PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return request.FileName;
        }

        string[] extensions = GetExecutableExtensions(request.FileName, request);
        string[] directories = pathValue.Split(
            platform.PathListSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
        {
            for (int extensionIndex = 0; extensionIndex < extensions.Length; extensionIndex++)
            {
                string candidate = Path.GetFullPath(
                    Path.Combine(directories[directoryIndex], request.FileName + extensions[extensionIndex]));
                if (platform.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return request.FileName;
    }

    private string[] GetExecutableExtensions(string fileName, ProcessRequest request)
    {
        if (!platform.IsWindows || Path.HasExtension(fileName))
        {
            return [string.Empty];
        }

        string? pathExtensions = GetEffectiveEnvironmentValue(request, "PATHEXT");
        return string.IsNullOrWhiteSpace(pathExtensions)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExtensions.Split(
                platform.PathListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string? GetEffectiveEnvironmentValue(ProcessRequest request, string name)
    {
        StringComparison comparison = platform.IsWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (System.Collections.Generic.KeyValuePair<string, string?> pair in request.Environment)
        {
            if (string.Equals(pair.Key, name, comparison))
            {
                return pair.Value;
            }
        }

        return platform.GetEnvironmentVariable(name);
    }

    // Windows starts .bat and .cmd files through cmd.exe, which re-parses the command line and
    // interprets metacharacters even inside the quotes ArgumentList adds around a token.
    private CommandFault? ValidateBatchFile(ProcessRequest request)
    {
        if (!platform.IsWindows || !IsBatchFile(request.FileName))
        {
            return null;
        }

        if (!request.Options.AllowWindowsBatchFiles)
        {
            return new CommandFault(
                ProcessFaultCodes.BatchFileRejected,
                "Windows batch files run through cmd.exe and require AllowWindowsBatchFiles.");
        }

        if (request.FileName.AsSpan().ContainsAny(BatchPathMetacharacters))
        {
            return new CommandFault(
                ProcessFaultCodes.BatchFileArgumentRejected,
                "The batch file path contains characters that cmd.exe would interpret.");
        }

        for (int index = 0; index < request.Arguments.Count; index++)
        {
            if (request.Arguments[index].AsSpan().ContainsAny(BatchArgumentMetacharacters))
            {
                return new CommandFault(
                    ProcessFaultCodes.BatchFileArgumentRejected,
                    "A batch file argument contains characters that cmd.exe would interpret.",
                    new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["argumentIndex"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }
        }

        return null;
    }

    private static bool IsBatchFile(string fileName)
    {
        // Windows ignores trailing dots and spaces, so "tool.cmd." starts tool.cmd.
        string extension = Path.GetExtension(fileName.TrimEnd('.', ' '));
        return string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CapturedOutput> DrainAsync(Stream stream, int limit, Encoding encoding)
    {
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] retained = limit == 0 ? Array.Empty<byte>() : new byte[limit];
        int retainedCount = 0;
        long observed = 0;

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                observed = SaturatingAdd(observed, read);
                int toCopy = Math.Min(read, retained.Length - retainedCount);
                if (toCopy > 0)
                {
                    readBuffer.AsSpan(0, toCopy).CopyTo(retained.AsSpan(retainedCount));
                    retainedCount += toCopy;
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Stream teardown may race bounded post-termination cleanup. Retained bytes remain valid.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }

        string text;
        try
        {
            text = encoding.GetString(retained, 0, retainedCount);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            text = string.Empty;
        }

        return new CapturedOutput(text, observed, observed > limit);
    }

    private async Task<DrainOutcome> AwaitTerminationAndDrainAsync(
        Process process,
        Task<CapturedOutput> stdoutTask,
        Task<CapturedOutput> stderrTask,
        TimeSpan gracePeriod)
    {
        Task all = Task.WhenAll(
            IgnoreFailureAsync(process.WaitForExitAsync(CancellationToken.None)),
            IgnoreFailureAsync(stdoutTask),
            IgnoreFailureAsync(stderrTask));
        try
        {
            await all.WaitAsync(gracePeriod, timeProvider).ConfigureAwait(false);
            return default;
        }
        catch (TimeoutException)
        {
            return await ClosePipesAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
        }
    }

    private async Task<DrainOutcome> AwaitDrainAsync(
        Process process,
        Task<CapturedOutput> stdoutTask,
        Task<CapturedOutput> stderrTask,
        TimeSpan gracePeriod)
    {
        Task drains = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            await drains.WaitAsync(gracePeriod, timeProvider).ConfigureAwait(false);
            return default;
        }
        catch (TimeoutException)
        {
            // A descendant that inherited a pipe can hold it open after the child exits.
            return await ClosePipesAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
        }
    }

    private async Task<DrainOutcome> ClosePipesAsync(
        Process process,
        Task<CapturedOutput> stdoutTask,
        Task<CapturedOutput> stderrTask)
    {
        var outcome = new DrainOutcome(!stdoutTask.IsCompleted, !stderrTask.IsCompleted);
        TryClose(process.StandardOutput.BaseStream);
        TryClose(process.StandardError.BaseStream);

        Task drains = Task.WhenAll(stdoutTask, stderrTask);
        await Task.WhenAny(drains, Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider)).ConfigureAwait(false);
        return outcome;
    }

    private static void TryClose(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static ProcessOutput GetOutput(Task<CapturedOutput> task, bool drainTimedOut)
    {
        if (task.Status != TaskStatus.RanToCompletion)
        {
            return drainTimedOut ? new ProcessOutput(string.Empty, 0, false, true) : EmptyOutput;
        }

        CapturedOutput capture = task.Result;
        return new ProcessOutput(capture.Text, capture.ObservedBytes, capture.IsTruncated, drainTimedOut);
    }

    private CancellationTokenSource? CreateTimeout(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, timeProvider);

    private ProcessResult StartFailed(ProcessStartFailureCategory category, DateTimeOffset startedAt) =>
        Complete(
            ProcessState.StartFailed,
            null,
            EmptyOutput,
            EmptyOutput,
            category,
            CreateStartFault(category),
            startedAt);

    private ProcessResult Complete(
        ProcessState state,
        int? exitCode,
        ProcessOutput standardOutput,
        ProcessOutput standardError,
        ProcessStartFailureCategory? failureCategory,
        CommandFault? fault,
        DateTimeOffset startedAt)
    {
        DateTimeOffset endedAt = timeProvider.GetUtcNow();
        NotifyCompleted(state, endedAt - startedAt);
        return new ProcessResult(
            state,
            exitCode,
            standardOutput,
            standardError,
            failureCategory,
            fault,
            startedAt,
            endedAt);
    }

    private static ProcessStartFailureCategory ClassifyStartFailure(Exception exception) => exception switch
    {
        System.ComponentModel.Win32Exception windowsException when windowsException.NativeErrorCode is 2 or 3 =>
            ProcessStartFailureCategory.NotFound,
        System.ComponentModel.Win32Exception windowsException when windowsException.NativeErrorCode is 5 =>
            ProcessStartFailureCategory.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => ProcessStartFailureCategory.NotFound,
        UnauthorizedAccessException => ProcessStartFailureCategory.AccessDenied,
        ArgumentException or InvalidOperationException => ProcessStartFailureCategory.InvalidRequest,
        PlatformNotSupportedException or NotSupportedException => ProcessStartFailureCategory.Unsupported,
        _ => ProcessStartFailureCategory.Other,
    };

    private static CommandFault CreateStartFault(ProcessStartFailureCategory category) =>
        new(
            ProcessFaultCodes.StartFailed,
            "The process could not be started.",
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["category"] = category.ToString(),
            });

    private static CommandFault NormalizePolicyFault(CommandFault fault)
    {
        string code = fault.Code == ProcessFaultCodes.WorkingDirectoryRejected
            ? ProcessFaultCodes.WorkingDirectoryRejected
            : ProcessFaultCodes.ExecutableRejected;
        return new CommandFault(
            code,
            "The process request is not permitted by the configured policy.");
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private void NotifyStarted(DateTimeOffset startedAt)
    {
        try
        {
            observer?.OnStarted(startedAt);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private void NotifyCompleted(ProcessState state, TimeSpan duration)
    {
        try
        {
            observer?.OnCompleted(state, duration);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static long SaturatingAdd(long current, int value) =>
        current > long.MaxValue - value ? long.MaxValue : current + value;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private readonly record struct CapturedOutput(string Text, long ObservedBytes, bool IsTruncated);

    private readonly record struct DrainOutcome(bool StandardOutputTimedOut, bool StandardErrorTimedOut);

    private sealed class TerminationRequest
    {
        private readonly Process process;
        private int requested;

        internal TerminationRequest(Process process)
        {
            this.process = process;
        }

        internal void Request()
        {
            if (Interlocked.Exchange(ref requested, 1) != 0)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (PlatformNotSupportedException)
            {
                TryKillSingleProcess();
            }
            catch (NotSupportedException)
            {
                TryKillSingleProcess();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }

        private void TryKillSingleProcess()
        {
            try
            {
                process.Kill();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }
    }
}

internal sealed class ProcessPlatform
{
    internal static readonly ProcessPlatform Current = new(
        OperatingSystem.IsWindows(),
        File.Exists,
        Environment.GetEnvironmentVariable);

    internal ProcessPlatform(
        bool isWindows,
        Func<string, bool> fileExists,
        Func<string, string?> getEnvironmentVariable)
    {
        IsWindows = isWindows;
        FileExists = fileExists;
        GetEnvironmentVariable = getEnvironmentVariable;
    }

    internal bool IsWindows { get; }

    internal Func<string, bool> FileExists { get; }

    internal Func<string, string?> GetEnvironmentVariable { get; }

    internal char PathListSeparator => IsWindows ? ';' : ':';
}

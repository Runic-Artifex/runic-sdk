using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RunicCommandLine.Processes;

namespace RunicCommandLine.Processes.Tests;

internal static class Program
{
    private static readonly string[] OriginalArgument = ["original"];
    private static readonly string[] RejectedCompletion = ["completed:Rejected"];
    private static readonly string[] StartFailedCompletion = ["completed:StartFailed"];
    private static readonly string[] CancelledCompletion = ["completed:Cancelled"];

    private static readonly (string Name, Func<Task> Test)[] Tests =
    [
        ("argument tokens are preserved without shell interpretation", ArgumentTokensArePreservedAsync),
        ("request defensively copies argument tokens", RequestDefensivelyCopiesArgumentsAsync),
        ("hostile policy rejection is normalized and prevents process start", PolicyRejectionPreventsStartAsync),
        ("missing executable is a sanitized start failure", MissingExecutableIsStartFailureAsync),
        ("success exit is reported", SuccessExitIsReportedAsync),
        ("nonzero exit is reported without reclassification", NonzeroExitIsReportedAsync),
        ("simultaneous stdout and stderr pressure is drained", SimultaneousPressureIsDrainedAsync),
        ("both output caps retain bounded bytes and count all observed bytes", BothOutputCapsAreEnforcedAsync),
        ("zero output caps still drain both channels", ZeroOutputCapsStillDrainAsync),
        ("output cap configuration rejects invalid bounds", OutputCapConfigurationRejectsInvalidBoundsAsync),
        ("timeout terminates the child and is distinct", TimeoutIsDistinctAsync),
        ("external cancellation terminates the child and is distinct", ExternalCancellationIsDistinctAsync),
        ("pre-cancellation does not start a child", PreCancellationDoesNotStartAsync),
        ("cancellation during policy evaluation prevents start", CancellationDuringPolicyPreventsStartAsync),
        ("cancellation and process-exit races have one terminal result", CancellationExitRaceIsStableAsync),
        ("timeout and process-exit races have one terminal result", TimeoutExitRaceIsStableAsync),
        ("caller cancellation wins over a later timeout", CallerCancellationWinsAsync),
        ("timeout wins over later caller cancellation", TimeoutWinsAsync),
        ("process-tree timeout terminates descendants", ProcessTreeIsTerminatedAsync),
        ("timestamps and duration bracket execution", TimestampsBracketExecutionAsync),
        ("observer notifications are ordered and exactly once", ObserverIsOrderedAndExactlyOnceAsync),
        ("terminal-only paths notify completion exactly once", TerminalOnlyPathsNotifyExactlyOnceAsync),
        ("observer exceptions do not corrupt process completion", ObserverExceptionsAreContainedAsync),
    ];

    public static async Task<int> Main(string[] args)
    {
        if (ChildProcessFixture.IsChildInvocation(args))
        {
            return await ChildProcessFixture.RunAsync(args).ConfigureAwait(false);
        }

        var failures = 0;
        foreach ((string name, Func<Task> test) in Tests)
        {
            try
            {
                await test().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"Executed {Tests.Length} process tests; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task ArgumentTokensArePreservedAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"wut-cli-shell-{Guid.NewGuid():N}.txt");
        string[] payload =
        [
            "contains spaces",
            "embedded\"quote",
            string.Empty,
            "Grüße-東京-🙂",
            "-leading-hyphen",
            $">{markerPath}",
            $"$(touch {markerPath})",
            $"& echo shell > {markerPath}",
        ];

        ProcessResult result = await RunChildAsync("echo-arguments", payload).ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.SequenceEqual(payload, DecodeArgumentEcho(result.StandardOutput.Text));
        TestAssert.False(File.Exists(markerPath), "A shell metacharacter argument created a marker file.");
        TestAssert.Equal(string.Empty, result.StandardError.Text);
    }

    private static async Task RequestDefensivelyCopiesArgumentsAsync()
    {
        string[] mutable = ChildProcessFixture.CreateArguments("echo-arguments", "original");
        var request = new ProcessRequest(ChildProcessFixture.ExecutablePath, mutable);
        mutable[^1] = "mutated";

        ProcessResult result = await CreateRunner().RunAsync(request).ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.SequenceEqual(OriginalArgument, DecodeArgumentEcho(result.StandardOutput.Text));
    }

    private static async Task PolicyRejectionPreventsStartAsync()
    {
        var policy = new RecordingRejectPolicy();
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(policy, observer);

        ProcessResult result = await runner.RunAsync(CreateChildRequest("sleep", "1")).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.Rejected, result.State);
        TestAssert.Null(result.ExitCode);
        TestAssert.Equal(1, policy.EvaluationCount);
        TestAssert.NotNull(result.Fault);
        TestAssert.Equal(ProcessFaultCodes.ExecutableRejected, result.Fault!.Code);
        TestAssert.Equal("The process request is not permitted by the configured policy.", result.Fault.Message);
        TestAssert.False(result.Fault.Code.Contains("secret", StringComparison.OrdinalIgnoreCase));
        TestAssert.False(result.Fault.Message.Contains("secret", StringComparison.Ordinal));
        TestAssert.Equal(0, result.Fault.Details.Count);
        TestAssert.SequenceEqual(RejectedCompletion, observer.Events);
    }

    private static async Task MissingExecutableIsStartFailureAsync()
    {
        var observer = new RecordingObserver();
        string missing = Path.Combine(Path.GetTempPath(), $"wut-cli-missing-{Guid.NewGuid():N}");

        ProcessResult result = await CreateRunner(observer).RunAsync(new ProcessRequest(missing)).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.StartFailed, result.State);
        TestAssert.Null(result.ExitCode);
        TestAssert.Equal(ProcessStartFailureCategory.NotFound, result.StartFailureCategory);
        TestAssert.NotNull(result.Fault);
        TestAssert.Equal(ProcessFaultCodes.StartFailed, result.Fault!.Code);
        TestAssert.SequenceEqual(StartFailedCompletion, observer.Events);
    }

    private static async Task SuccessExitIsReportedAsync()
    {
        ProcessResult result = await RunChildAsync("pressure", "5", "7", "0").ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.Equal("OOOOO", result.StandardOutput.Text);
        TestAssert.Equal("EEEEEEE", result.StandardError.Text);
    }

    private static async Task NonzeroExitIsReportedAsync()
    {
        ProcessResult result = await RunChildAsync("pressure", "0", "0", "23").ConfigureAwait(false);

        AssertExited(result, 23);
        TestAssert.Null(result.Fault);
    }

    private static async Task SimultaneousPressureIsDrainedAsync()
    {
        const int byteCount = 512 * 1024;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            standardOutputLimitBytes: byteCount,
            standardErrorLimitBytes: byteCount);

        ProcessResult result = await RunChildAsync(
            options,
            "pressure",
            byteCount.ToString(CultureInfo.InvariantCulture),
            byteCount.ToString(CultureInfo.InvariantCulture),
            "0",
            "1024").ConfigureAwait(false);

        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, byteCount, byteCount, 'O', truncated: false);
        AssertOutput(result.StandardError, byteCount, byteCount, 'E', truncated: false);
    }

    private static async Task BothOutputCapsAreEnforcedAsync()
    {
        const int observedOutput = 300_123;
        const int observedError = 400_321;
        const int retainedOutput = 1_003;
        const int retainedError = 2_007;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            standardOutputLimitBytes: retainedOutput,
            standardErrorLimitBytes: retainedError);

        ProcessResult result = await RunChildAsync(
            options,
            "pressure",
            observedOutput.ToString(CultureInfo.InvariantCulture),
            observedError.ToString(CultureInfo.InvariantCulture),
            "0").ConfigureAwait(false);

        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, observedOutput, retainedOutput, 'O', truncated: true);
        AssertOutput(result.StandardError, observedError, retainedError, 'E', truncated: true);
    }

    private static async Task ZeroOutputCapsStillDrainAsync()
    {
        const int observed = 200_000;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            standardOutputLimitBytes: 0,
            standardErrorLimitBytes: 0);

        ProcessResult result = await RunChildAsync(
            options,
            "pressure",
            observed.ToString(CultureInfo.InvariantCulture),
            observed.ToString(CultureInfo.InvariantCulture),
            "0").ConfigureAwait(false);

        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, observed, 0, 'O', truncated: true);
        AssertOutput(result.StandardError, observed, 0, 'E', truncated: true);
    }

    private static Task OutputCapConfigurationRejectsInvalidBoundsAsync()
    {
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(standardOutputLimitBytes: -1));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(standardErrorLimitBytes: -1));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(
                standardOutputLimitBytes: ProcessExecutionOptions.MaximumOutputLimitBytes + 1));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(
                standardErrorLimitBytes: ProcessExecutionOptions.MaximumOutputLimitBytes + 1));
        return Task.CompletedTask;
    }

    private static async Task TimeoutIsDistinctAsync()
    {
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromMilliseconds(150),
            drainGracePeriod: TimeSpan.FromSeconds(2));

        ProcessResult result = await RunChildAsync(options, "sleep", "5000").ConfigureAwait(false);

        AssertTerminated(result, ProcessState.TimedOut);
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(4), "Timeout did not bound execution.");
    }

    private static async Task ExternalCancellationIsDistinctAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        ProcessResult result = await CreateRunner()
            .RunAsync(CreateChildRequest("sleep", "5000"), cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.Cancelled);
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(4), "Cancellation did not bound execution.");
    }

    private static async Task PreCancellationDoesNotStartAsync()
    {
        var observer = new RecordingObserver();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        ProcessResult result = await CreateRunner(observer)
            .RunAsync(CreateChildRequest("sleep", "1"), cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.Cancelled);
        TestAssert.SequenceEqual(CancelledCompletion, observer.Events);
    }

    private static async Task CancellationDuringPolicyPreventsStartAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var policy = new BlockingAllowPolicy();
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(policy, observer);

        Task<ProcessResult> execution = Task.Run(async () =>
            await runner.RunAsync(CreateChildRequest("sleep", "5"), cancellation.Token).ConfigureAwait(false));
        TestAssert.True(policy.WaitUntilEntered(TimeSpan.FromSeconds(10)), "Policy evaluation did not begin.");
        cancellation.Cancel();
        policy.Release();

        ProcessResult result = await execution.ConfigureAwait(false);
        TestAssert.Equal(ProcessState.Cancelled, result.State);
        AssertObserverExactlyOnce(observer, ProcessState.Cancelled, expectsStart: false);
    }

    private static async Task CancellationExitRaceIsStableAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var observer = new RecordingObserver();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            ProcessResult result = await CreateRunner(observer)
                .RunAsync(CreateChildRequest("sleep", "80", "7"), cancellation.Token)
                .ConfigureAwait(false);

            TestAssert.True(
                result.State is ProcessState.Exited or ProcessState.Cancelled,
                $"Unexpected race state {result.State}.");
            AssertExitCodeMatchesState(result, 7);
            AssertObserverExactlyOnce(observer, result.State, expectsStart: true);
        }
    }

    private static async Task TimeoutExitRaceIsStableAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var observer = new RecordingObserver();
            var options = new ProcessExecutionOptions(timeout: TimeSpan.FromMilliseconds(80));
            ProcessResult result = await CreateRunner(observer)
                .RunAsync(CreateChildRequest(options, "sleep", "80", "9"))
                .ConfigureAwait(false);

            TestAssert.True(
                result.State is ProcessState.Exited or ProcessState.TimedOut,
                $"Unexpected race state {result.State}.");
            AssertExitCodeMatchesState(result, 9);
            AssertObserverExactlyOnce(observer, result.State, expectsStart: true);
        }
    }

    private static async Task CallerCancellationWinsAsync()
    {
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        ProcessResult result = await CreateRunner()
            .RunAsync(CreateChildRequest(options, "sleep", "5000"), cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.Cancelled);
    }

    private static async Task TimeoutWinsAsync()
    {
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        ProcessResult result = await CreateRunner()
            .RunAsync(CreateChildRequest(options, "sleep", "5000"), cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.TimedOut);
    }

    private static async Task ProcessTreeIsTerminatedAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"wut-cli-tree-{Guid.NewGuid():N}.txt");
        string startedMarkerPath = string.Concat(markerPath, ".started");
        int? descendantProcessId = null;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            drainGracePeriod: TimeSpan.FromSeconds(2));

        try
        {
            ProcessResult result = await RunChildAsync(
                options,
                "tree-parent",
                markerPath,
                "30000").ConfigureAwait(false);

            AssertTerminated(result, ProcessState.TimedOut);
            TestAssert.True(File.Exists(startedMarkerPath), "The descendant was not started before timeout.");
            descendantProcessId = int.Parse(
                await File.ReadAllTextAsync(startedMarkerPath).ConfigureAwait(false),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            TestAssert.True(
                await WaitForProcessExitAsync(descendantProcessId.Value, TimeSpan.FromSeconds(3)).ConfigureAwait(false),
                "A descendant remained alive after process-tree termination.");
            TestAssert.False(File.Exists(markerPath), "A descendant survived process-tree termination.");
        }
        finally
        {
            if (descendantProcessId is not null)
            {
                TryKillProcess(descendantProcessId.Value);
            }

            File.Delete(markerPath);
            File.Delete(startedMarkerPath);
        }
    }

    private static async Task TimestampsBracketExecutionAsync()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        ProcessResult result = await RunChildAsync("sleep", "100").ConfigureAwait(false);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        AssertExited(result, 0);
        TestAssert.True(result.StartedAt >= before);
        TestAssert.True(result.EndedAt <= after);
        TestAssert.True(result.EndedAt >= result.StartedAt);
        TestAssert.Equal(result.EndedAt - result.StartedAt, result.Duration);
        TestAssert.True(result.Duration >= TimeSpan.FromMilliseconds(50));
    }

    private static async Task ObserverIsOrderedAndExactlyOnceAsync()
    {
        var observer = new RecordingObserver();
        ProcessResult result = await CreateRunner(observer)
            .RunAsync(CreateChildRequest("sleep", "20"))
            .ConfigureAwait(false);

        AssertExited(result, 0);
        AssertObserverExactlyOnce(observer, ProcessState.Exited, expectsStart: true);
        TestAssert.Equal(result.StartedAt, observer.StartedAt);
        TestAssert.Equal(result.Duration, observer.CompletedDuration);
    }

    private static async Task TerminalOnlyPathsNotifyExactlyOnceAsync()
    {
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(new RecordingRejectPolicy(), observer);

        ProcessResult result = await runner.RunAsync(CreateChildRequest("sleep", "1")).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.Rejected, result.State);
        AssertObserverExactlyOnce(observer, ProcessState.Rejected, expectsStart: false);
    }

    private static async Task ObserverExceptionsAreContainedAsync()
    {
        var observer = new ThrowingObserver();
        ProcessResult result = await CreateRunner(observer)
            .RunAsync(CreateChildRequest("sleep", "1"))
            .ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.Equal(1, observer.StartedCount);
        TestAssert.Equal(1, observer.CompletedCount);
    }

    private static ProcessRunner CreateRunner(IProcessObserver? observer = null) =>
        new(new LocalExecutablePolicy(), observer);

    private static ProcessRequest CreateChildRequest(string mode, params string[] arguments) =>
        new(ChildProcessFixture.ExecutablePath, ChildProcessFixture.CreateArguments(mode, arguments));

    private static ProcessRequest CreateChildRequest(
        ProcessExecutionOptions options,
        string mode,
        params string[] arguments) =>
        new(
            ChildProcessFixture.ExecutablePath,
            ChildProcessFixture.CreateArguments(mode, arguments),
            options: options);

    private static async Task<ProcessResult> RunChildAsync(string mode, params string[] arguments) =>
        await CreateRunner().RunAsync(CreateChildRequest(mode, arguments)).ConfigureAwait(false);

    private static async Task<ProcessResult> RunChildAsync(
        ProcessExecutionOptions options,
        string mode,
        params string[] arguments) =>
        await CreateRunner().RunAsync(CreateChildRequest(options, mode, arguments)).ConfigureAwait(false);

    private static string[] DecodeArgumentEcho(string output)
    {
        using var reader = new StringReader(output);
        int count = int.Parse(reader.ReadLine()!, NumberStyles.None, CultureInfo.InvariantCulture);
        var arguments = new string[count];

        for (var index = 0; index < arguments.Length; index++)
        {
            string encoded = reader.ReadLine()
                ?? throw new InvalidOperationException("The argument echo ended early.");
            arguments[index] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }

        TestAssert.Null(reader.ReadLine());
        return arguments;
    }

    private static void AssertExited(ProcessResult result, int expectedExitCode)
    {
        TestAssert.Equal(ProcessState.Exited, result.State);
        TestAssert.Equal<int?>(expectedExitCode, result.ExitCode);
        TestAssert.Null(result.StartFailureCategory);
        TestAssert.Null(result.Fault);
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void AssertTerminated(ProcessResult result, ProcessState expectedState)
    {
        TestAssert.Equal(expectedState, result.State);
        TestAssert.Null(result.ExitCode);
        TestAssert.Null(result.StartFailureCategory);
    }

    private static void AssertOutput(
        ProcessOutput output,
        long expectedObservedBytes,
        int expectedRetainedBytes,
        char expectedCharacter,
        bool truncated)
    {
        TestAssert.Equal(expectedObservedBytes, output.ObservedByteCount);
        TestAssert.Equal(expectedRetainedBytes, Encoding.UTF8.GetByteCount(output.Text));
        TestAssert.True(output.Text.All(character => character == expectedCharacter));
        TestAssert.Equal(truncated, output.IsTruncated);
    }

    private static void AssertExitCodeMatchesState(ProcessResult result, int expectedExitCode)
    {
        if (result.State == ProcessState.Exited)
        {
            TestAssert.Equal<int?>(expectedExitCode, result.ExitCode);
        }
        else
        {
            TestAssert.Null(result.ExitCode);
        }
    }

    private static void AssertObserverExactlyOnce(
        RecordingObserver observer,
        ProcessState state,
        bool expectsStart)
    {
        string[] expected = expectsStart
            ? ["started", $"completed:{state}"]
            : [$"completed:{state}"];
        TestAssert.SequenceEqual(expected, observer.Events);
        TestAssert.Equal(expectsStart ? 1 : 0, observer.StartedCount);
        TestAssert.Equal(1, observer.CompletedCount);
    }

    private sealed class RecordingRejectPolicy : IExecutablePolicy
    {
        public int EvaluationCount { get; private set; }

        public ExecutablePolicyDecision Evaluate(ProcessRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            EvaluationCount++;
            return ExecutablePolicyDecision.Reject(
                "secret-policy-code",
                "secret\u001b[31m\r\npolicy detail");
        }
    }

    private sealed class BlockingAllowPolicy : IExecutablePolicy, IDisposable
    {
        private readonly ManualResetEventSlim entered = new(false);
        private readonly ManualResetEventSlim release = new(false);

        public ExecutablePolicyDecision Evaluate(ProcessRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            entered.Set();
            release.Wait();
            return ExecutablePolicyDecision.Allow();
        }

        public bool WaitUntilEntered(TimeSpan timeout) => entered.Wait(timeout);

        public void Release() => release.Set();

        public void Dispose()
        {
            entered.Dispose();
            release.Dispose();
        }
    }

    private sealed class RecordingObserver : IProcessObserver
    {
        private readonly List<string> events = [];

        public IReadOnlyList<string> Events => events;

        public int StartedCount { get; private set; }

        public int CompletedCount { get; private set; }

        public DateTimeOffset StartedAt { get; private set; }

        public TimeSpan CompletedDuration { get; private set; }

        public void OnStarted(DateTimeOffset startedAt)
        {
            StartedCount++;
            StartedAt = startedAt;
            events.Add("started");
        }

        public void OnCompleted(ProcessState state, TimeSpan duration)
        {
            CompletedCount++;
            CompletedDuration = duration;
            events.Add($"completed:{state}");
        }
    }

    private sealed class ThrowingObserver : IProcessObserver
    {
        public int StartedCount { get; private set; }

        public int CompletedCount { get; private set; }

        public void OnStarted(DateTimeOffset startedAt)
        {
            StartedCount++;
            throw new InvalidOperationException("observer start failure");
        }

        public void OnCompleted(ProcessState state, TimeSpan duration)
        {
            CompletedCount++;
            throw new InvalidOperationException("observer completion failure");
        }
    }
}

// A recorded single retry for flake-prone tests (eng/ci/README.md, "Recorded retries"; B1 of D077.25). Compiled into a
// test project by RunicFlakyTests=true (eng/build/flaky-tests.targets); internal to each project.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

/// <summary>Why a test may be retried once: the only four kinds of test the retry is for.</summary>
enum RetryReason
{
    /// <summary>A real window manager, focus, drag or dialog.</summary>
    Gui,
    /// <summary>What a real screen reader says.</summary>
    Speech,
    /// <summary>A real browser or WebView starting and connecting.</summary>
    BrowserConnect,
    /// <summary>A wait on real time: a dev server, watcher, debugger or editor settling.</summary>
    RealClock,
}

/// <summary>
/// Retries a failed test once, and records the retry: a line in the file that <c>RUNIC_RETRIED_TESTS</c> names,
/// which the job reports as a warning and uploads as the <c>flaky-tests-…</c> artifact that <c>eng/ci/flakes.mjs</c>
/// counts as a sighting. Without that variable nothing is retried, so no retry goes unrecorded. Opt in by class or
/// method, with the reason; a security probe or a budget check is never retried, whatever it is marked with.
/// </summary>
sealed partial class RecordedRetryAttribute(RetryReason reason) : RetryAttribute(1)
{
    /// <summary>The variable that names the record file; the CI job sets it (.github/actions/flaky-tests).</summary>
    public const string RecordVariable = "RUNIC_RETRIED_TESTS";

    /// <summary>The variable that names the CI leg (an OS and architecture, a screen reader) in the record.</summary>
    public const string LegVariable = "RUNIC_RETRIED_TESTS_LEG";

    /// <summary>Why this test may be retried.</summary>
    public RetryReason Reason { get; } = reason;

    static readonly Lock RecordLock = new();

    /// <summary>
    /// Names of security probes and budget checks: such a failure is the finding, never a flake to retry. A test or
    /// class whose name or category matches is not retried even if marked.
    /// </summary>
    [GeneratedRegex("Security|Admission|Admit|Redact|Origin|Token|Secret|Sandbox|Csp|ContentSecurity|Preflight|Credential|Permission|Elevat|Privilege|Budget|Size|Allocat|Memory",
        RegexOptions.CultureInvariant)]
    public static partial Regex Excluded();

    /// <inheritdoc />
    public override async Task<bool> ShouldRetry(TestContext context, Exception exception, int currentRetryCount)
    {
        var path = Environment.GetEnvironmentVariable(RecordVariable);
        if (string.IsNullOrEmpty(path) || currentRetryCount > 1 || IsExcluded(context.Metadata.TestDetails))
            return false;
        if (!await base.ShouldRetry(context, exception, currentRetryCount))
            return false;
        Record(path, context, exception, currentRetryCount);
        return true;
    }

    /// <summary>Whether a test is a security probe or a budget check by its name, class or category.</summary>
    public static bool IsExcluded(TestDetails details) =>
        Excluded().IsMatch(details.MethodName) || Excluded().IsMatch(details.ClassType.Name)
        || details.Categories.Any(category => Excluded().IsMatch(category));

    void Record(string path, TestContext context, Exception exception, int attempt)
    {
        var details = context.Metadata.TestDetails;
        var failure = exception is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerExceptions[0] : exception;
        var message = failure.Message.Split('\n', 2)[0].Trim();
        // Utf8JsonWriter, not JsonSerializer: the NativeAOT test hosts have no reflection-based serialization.
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("test", context.Metadata.DisplayName);
            json.WriteString("method", details.MethodName);
            json.WriteString("class", details.ClassType.FullName);
            json.WriteString("assembly", details.ClassType.Assembly.GetName().Name);
            json.WriteString("reason", Reason.ToString());
            json.WriteString("leg", Environment.GetEnvironmentVariable(LegVariable));
            json.WriteString("exception", $"{failure.GetType().Name}: {message}");
            json.WriteNumber("attempt", attempt);
            json.WriteString("at", DateTimeOffset.UtcNow.ToString("O"));
            json.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(buffer.ToArray());
        lock (RecordLock)
            File.AppendAllText(path, line + "\n");
        Console.Error.WriteLine($"Retrying {context.Metadata.DisplayName} once after {failure.GetType().Name}: {message}");
    }
}

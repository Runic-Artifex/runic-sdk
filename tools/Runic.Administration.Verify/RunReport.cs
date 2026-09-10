using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.Platform.Administration.Windows;

internal sealed record CheckResult(string Name, string Status, double Seconds, string Detail, string? ErrorType,
    string? Operation, string? Category, string? NativeDomain, int? NativeCode, string? StackTrace);
internal sealed record ReportDocument(string RunId, string Machine, string Os, string LibraryVersion, string Suite,
    bool ChangesEnabled, string Backend, string? Server, string? Domain, string? BaseDn, DateTimeOffset Started, DateTimeOffset? Finished,
    List<CheckResult> Results, List<string> Resources);
[JsonSerializable(typeof(ReportDocument))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class ReportJson : JsonSerializerContext { }

internal sealed class RunReport
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Prefix => "RunicVerify-" + Id;
    internal string Folder { get; }
    internal List<CheckResult> Results { get; } = [];
    private readonly ReportDocument _document;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    internal RunReport(Options options)
    {
        Folder = Path.Combine(options.Output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Id);
        Directory.CreateDirectory(Folder);
        _document = new(Id, Environment.MachineName, Environment.OSVersion.ToString(),
            typeof(WindowsAdministrationException).Assembly.GetName().Version?.ToString() ?? "unknown",
            options.Suite, options.Changes, options.Backend, options.Server, options.Domain, options.BaseDn, DateTimeOffset.UtcNow, null, Results, []);
        Save();
    }
    internal async Task Check(string name, Func<Task> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) { Record(name, "CANCELED", detail: "Not started."); return; }
        var start = _watch.Elapsed;
        Console.WriteLine("RUN  " + name);
        try { await action(); Record(name, "PASS", seconds: (_watch.Elapsed - start).TotalSeconds); }
        catch (OperationCanceledException error) { Record(name, "CANCELED", error, seconds: (_watch.Elapsed - start).TotalSeconds); }
        catch (Exception error) { Record(name, "FAIL", error, seconds: (_watch.Elapsed - start).TotalSeconds); }
    }
    internal void Skip(string name, string reason) => Record(name, "SKIP", detail: reason);
    internal void Resource(string description) { _document.Resources.Add(description); Save(); }
    internal void Record(string name, string status, Exception? error = null, string detail = "", double seconds = 0)
    {
        var native = error as WindowsAdministrationException;
        Results.Add(new(name, status, seconds, error?.Message ?? detail, error?.GetType().FullName,
            native?.Operation, native?.Category.ToString(), native?.NativeErrorDomain.ToString(), native?.NativeErrorCode, error?.StackTrace));
        Console.WriteLine($"{status,-8} {name}" + (error is null ? "" : ": " + error.Message));
        Save();
    }
    internal void Finish() => Save(_document with { Finished = DateTimeOffset.UtcNow });
    private void Save(ReportDocument? document = null)
    {
        var data = document ?? _document;
        var json = Path.Combine(Folder, "report.json");
        File.WriteAllText(json + ".tmp", JsonSerializer.Serialize(data, ReportJson.Default.ReportDocument));
        File.Move(json + ".tmp", json, true);
        var text = new StringBuilder($"Runic administration verification {Id}\nMachine: {data.Machine}; OS: {data.Os}\nSuite: {data.Suite}; changes: {data.ChangesEnabled}; shares/firewall backend: {data.Backend}\n\n");
        foreach (var result in Results)
        {
            text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{result.Status,-8} {result.Name} ({result.Seconds:F2}s) {result.Detail}");
            if (result.NativeCode is { } code) text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {result.Operation}: {result.Category}; {result.NativeDomain} 0x{code:X8}");
        }
        text.AppendLine("\nResource journal (attempted/created resources; cleanup outcomes are listed above):");
        foreach (var resource in data.Resources) text.AppendLine(resource);
        File.WriteAllText(Path.Combine(Folder, "report.txt"), text.ToString());
    }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

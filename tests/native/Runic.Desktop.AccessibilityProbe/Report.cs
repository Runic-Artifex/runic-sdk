using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class Report(string host, bool survey)
{
    private readonly JsonArray _checks = [];
    private int _failures;

    internal string Api { get; set; } = "";

    /// <summary>The screen reader that ran (--screen-reader), or null.</summary>
    internal string? ScreenReader { get; init; }

    internal int ExitCode => _failures == 0 ? 0 : 1;

    internal void Pass(string id, string detail) => Add(id, "pass", detail, null);

    // A check the host is known to fail: recorded, and reported if it starts to pass.
    internal void Gap(string id, string detail, string reason) => Add(id, "known-gap", detail, reason);

    internal void Fail(string id, string detail)
    {
        Add(id, survey ? "fail (survey)" : "fail", detail, null);
        if (!survey) _failures++;
    }

    internal void NotApplicable(string id, string reason) => Add(id, "not-applicable", "", reason);

    private void Add(string id, string outcome, string detail, string? reason)
    {
        Console.WriteLine($"{outcome.ToUpperInvariant(),-14} {id}: {detail}{(reason is null ? "" : $" [{reason}]")}");
        _checks.Add(new JsonObject { ["id"] = id, ["outcome"] = outcome, ["detail"] = detail, ["reason"] = reason });
    }

    internal void Write(string path)
    {
        var receipt = new JsonObject
        {
            ["host"] = host,
            ["api"] = Api,
            ["screenReader"] = ScreenReader,
            ["os"] = Environment.OSVersion.ToString(),
            ["survey"] = survey,
            ["failures"] = _failures,
            ["checks"] = _checks,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;
using Runic.CommandLine.Spectre;
using Spectre.Console;

namespace Runic.CommandLine.Tests;

internal static class ApplicationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("app/generated-completion-command-is-not-intercepted", OwnedCompletion),
        new("app/generated-string-default-and-prefix-output", DefaultAndPrefix),
        new("app/generated-negative-nullable-enum-and-list", TypedValues),
        new("app/help-path-and-groups-use-metadata", Help),
        new("app/json-handler-output-and-input-are-isolated", JsonIsolation),
        new("app/internal-exceptions-are-observable-and-sanitized", Exceptions),
        new("app/scope-configuration-errors-follow-the-execution-contract", ScopeErrors),
        new("app/environment-fallback-and-explicit-precedence", Environment),
        new("app/spectre-literal-text-and-byte-frame", Spectre),
        new("app/custom-values-bind-once-and-validate", CustomValues),
        new("app/record-human-output-uses-fields", RecordOutput),
        new("app/nullable-decimal-and-enum-defaults", NullableDefaults),
        new("app/global-options-work-before-and-after-command", GlobalOptions),
    ];

    private static CommandApp App(ICommandConsole console, Action<Exception>? observer = null, ParseSettings? settings = null) => new(GeneratedCommandCatalog.Create())
    {
        Name = "sample", Version = "1.2.3", Console = console, HandleCancelKeyPress = false,
        ParseSettings = settings ?? ParseSettings.Default, ExceptionObserver = observer,
    };

    private static async ValueTask OwnedCompletion()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["completion", "bash"]));
        AssertEx.Equal("Application completion: bash\n", console.StandardOutput);
    }

    private static async ValueTask DefaultAndPrefix()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync([]));
        AssertEx.Equal("Hello world\n", console.StandardOutput);
        console = new();
        AssertEx.Equal(0, await App(console).RunAsync(["--output", "json", "greet", "Ada"]));
        using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("Hello Ada", frame.RootElement.GetProperty("payload").GetString());
    }
    private static async ValueTask TypedValues()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["values", "--count", "-3", "--mode", "fast", "--number", "-2", "--number", "5"]));
        AssertEx.Equal("-3:Fast:-2,5\n", console.StandardOutput);
        console = new();
        AssertEx.Equal(2, await App(console).RunAsync(["values", "--count", "secret-invalid"]));
        AssertEx.True(console.StandardError.Contains("count", StringComparison.Ordinal));
        AssertEx.True(!console.StandardError.Contains("secret-invalid", StringComparison.Ordinal));
    }
    private static async ValueTask Help()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["help", "config", "show"]));
        AssertEx.True(console.StandardOutput.Contains("Display configuration", StringComparison.Ordinal));
        console = new();
        AssertEx.Equal(0, await App(console).RunAsync(["config"]));
        AssertEx.True(console.StandardOutput.Contains("show", StringComparison.Ordinal));
    }
    private static async ValueTask JsonIsolation()
    {
        var console = new TestCommandConsole { IsInteractive = true, IsInputRedirected = false };
        console.QueueInput("must not consume");
        AssertEx.Equal(0, await App(console).RunAsync(["noisy", "--output=json"]));
        using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.True(frame.RootElement.GetProperty("success").GetBoolean());
        AssertEx.Equal("Working\n", console.StandardError);
        AssertEx.Equal("must not consume", await console.ReadLineAsync(default));
    }
    private static async ValueTask Exceptions()
    {
        var console = new TestCommandConsole();
        Exception? captured = null;
        AssertEx.Equal(70, await App(console, exception => captured = exception).RunAsync(["fail", "--output=json"]));
        AssertEx.True(captured is InvalidOperationException);
        AssertEx.True(!console.StandardOutput.Contains("internal-secret", StringComparison.Ordinal));
    }
    private static async ValueTask ScopeErrors()
    {
        var console = new TestCommandConsole();
        Exception? captured = null;
        var app = new CommandApp(GeneratedCommandCatalog.Create())
        {
            Console = console, ParseSettings = ParseSettings.Default, HandleCancelKeyPress = false,
            CreateScopeFactory = _ => throw new InvalidOperationException("scope-secret"),
            ExceptionObserver = exception => captured = exception,
        };
        AssertEx.Equal(70, await app.RunAsync(["greet", "--output=json"]));
        AssertEx.True(captured is InvalidOperationException);
        using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.True(!console.StandardOutput.Contains("scope-secret", StringComparison.Ordinal));
    }
    private static async ValueTask Environment()
    {
        var settings = new ParseSettings { GetEnvironmentVariable = _ => "environment" };
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, settings: settings).RunAsync(["env"]));
        AssertEx.Equal("environment\n", console.StandardOutput);
        console = new();
        AssertEx.Equal(0, await App(console, settings: settings).RunAsync(["env", "--name", "explicit"]));
        AssertEx.Equal("explicit\n", console.StandardOutput);
    }
    private static async ValueTask NullableDefaults()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["optional"]));
        AssertEx.Equal("1.5:Fast\n", console.StandardOutput);
    }
    private static async ValueTask RecordOutput()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["report"]));
        AssertEx.Equal("Status: Ready\nCount: 2\n", console.StandardOutput);
    }
    private static async ValueTask CustomValues()
    {
        PortConverter.Calls = 0;
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["port", "--port", "8080"]));
        AssertEx.Equal(1, PortConverter.Calls);
        AssertEx.Equal("8080\n", console.StandardOutput);
        console = new();
        AssertEx.Equal(2, await App(console).RunAsync(["port", "--port", "0"]));
        AssertEx.True(console.StandardError.Contains("port", StringComparison.Ordinal));
    }
    private static ValueTask GlobalOptions()
    {
        CommandCatalog catalog = GeneratedCommandCatalog.Create(builder => builder.GlobalOption("verbose", "--verbose", CommandArity.Zero));
        foreach (string[] args in new[] { new[] { "--verbose", "greet", "Ada" }, new[] { "greet", "--verbose", "Ada" } })
        {
            ParsedInvocation invocation = CommandTestInvocation.Parse(catalog, args);
            AssertEx.True(GeneratedCommandBinding.Flag(invocation, "verbose"));
            AssertEx.Equal("Ada", GeneratedCommandBinding.Argument(invocation, "name"));
        }
        ParseOutcome rootHelp = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["--verbose", "--help"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Help, rootHelp.Kind);
        AssertEx.Equal(0, rootHelp.HelpRequest!.Path.Count);
        ParseOutcome scopedHelp = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["--verbose", "help", "config", "show"], ParseSettings.Default);
        AssertEx.Equal("config show", scopedHelp.HelpRequest!.Path.ToString());
        string script = CommandCompletion.Generate(catalog, "sample", "bash");
        AssertEx.True(script.Contains("--verbose", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }
    private static async ValueTask Spectre()
    {
        var inner = new TestCommandConsole();
        var console = new SpectreCommandConsole(inner, width: 40);
        await console.WriteOutAsync("[red]literal[/]\n".AsMemory(), default);
        AssertEx.Equal("[red]literal[/]\n", inner.StandardOutput);
        await console.WriteAsync(new Table().AddColumn("Status").AddRow("Ready"));
        AssertEx.True(inner.StandardOutput.Contains("Ready", StringComparison.Ordinal));
        AssertEx.True(!inner.StandardOutput.Contains('\u001b'));
        AssertEx.Equal("fallback", await console.PromptAsync("Name?", "fallback"));
    }
}

internal enum SampleMode { Fast, Careful }
internal static class SampleCommands
{
    [Command("completion")]
    internal static string Completion([Argument] string shell) => "Application completion: " + shell;

    [Command("greet", Description = "Greet someone.", Examples = ["sample greet Ada"]), DefaultCommand]
    internal static string Greet([Argument] string name = "world") => "Hello " + name;

    [Command("values")]
    internal static string Values([Option("--count")] int? count, [Option("--mode")] SampleMode? mode,
        [Option("--number")] IReadOnlyList<int?> numbers) => $"{count}:{mode}:{string.Join(',', numbers)}";

    [Command("config show", Description = "Display configuration")]
    internal static string Show() => "config";

    [Command("noisy")]
    internal static async Task Noisy(ICommandConsole console, CancellationToken cancellationToken)
    {
        await console.WriteOutAsync("Working\n".AsMemory(), cancellationToken);
        if (console.IsInteractive || await console.ReadLineAsync(cancellationToken) is not null) throw new InvalidOperationException("Machine input was enabled.");
    }

    [Command("fail")]
    internal static void Fail() => throw new InvalidOperationException("internal-secret");

    [Command("optional")]
    internal static string Optional([Option("--price")] decimal? price = 1.5m, [Option("--mode")] SampleMode? mode = SampleMode.Fast) => $"{price?.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{mode}";

    [Command("report"), CommandResult("sample.report/1", typeof(SampleJsonContext))]
    internal static SampleReport Report() => new("Ready", 2);

    [Command("port")]
    internal static string Port([Option("--port"), ConvertWith(typeof(PortConverter)), ValidateWith(typeof(PortValidator))] PortNumber port) => port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Command("env")]
    internal static string Environment([Option("--name", EnvironmentVariable = "SAMPLE_NAME")] string name) => name;
}

internal readonly record struct PortNumber(int Value);
internal sealed class PortConverter : ICommandValueConverter<PortNumber>
{
    internal static int Calls;
    public static PortNumber Parse(string value) { Calls++; return new(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); }
}
internal sealed class PortValidator : ICommandValueValidator<PortNumber>
{
    public static bool IsValid(PortNumber value) => value.Value is > 0 and <= 65535;
}

internal sealed record SampleReport(string Status, int Count);
[System.Text.Json.Serialization.JsonSerializable(typeof(SampleReport))]
internal sealed partial class SampleJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

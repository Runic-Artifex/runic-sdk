using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// client-errors/1 (specs/application/README.md, "Page errors"): a page's error reports are logged in the category
// Runic.Application.Views.Page as untrusted input: capped, without control characters or URL queries, and at most 10
// per connection a minute.
public sealed class ClientErrorTests
{
    private const string Hello = """{"kind":"hello","protocol":4,"session":"s-1","client":{"name":"@runic-artifex/views","version":"0.16.0"},"features":["client-errors/1"]}""";
    private const string HelloWithout = """{"kind":"hello","protocol":4,"session":"s-2","client":{"name":"@runic-artifex/views","version":"0.15.0"},"features":["presented/1"]}""";

    [Test]
    public void AReportIsLoggedInThePageCategory()
    {
        using var window = new Window();
        var hello = JsonDocument.Parse(window.Transport.Request(Hello)).RootElement;
        Require(hello.GetProperty("features").EnumerateArray().Any(feature => feature.GetString() == "client-errors/1"),
            "The producer does not list client-errors/1.");

        var reply = window.Transport.Request("""
            {"kind":"client-error","source":"unhandled-rejection","name":"TypeError","message":"x is not a function\r\nFAKE 1150 entry",
             "stack":"TypeError: x\n    at load (main.js:1:2)\u0007","url":"https://app.example/index.html?token=secret#top","route":"editor","dropped":2}
            """);

        Require(reply == """{"ok":true}""", $"The report was answered {reply}.");
        var entry = window.Log.Entries.Single();
        Require(entry is { Category: "Runic.Application.Views.Page", EventId: 1150, Level: LogLevel.Error }, $"The report was logged as {entry}.");
        Require(entry.Values["Source"] as string == "unhandled-rejection" && entry.Values["ErrorName"] as string == "TypeError"
            && entry.Values["ErrorMessage"] as string == "x is not a function  FAKE 1150 entry"
            && entry.Values["PageUrl"] as string == "https://app.example/index.html" && entry.Values["Route"] as string == "editor"
            && entry.Values["Dropped"] is 2L && entry.Values["Client"] as string == "@runic-artifex/views 0.16.0"
            && entry.Values["ErrorStack"] as string == "TypeError: x\n    at load (main.js:1:2) ",
            $"The report's values were not capped and cleaned: {entry.Message}");
    }

    [Test]
    public void LongTextsAreCut()
    {
        using var window = new Window();
        window.Transport.Request(Hello);
        var message = new string('m', 5_000);
        var stack = new string('s', 20_000);
        window.Transport.Request($$"""{"kind":"client-error","source":"error","message":"{{message}}","stack":"{{stack}}"}""");
        var entry = window.Log.Entries.Single();
        Require(entry.Values["ErrorMessage"] is string { Length: 2_049 } cut && cut.EndsWith('…')
            && entry.Values["ErrorStack"] is string { Length: 8_193 }, "Overlong texts were not cut.");
        Require(entry.Values["PageUrl"] as string == "an unknown page" && entry.Values["ErrorName"] as string == "Error",
            "Absent members did not get their defaults.");
    }

    [Test]
    public void EachConnectionLogsAtMostTenAMinuteAndCountsTheRest()
    {
        var time = new FakeTimeProvider();
        using var window = new Window(time);
        window.Transport.Request(Hello);
        for (var index = 0; index < 13; index++)
            Require(window.Transport.Request($$"""{"kind":"client-error","source":"error","message":"error {{index}}"}""") == """{"ok":true}""",
                "A suppressed report was not answered ok.");
        Require(window.Log.Entries.Count(entry => entry.EventId == 1150) == 10 && window.Log.Entries.Count(entry => entry.EventId == 1151) == 1,
            "The limit was not applied once.");

        time.Advance(TimeSpan.FromMinutes(1));
        window.Transport.Request("""{"kind":"client-error","source":"error","message":"later","dropped":1}""");
        var later = window.Log.Entries.Last();
        Require(later.EventId == 1150 && later.Values["Dropped"] is 4L, $"The suppressed reports were not counted with the next one: {later.Message}");
    }

    [Test]
    public void AReportWithoutTheFeatureOrWithWrongMembersIsInvalid()
    {
        using var window = new Window();
        window.Transport.Request(HelloWithout);
        RequireInvalid(window.Transport.Request("""{"kind":"client-error","source":"error","message":"x"}"""));
        window.Transport.Request(Hello, "c-2");
        foreach (var request in new[]
                 {
                     """{"kind":"client-error","message":"x"}""",
                     """{"kind":"client-error","source":"not an identifier","message":"x"}""",
                     """{"kind":"client-error","source":"error"}""",
                     """{"kind":"client-error","source":"error","message":"x","stack":3}""",
                     """{"kind":"client-error","source":"error","message":"x","route":"Not a route"}""",
                     """{"kind":"client-error","source":"error","message":"x","dropped":-1}""",
                 })
            RequireInvalid(window.Transport.Request(request, "c-2"));
        Require(window.Log.Entries.All(entry => entry.EventId != 1150), "An invalid report was logged.");
    }

    private static void RequireInvalid(string reply)
    {
        var error = JsonDocument.Parse(reply).RootElement;
        Require(!error.GetProperty("ok").GetBoolean() && error.GetProperty("error").GetProperty("reason").GetString() == "invalid-request",
            $"The request was answered {reply}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Window : IDisposable
    {
        public Window(TimeProvider? time = null)
        {
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { LoggerFactory = Log, TimeProvider = time });
        }

        public CategoryLog Log { get; } = new();
        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }

        public void Dispose()
        {
            Session.Dispose();
            Transport.Dispose();
        }
    }

    internal sealed record Entry(string Category, int EventId, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    internal sealed class CategoryLog : ILoggerFactory
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        // The entries of the page category, in order.
        public IReadOnlyList<Entry> Entries => [.. _entries.Where(static entry => entry.Category == RunicViewsTelemetry.PageLogCategory)];

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose()
        {
        }

        private sealed class Logger(CategoryLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value)
                    : [];
                owner._entries.Enqueue(new Entry(category, eventId.Id, logLevel, formatter(state, exception), values));
            }
        }
    }
}

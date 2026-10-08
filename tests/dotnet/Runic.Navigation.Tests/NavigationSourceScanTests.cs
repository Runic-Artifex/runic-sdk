using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Runic.Navigation.Tests;

// W240-001 §4.5: every await in Runic.Navigation goes through NavigationAwait, so the engine never
// continues on the model thread or inside a turn. The engine's sources are embedded into this
// assembly (see the project file), so the scan sees every .cs file of the package.
internal static class NavigationSourceScanTests
{
    private const string ResourcePrefix = "RunicNavigationSource/";

    // Awaits that may bypass the helper, keyed by file and trimmed source line, with the number of
    // occurrences expected. An entry that no longer matches exactly fails the scan, so the list
    // can't go stale.
    private static readonly (string File, string Line, int Count, string Reason)[] Allowed =
    [
        ("NavigationAwait.cs", "try { await pending.ConfigureAwait(false); }", 1,
            "the helper itself: awaits the pending task, then hops"),
        ("NavigationAwait.cs", "try { return await pending.ConfigureAwait(false); }", 1,
            "the helper itself: awaits the pending task, then hops"),
        ("NavigationAwait.cs", "if (context.IsExecuting) await Hop();", 2,
            "the helper itself: the conditional hop"),
        ("RunicNavigator.cs", "Map<T>(await outcome.ConfigureAwait(false));", 2,
            "maps the public result for the caller; no engine work continues after it"),
        ("RunicModelContextRegistry.cs", "try { await release.ConfigureAwait(false); }", 1,
            "only logs a candidate context's shutdown failure; not navigator code"),
        ("RunicModelContextRegistry.cs", "if (dispose is not null) await dispose.DisposeAsync().ConfigureAwait(false);", 1,
            "a lease release's last step; no engine work continues after it"),
        ("RunicNavigator.cs", "_ = task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,", 1,
            "Observe: only reads the exception of a task that nobody awaits any more"),
        ("RunicNavigator.cs", "return new(task.AsTask().ContinueWith(static completed =>", 1,
            "Completion: adapts a hook's ValueTask to ValueTask<bool>; the engine awaits that through the helper"),
        // LeaveConfirmation is a guard: user code the engine calls, not engine code. It keeps the
        // caller's context so that a confirm delegate started on the UI thread stays on it.
        ("LeaveConfirmation.cs", "await dialogs.PushForResult<bool>(dialog(), cancellationToken: cancellationToken).Completion", 1,
            "LeaveConfirmation is user guard code, not engine code; it keeps the caller's context so UI-thread confirm delegates stay on the UI thread"),
        ("LeaveConfirmation.cs", "if (!await departure.ModelContext.InvokeAsync(_hasUnsavedChanges, cancellationToken)) return true;", 1,
            "LeaveConfirmation is user guard code, not engine code; it keeps the caller's context so UI-thread confirm delegates stay on the UI thread"),
        ("LeaveConfirmation.cs", "if (!await _confirm(departure, cancellationToken)) return false;", 1,
            "LeaveConfirmation is user guard code, not engine code; it keeps the caller's context so UI-thread confirm delegates stay on the UI thread"),
    ];

    private static readonly Regex Await = new(@"\bawait\b", RegexOptions.CultureInvariant);
    private static readonly Regex Routed = new(
        @"\Gawait\s+(?:this\s*\.\s*AfterUserCode\s*\(|NavigationAwait\s*\.\s*Hop\s*\(\s*\))",
        RegexOptions.CultureInvariant);
    // Task.Yield returns to the caller's SynchronizationContext, and hand-made continuations bypass the helper.
    private static readonly Regex Banned = new(
        @"\bTask\s*\.\s*Yield\b|\.\s*ContinueWith\s*[<(]|\.\s*(?:Unsafe)?OnCompleted\s*\(", RegexOptions.CultureInvariant);

    public static void Run()
    {
        ScannerSeesOnlyCode();
        EngineAwaitsGoThroughTheHelper();
    }

    private static void EngineAwaitsGoThroughTheHelper()
    {
        var assembly = typeof(NavigationSourceScanTests).Assembly;
        var names = assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
        string[] expected = ["RunicNavigator.cs", "NavigationAwait.cs", "RunicModelContext.cs", "RunicModelContextRegistry.cs"];
        foreach (var file in expected)
        {
            Require(names.Contains(ResourcePrefix + file), $"The source scan must cover {file}; embedded: {string.Join(", ", names)}.");
        }

        var found = new Dictionary<(string, string), int>();
        var violations = new List<string>();
        foreach (var name in names)
        {
            var file = name[ResourcePrefix.Length..];
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var source = reader.ReadToEnd();
            foreach (var (line, text) in Scan(source))
            {
                var key = (file, text);
                if (Allowed.Any(entry => entry.File == file && entry.Line == text))
                    found[key] = found.GetValueOrDefault(key) + 1;
                else
                    violations.Add($"{file}:{line}: {text}");
            }
        }

        Require(violations.Count == 0,
            "Awaits in Runic.Navigation must use 'await this.AfterUserCode(...)' or 'await NavigationAwait.Hop()' " +
            "(W240-001 §4.5), and Task.Yield, ContinueWith and OnCompleted are banned:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        foreach (var entry in Allowed)
        {
            var count = found.GetValueOrDefault((entry.File, entry.Line));
            Require(count == entry.Count,
                $"The allow-listed await '{entry.Line}' in {entry.File} ({entry.Reason}) occurs {count} times, not {entry.Count}; update the allow-list.");
        }
    }

    // The scanner ignores comments and literals, raw and interpolated raw strings included, and reports
    // bypassing awaits, await using, await foreach, Task.Yield, ContinueWith and OnCompleted.
    private static void ScannerSeesOnlyCode()
    {
        const string sample = """"
            // await x;
            /* await y; */
            var a = "await z;" + @"await ""q""" + 'a';
            var b = $"{a} await";
            await this.AfterUserCode(task);
            await NavigationAwait.Hop();
            await task.ConfigureAwait(false);
            await using var scope = Create();
            await foreach (var item in items) { }
            await Task.Yield();
            await this.Other(task);
            var c = $$"""
                await {{a}} "await"
                """;
            _ = task.ContinueWith(_ => { });
            awaiter.OnCompleted(next);
            awaiter.UnsafeOnCompleted(next);
            """";
        var hits = Scan(sample).Select(hit => hit.Line).ToArray();
        Require(hits.SequenceEqual([7, 8, 9, 10, 11, 15, 16, 17]), $"The source scanner reported lines [{string.Join(", ", hits)}].");
    }

    private static IEnumerable<(int Line, string Text)> Scan(string source)
    {
        var code = StripCommentsAndLiterals(source);
        var lines = source.Split('\n');
        var reported = new HashSet<int>();
        foreach (Match match in Await.Matches(code))
        {
            if (Routed.IsMatch(code, match.Index)) continue;
            var line = LineOf(code, match.Index);
            if (reported.Add(line)) yield return (line, lines[line - 1].Trim());
        }
        foreach (Match match in Banned.Matches(code))
        {
            var line = LineOf(code, match.Index);
            if (reported.Add(line)) yield return (line, lines[line - 1].Trim());
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    // Replaces comments, string and character literals with spaces, keeping line breaks so
    // positions map back to source lines. Interpolated strings are blanked whole, holes included;
    // the engine has no awaits inside them.
    private static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        void Blank(int end)
        {
            for (; i < end && i < source.Length; i++) output.Append(source[i] == '\n' ? '\n' : ' ');
        }
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = source.IndexOf('\n', i);
                Blank(end < 0 ? source.Length : end);
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Blank(end < 0 ? source.Length : end + 2);
            }
            else if (c == '\'')
            {
                var end = i + 1;
                while (end < source.Length && source[end] != '\'') end += source[end] == '\\' ? 2 : 1;
                Blank(end + 1);
            }
            else if (c == '"' || (c is '@' or '$' && StringPrefixEnd(source, i) is var quote && quote < source.Length && source[quote] == '"'))
            {
                Blank(EndOfString(source, i));
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }

    // The index after a run of string prefixes ($, $$, @, $@, @$ and so on).
    private static int StringPrefixEnd(string source, int start)
    {
        var i = start;
        while (i < source.Length && source[i] is '@' or '$') i++;
        return i;
    }

    private static int EndOfString(string source, int start)
    {
        var i = start;
        var verbatim = false;
        while (source[i] != '"')
        {
            verbatim |= source[i] == '@';
            i++;
        }
        var quotes = 0;
        while (i + quotes < source.Length && source[i + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            var close = source.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? source.Length : close + quotes;
        }
        if (quotes == 2 && !verbatim) return i + 2;
        i++;
        while (i < source.Length)
        {
            if (verbatim && source[i] == '"')
            {
                if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            if (!verbatim && source[i] == '\\') { i += 2; continue; }
            if (!verbatim && source[i] == '"') return i + 1;
            i++;
        }
        return source.Length;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

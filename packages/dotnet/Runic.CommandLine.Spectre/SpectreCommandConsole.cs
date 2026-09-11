using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Runic.CommandLine.Spectre;

/// <summary>Renders human output with Spectre.Console while preserving exact machine frames.</summary>
public sealed class SpectreCommandConsole : ICommandConsole
{
    private readonly ICommandConsole _inner;
    private readonly bool _color;
    private readonly int _width;
    private readonly bool _unicode;

    /// <summary>Initializes a presenter with captured terminal capabilities.</summary>
    public SpectreCommandConsole(ICommandConsole? inner = null, int? width = null, bool? color = null, bool? unicode = null)
    {
        if (width is < 20) throw new ArgumentOutOfRangeException(nameof(width));
        _inner = inner ?? new SystemCommandConsole();
        _width = width ?? TerminalWidth(_inner);
        _unicode = unicode ?? (Environment.GetEnvironmentVariable("TERM") != "dumb" && System.Console.OutputEncoding.CodePage is 65001 or 1200 or 1201);
        _color = color ?? (!_inner.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null && Environment.GetEnvironmentVariable("TERM") != "dumb");
    }
    private static int TerminalWidth(ICommandConsole console)
    {
        if (console.IsOutputRedirected) return 100;
        try { return System.Console.WindowWidth >= 20 ? System.Console.WindowWidth : 100; }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException) { return 100; }
    }

    /// <inheritdoc />
    public bool IsInteractive => _inner.IsInteractive;
    /// <inheritdoc />
    public bool IsInputRedirected => _inner.IsInputRedirected;
    /// <inheritdoc />
    public bool IsOutputRedirected => _inner.IsOutputRedirected;
    /// <inheritdoc />
    public bool IsErrorRedirected => _inner.IsErrorRedirected;
    /// <inheritdoc />
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => _inner.ReadLineAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => _inner.WriteOutBytesAsync(value, cancellationToken);
    /// <inheritdoc />
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        IsOutputRedirected
            ? _inner.WriteOutAsync(value, cancellationToken)
            : WriteAsync(new Text(value.ToString()), false, cancellationToken);
    /// <inheritdoc />
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        IsErrorRedirected
            ? _inner.WriteErrorAsync(value, cancellationToken)
            : WriteAsync(new Text(value.ToString(), new Style(foreground: Color.Red)), true, cancellationToken);

    /// <summary>Renders a table, tree, panel, or other Spectre component to an invocation-local stream.</summary>
    public ValueTask WriteAsync(IRenderable value, bool standardError = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = _color && !(standardError && IsErrorRedirected) ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ColorSystemSupport.Standard,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(buffer),
        });
        console.Profile.Width = _width;
        console.Profile.Capabilities.Unicode = _unicode;
        console.Write(value);
        return standardError ? _inner.WriteErrorAsync(buffer.ToString().AsMemory(), cancellationToken) : _inner.WriteOutAsync(buffer.ToString().AsMemory(), cancellationToken);
    }

    /// <summary>Runs work with terminal progress. Redirected or noninteractive invocations emit one plain status line.</summary>
    public async Task WithProgressAsync(string description, Func<IProgress<double>, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsInteractive || !_color || IsErrorRedirected)
        {
            await _inner.WriteErrorAsync((description + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await operation(new InlineProgress(static _ => { }), cancellationToken).ConfigureAwait(false);
            return;
        }
        using var writer = new ConsoleWriter(_inner);
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = _width;
        console.Profile.Capabilities.Unicode = _unicode;
        await console.Progress().AutoClear(true).StartAsync(async progress =>
        {
            ProgressTask task = progress.AddTask(Markup.Escape(description));
            await operation(new InlineProgress(value => task.Value = Math.Clamp(value, 0, 100)), cancellationToken).ConfigureAwait(false);
            task.Value = 100;
        }).ConfigureAwait(false);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
    private sealed class ConsoleWriter(ICommandConsole console) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(string? value)
        {
            if (value is not null) console.WriteErrorAsync(value.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        public override void Write(char value) => Write(value.ToString());
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    /// <summary>Asks for a line only when interaction is explicitly available; otherwise returns the supplied fallback.</summary>
    public async ValueTask<string?> PromptAsync(string prompt, string? fallback = null, CancellationToken cancellationToken = default)
    {
        if (!IsInteractive) return fallback;
        await WriteAsync(new Text(prompt + " ", new Style(foreground: Color.Cyan)), true, cancellationToken).ConfigureAwait(false);
        return await ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? fallback;
    }
}

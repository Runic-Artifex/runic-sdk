using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.CommandLine;

/// <summary>Dispatches semantic command responses to human or machine presentation.</summary>
public sealed class CommandOutputDispatcher : ICommandOutcomeSink
{
    /// <inheritdoc />
    public ValueTask WriteAsync<T>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<T> outcome,
        ICommandResultCodec<T> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string path = context.Path.Count == 0 ? command.Name : context.Path.ToString();
        CommandResponse<T> response = CommandResponse.FromOutcome(
            context.CorrelationId,
            path,
            exitCode,
            codec.PayloadType,
            outcome,
            diagnostics);

        return DispatchAsync(
            context.OutputMode,
            context.Console,
            context.Culture,
            response,
            codec,
            cancellationToken);
    }

    /// <summary>Writes a response through the supplied console using the selected output mode.</summary>
    public static ValueTask DispatchAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(codec);

        return mode switch
        {
            CommandOutputMode.Json => CommandJsonEnvelopeWriter.WriteAsync(
                console,
                response,
                codec,
                cancellationToken),
            CommandOutputMode.Human => WriteHumanAsync(
                console,
                culture,
                response,
                codec,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static ValueTask WriteHumanAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken)
    {
        if (response.Success)
        {
            return codec.WriteHumanAsync(response.Payload!, console, culture, cancellationToken);
        }

        var text = new StringBuilder();
        foreach (CommandDiagnostic diagnostic in response.Diagnostics)
        {
            text.Append(diagnostic.Code);
            text.Append(": ");
            text.Append(CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message)
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredText(diagnostic.Message));
            text.Append('\n');
        }

        if (text.Length == 0 ||
            !string.Equals(response.Diagnostics[^1].Message, response.Fault!.Message, StringComparison.Ordinal))
        {
            CommandFault fault = CommandFaultSanitizer.Sanitize(response.Fault!);
            text.Append(fault.Code);
            text.Append(": ");
            text.Append(fault.Message);
            text.Append('\n');
        }

        return console.WriteErrorAsync(text.ToString().AsMemory(), cancellationToken);
    }
}

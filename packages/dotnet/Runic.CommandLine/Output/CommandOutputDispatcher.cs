using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

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

        return context.OutputMode == CommandOutputMode.Human &&
            !outcome.IsSuccess &&
            outcome.HumanOutput is { Length: > 0 } humanOutput
            ? DispatchFailureHumanOutputAsync(
                context.Console,
                context.Culture,
                response,
                codec,
                humanOutput,
                cancellationToken)
            : DispatchAsync(
                context.OutputMode,
                context.Console,
                context.Culture,
                response,
                codec,
                cancellationToken);
    }

    private static async ValueTask DispatchFailureHumanOutputAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        string humanOutput,
        CancellationToken cancellationToken)
    {
        await console.WriteOutAsync(humanOutput.AsMemory(), cancellationToken).ConfigureAwait(false);
        await DispatchAsync(
            CommandOutputMode.Human,
            console,
            culture,
            response,
            codec,
            cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask WriteHumanAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken)
    {
        if (response.Success)
        {
            await WriteDiagnosticsAsync(console, response.Diagnostics, cancellationToken).ConfigureAwait(false);
            await codec.WriteHumanAsync(response.Payload!, console, culture, cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = new StringBuilder(BuildDiagnostics(response.Diagnostics));

        CommandFault fault = CommandFaultSanitizer.Sanitize(response.Fault!);
        if (!ContainsFaultDiagnostic(response.Diagnostics, fault))
        {
            text.Append(fault.Code);
            text.Append(": ");
            text.Append(fault.Message);
            text.Append('\n');
        }

        await console.WriteErrorAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask WriteDiagnosticsAsync(
        ICommandConsole console,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string text = BuildDiagnostics(diagnostics);
        return text.Length == 0
            ? ValueTask.CompletedTask
            : console.WriteErrorAsync(text.AsMemory(), cancellationToken);
    }

    private static string BuildDiagnostics(IReadOnlyList<CommandDiagnostic> diagnostics)
    {
        var text = new StringBuilder();
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            text.Append(diagnostic.Code);
            text.Append(": ");
            text.Append(CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message)
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredText(diagnostic.Message));
            text.Append('\n');
        }

        return text.ToString();
    }

    private static bool ContainsFaultDiagnostic(
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CommandFault fault)
    {
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            string message = CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message)
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredText(diagnostic.Message);
            if (string.Equals(diagnostic.Code, fault.Code, StringComparison.Ordinal) &&
                string.Equals(message, fault.Message, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

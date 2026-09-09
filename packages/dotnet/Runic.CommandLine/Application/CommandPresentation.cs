using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Shared framework presentation for standalone and hosted commands.</summary>
public sealed class CommandPresentation
{
    /// <summary>Gets the application name shown in help.</summary>
    public string Name { get; init; } = "app";
    /// <summary>Gets the application version.</summary>
    public string Version { get; init; } = "0.0.0";
    /// <summary>Gets the executable name used by completion scripts.</summary>
    public string? CompletionExecutableName { get; init; }
    /// <summary>Gets the human help renderer.</summary>
    public ICommandHelpPresenter? HelpPresenter { get; init; }
    /// <summary>Gets an optional application-specific help formatter.</summary>
    public Func<CommandPath, string>? FormatHelp { get; init; }
    /// <summary>Gets the policy used for framework errors; use the executor's policy for consistency.</summary>
    public IExitCodePolicy ExitCodePolicy { get; init; } = DefaultExitCodePolicy.Instance;

    /// <summary>Gets the observer for private presentation failures. Observer failures do not change the exit code.</summary>
    public Action<Exception>? ExceptionObserver { get; init; }

    internal async ValueTask<int> GuardAsync(Func<ValueTask<int>> present, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await present().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodePolicy.GetExitCode(CommandExitCategory.Cancelled);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            try { ExceptionObserver?.Invoke(exception); }
            catch (Exception observerException) when (!IsFatal(observerException)) { }
            // Output may already be partial or broken. Do not attempt another frame/write.
            return ExitCodePolicy.GetExitCode(CommandExitCategory.HostFailure);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException or AppDomainUnloadedException or BadImageFormatException;

    internal async ValueTask<int> WriteCompletionAsync(CommandCatalog catalog, string shell, string outputOptionName, ICommandConsole console, CancellationToken cancellationToken)
    {
        string script;
        try { script = CommandCompletion.Generate(catalog, CompletionExecutableName ?? Name, shell, outputOptionName); }
        catch (ArgumentException)
        {
            await console.WriteErrorAsync("Choose a completion shell: bash, zsh, fish or powershell.\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitCodePolicy.GetExitCode(CommandExitCategory.Usage);
        }
        await console.WriteOutBytesAsync(System.Text.Encoding.UTF8.GetBytes(script), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    internal async ValueTask<int> WriteAsync(CommandCatalog catalog, ParseOutcome parsed, string outputOptionName,
        ICommandConsole console, CultureInfo culture, string requestId, CancellationToken cancellationToken)
    {
        if (parsed.Kind == ParseOutcomeKind.Help && parsed.OutputClassification?.Mode == CommandOutputMode.Human && HelpPresenter is not null && FormatHelp is null)
        {
            await HelpPresenter.WriteAsync(catalog, Name, parsed.HelpRequest!.Path, outputOptionName, console, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        CommandResponse<string> response;
        if (parsed.Kind == ParseOutcomeKind.Error)
        {
            CommandDiagnostic diagnostic = parsed.Diagnostics[0];
            int exitCode = ExitCodePolicy.GetExitCode(CommandExitCategory.Usage);
            response = CommandResponse.Failed<string>(requestId, diagnostic.Path.Count > 0 ? diagnostic.Path.ToString() : "root", exitCode,
                new CommandFault(diagnostic.Code, diagnostic.Message), parsed.Diagnostics);
        }
        else
        {
            string text = parsed.Kind == ParseOutcomeKind.Version ? Version :
                FormatHelp?.Invoke(parsed.HelpRequest!.Path) ?? CommandHelpFormatter.Format(catalog, Name, parsed.HelpRequest!.Path, outputOptionName);
            response = CommandResponse.Succeeded(requestId, parsed.Kind == ParseOutcomeKind.Version ? "version" : "help", CommandResultCodecs.String.PayloadType, text);
        }
        await CommandOutputDispatcher.DispatchAsync(parsed.OutputClassification?.Mode ?? CommandOutputMode.Human,
            console, culture, response, CommandResultCodecs.String, cancellationToken).ConfigureAwait(false);
        return response.ExitCode;
    }
}

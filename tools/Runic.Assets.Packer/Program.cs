using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Runic.Assets;
using Runic.CommandLine;
using Runic.CommandLine.Generated;

return await PackerApplication.RunAsync(args).ConfigureAwait(false);

internal static class PackerApplication
{
    private const int UsageExitCode = 2;
    private const int SourceDirectoryExitCode = 3;
    private const int EntryPointExitCode = 4;
    private const int OperationExitCode = 5;
    private const string Usage =
        "Usage: Runic.Assets.Packer <source-directory> <destination-archive> " +
        "[--entry-point <relative-path>] [--exclude <semicolon-separated-relative-paths>] " +
        "[--trusted-generated-output]";

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        CommandCatalog catalog = GeneratedCommandCatalog.Create();
        ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            args,
            ParseSettings.Default);
        var console = new SystemCommandConsole();

        if (parse.Kind == ParseOutcomeKind.Invocation && parse.Invocation is not null)
        {
            CommandExecutionResult execution = await new CommandExecutor(
                EmptyScopeFactory.Instance,
                PackerExitCodePolicy.Instance).ExecuteAsync(
                    new CommandExecutionRequest(
                        parse.Invocation,
                        console,
                        CultureInfo.InvariantCulture,
                        "runic-assets-packer"),
                    PackerOutcomeSink.Instance).ConfigureAwait(false);
            return execution.ExitCode;
        }

        if (parse.Kind == ParseOutcomeKind.Error)
        {
            int exitCode = CommandParsePresentation.GetExitCode(
                parse,
                static _ => UsageExitCode);
            await CommandParsePresentation.WriteHumanAsync(
                parse,
                console,
                static (_, _) => Usage + Environment.NewLine,
                CultureInfo.InvariantCulture).ConfigureAwait(false);
            return exitCode;
        }

        await console.WriteErrorAsync((Usage + Environment.NewLine).AsMemory(), CancellationToken.None)
            .ConfigureAwait(false);
        return UsageExitCode;
    }

    [Command("pack")]
    [DefaultCommand]
    [CommandResult("runic.assets.pack-result/1", typeof(PackerJsonContext))]
    internal static async Task<CommandOutcome<PackerResult>> PackAsync(
        [Argument("source-directory")] string sourceDirectory,
        [Argument("destination-archive")] string destination,
        [Option("--exclude")] IReadOnlyList<string> exclusions,
        [Option("--trusted-generated-output")] bool trustedGeneratedOutput,
        CancellationToken cancellationToken,
        [Option("--entry-point")] string entryPoint = "index.html")
    {
        string fullSourceDirectory = Path.GetFullPath(sourceDirectory);
        string fullDestination = Path.GetFullPath(destination);
        if (!Directory.Exists(fullSourceDirectory))
        {
            return Failure(
                CommandExitCategory.Validation,
                "RAS1001",
                $"Source directory '{fullSourceDirectory}' does not exist.");
        }

        try
        {
            entryPoint = AssetPath.Normalize(entryPoint);
            var excludedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string exclusion in exclusions)
            {
                foreach (string path in exclusion.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    excludedPaths.Add(AssetPath.Normalize(path));
                }
            }

            string? destinationDirectory = Path.GetDirectoryName(fullDestination);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            string temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            AddSourceRelativeExclusion(fullSourceDirectory, fullDestination, excludedPaths);
            AddSourceRelativeExclusion(fullSourceDirectory, temporary, excludedPaths);
            if (excludedPaths.Contains(entryPoint)
                || !File.Exists(Path.Combine(fullSourceDirectory, entryPoint.Replace('/', Path.DirectorySeparatorChar))))
            {
                return Failure(
                    CommandExitCategory.Unavailable,
                    "RAS1002",
                    $"Entry point '{entryPoint}' does not exist below '{fullSourceDirectory}' or was excluded.");
            }

            try
            {
                await using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    if (trustedGeneratedOutput)
                    {
                        await AssetArchive.WriteTrustedGeneratedOutputDirectoryAsync(
                            fullSourceDirectory,
                            output,
                            entryPoint,
                            excludedPaths,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        await AssetArchive.WriteDirectoryAsync(
                            fullSourceDirectory,
                            output,
                            entryPoint,
                            excludedPaths,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        return Failure(
                            CommandExitCategory.CommandFailure,
                            "RAS1004",
                            "Directory archive compilation requires Linux handle-pinned traversal. " +
                            "Use --trusted-generated-output only for output generated by the current build.");
                    }
                }

                File.Move(temporary, fullDestination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return CommandOutcome.Success(new PackerResult(new FileInfo(fullDestination).Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or PlatformNotSupportedException)
        {
            return Failure(CommandExitCategory.CommandFailure, "RAS1003", exception.Message);
        }
    }

    private static CommandOutcome<PackerResult> Failure(
        CommandExitCategory category,
        string code,
        string message) =>
        CommandOutcome.Failure<PackerResult>(category, new CommandFault(code, message));

    private static void AddSourceRelativeExclusion(
        string sourceDirectory,
        string candidate,
        HashSet<string> excludedPaths)
    {
        string relative = Path.GetRelativePath(sourceDirectory, candidate);
        if (relative == "."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return;
        }

        excludedPaths.Add(AssetPath.Normalize(relative.Replace(Path.DirectorySeparatorChar, '/')));
    }

    private sealed class PackerOutcomeSink : ICommandOutcomeSink
    {
        public static PackerOutcomeSink Instance { get; } = new();

        public ValueTask WriteAsync<TResult>(
            CommandDescriptor command,
            CommandExecutionContext context,
            CommandOutcome<TResult> outcome,
            ICommandResultCodec<TResult> codec,
            int exitCode,
            IReadOnlyList<CommandDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            if (outcome.IsSuccess && outcome.Value is PackerResult result)
            {
                return context.Console.WriteOutAsync(
                    $"Packed a canonical Runic Assets archive into {result.ArchiveLength} bytes.{Environment.NewLine}".AsMemory(),
                    cancellationToken);
            }

            string message = outcome.Fault?.Message ?? "The archive could not be packed.";
            return context.Console.WriteErrorAsync(
                (message + Environment.NewLine).AsMemory(),
                cancellationToken);
        }
    }

    private sealed class PackerExitCodePolicy : IExitCodePolicy
    {
        public static PackerExitCodePolicy Instance { get; } = new();

        public int GetExitCode(CommandExitCategory category) => category switch
        {
            CommandExitCategory.Success => CommandExitCodes.Success,
            CommandExitCategory.Usage => UsageExitCode,
            CommandExitCategory.Validation => SourceDirectoryExitCode,
            CommandExitCategory.Unavailable => EntryPointExitCode,
            CommandExitCategory.CommandFailure or CommandExitCategory.Cancelled or CommandExitCategory.HostFailure => OperationExitCode,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
    }

    private sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
    {
        public static EmptyScopeFactory Instance { get; } = new();

        public ICommandExecutionScope CreateScope() => EmptyScope.Instance;

        private sealed class EmptyScope : ICommandExecutionScope
        {
            public static EmptyScope Instance { get; } = new();

            public IServiceProvider Services { get; } = EmptyServices.Instance;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class EmptyServices : IServiceProvider
        {
            public static EmptyServices Instance { get; } = new();

            public object? GetService(Type serviceType) => null;
        }
    }

    private sealed class SystemCommandConsole : ICommandConsole
    {
        public bool IsInputRedirected => Console.IsInputRedirected;

        public bool IsOutputRedirected => Console.IsOutputRedirected;

        public bool IsErrorRedirected => Console.IsErrorRedirected;

        public bool IsInteractive => !IsInputRedirected && !IsOutputRedirected;

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Console.ReadLine());

        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
            new(Console.Out.WriteAsync(value, cancellationToken));

        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
            Console.OpenStandardOutput().WriteAsync(value, cancellationToken);

        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
            new(Console.Error.WriteAsync(value, cancellationToken));
    }
}

internal sealed record PackerResult(long ArchiveLength);

[JsonSerializable(typeof(PackerResult))]
internal sealed partial class PackerJsonContext : JsonSerializerContext;

using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Runic.Platform.Windows;

// Login startup through the user's Run key. Task Manager's Startup apps page records the user's choice under
// StartupApproved; an entry the user turned off stays off.
[SupportedOSPlatform("windows")]
internal sealed class WindowsLoginStartup(string applicationId,
    string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run") : IDesktopLoginStartup
{
    public ValueTask<PlatformResult<bool>> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKey);
            bool registered = run?.GetValue(applicationId) is string;
            return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Success(registered && !TurnedOff()));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Failed(PlatformFailureCode.IoError));
        }
    }

    public ValueTask<PlatformResult<PlatformUnit>> EnableAsync(LoginStartupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments.IsDefault || request.Arguments.Length > 64 || request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Supply at most 64 arguments without NUL characters.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // The user turned it off in Task Manager; only the user turns it on again.
            if (TurnedOff()) return Result(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied));
            string process = Environment.ProcessPath ?? throw new IOException("The process path is unknown.");
            var command = new List<string> { process };
            if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && Environment.GetCommandLineArgs() is [{ } assembly, ..] && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                command.Add(Path.GetFullPath(assembly));
            command.AddRange(request.Arguments);
            using var run = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            run.SetValue(applicationId, string.Join(' ', command.Select(Quote)), RegistryValueKind.String);
            return Result(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Result(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError));
        }
    }

    public ValueTask<PlatformResult<PlatformUnit>> DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            run?.DeleteValue(applicationId, throwOnMissingValue: false);
            return Result(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Result(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError));
        }
    }

    // Task Manager stores 12 bytes; an odd first byte means the user turned the entry off.
    private bool TurnedOff()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey);
        return approved?.GetValue(applicationId) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
    }

    private static ValueTask<PlatformResult<PlatformUnit>> Result(PlatformResult<PlatformUnit> result) => ValueTask.FromResult(result);

    // One argument as CommandLineToArgvW reads it back: quoted, with backslashes doubled before a quote.
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"')) return argument;
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(c);
        }
        quoted.Append('\\', backslashes * 2);
        return quoted.Append('"').ToString();
    }
}

namespace Runic.Platform.Linux.Polkit;

/// <summary>Turns pkexec's exit code and diagnostic into a result.</summary>
/// <remarks>
/// pkexec exits with the program's exit code once the program runs. Before that it exits with 126 when the user
/// dismissed the authentication dialog, and with 127 when authorization was refused or could not be obtained, or
/// pkexec failed; its English, untranslated diagnostic on standard error tells these apart. A program that exits
/// with 126 or 127, or prints pkexec's text, is reported as one of these although it ran, so an application must not
/// conclude that nothing ran for an action that is not idempotent without checking the system's state.
/// </remarks>
internal static class PkexecOutcome
{
    internal const int Dismissed = 126;
    internal const int Refused = 127;

    private const string Domain = "pkexec";

    // polkit 0.105 to 127: src/programs/pkexec.c.
    private static readonly string[] UnavailableMarkers =
    [
        "Error executing command as another user: No authentication agent found.",
        "Error creating textual authentication agent:",
        "Error registering local authentication agent:",
        "Error checking for authorization ",
        "Error getting authority:",
        "pkexec must be setuid root",
    ];

    private static readonly string[] DeniedMarkers =
    [
        "Error executing command as another user: Not authorized",
    ];

    public static PlatformResult<PrivilegedActionCompletion> Classify(int exitCode, string diagnostic)
    {
        // Newer pkexec prints "Request dismissed" with 126 and 0.105 prints nothing, so the code alone decides.
        if (exitCode == Dismissed)
            return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.UserDismissed, new(Domain, Dismissed, "Request dismissed"));
        if (exitCode != Refused)
            return new PlatformResult<PrivilegedActionCompletion>.Success(new PrivilegedActionCompletion(exitCode));
        if (Find(diagnostic, DeniedMarkers) is not null)
            return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied, new(Domain, Refused, "Not authorized"));
        if (Find(diagnostic, UnavailableMarkers) is not null)
            return new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        // Any other pkexec failure (a missing program, failing to change user) or a program that exited with 127.
        return new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new(Domain, Refused));
    }

    private static string? Find(string diagnostic, string[] markers)
    {
        foreach (var line in diagnostic.Split('\n'))
            foreach (var marker in markers)
                if (line.StartsWith(marker, StringComparison.Ordinal)) return marker;
        return null;
    }
}

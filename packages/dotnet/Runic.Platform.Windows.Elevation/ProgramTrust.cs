namespace Runic.Platform.Windows.Elevation;

internal enum ProgramCheck
{
    Trusted,
    Missing,
    /// <summary>A component is a reparse point (symbolic link, junction, mount point).</summary>
    NotRealPath,
    /// <summary>Someone other than SYSTEM, Administrators or TrustedInstaller can change a component.</summary>
    Replaceable,
    /// <summary>The helper is a .NET apphost or single-file bundle, or runtime files lie beside it: a JIT build.</summary>
    NotNativeAot,
    /// <summary>The helper's image could not be read to check what kind of build it is.</summary>
    Unreadable,
}

/// <summary>Where a path component sits relative to the helper, which decides which rights matter.</summary>
internal enum ComponentRole
{
    /// <summary>A directory above the helper's directory: renaming, deleting or re-permissioning it replaces the helper.</summary>
    Ancestor,
    /// <summary>The helper's directory: adding a file there plants a DLL the helper may load.</summary>
    Directory,
    /// <summary>The helper itself.</summary>
    Program,
}

/// <summary>
/// One access-control entry of a DACL, reduced to what the check needs. <c>Allow</c> is an access-allowed entry
/// (denied entries only remove rights and are ignored); <c>InheritOnly</c> applies only to children created later.
/// </summary>
internal readonly record struct AccessEntry(bool Allow, bool InheritOnly, uint Mask, string Sid);

/// <summary>A component of the helper's path; <c>Dacl</c> is null for a NULL DACL, which grants everyone full access.</summary>
internal sealed record PathComponent(string Path, ComponentRole Role, string OwnerSid, IReadOnlyList<AccessEntry>? Dacl);

/// <summary>Decides whether only administrators can replace the helper, from the security descriptors of its path.</summary>
/// <remarks>
/// This is a deployment check in the unprivileged application, as on Linux: it catches a helper installed where the
/// user can change it before the UAC prompt names it. It is not the boundary; the user can start the helper with
/// <c>runas</c> themselves.
/// </remarks>
internal static class ProgramTrust
{
    internal const string LocalSystem = "S-1-5-18";
    internal const string Administrators = "S-1-5-32-544";
    internal const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    // Placeholders replaced by a real SID when an object inherits the entry; on the object itself no token holds them.
    private const string CreatorOwner = "S-1-3-0", CreatorGroup = "S-1-3-1";
    // The object's owner, who must be trusted anyway.
    private const string OwnerRights = "S-1-3-4";

    private const uint WriteData = 0x2, AppendData = 0x4, WriteExtendedAttributes = 0x10, DeleteChild = 0x40, WriteAttributes = 0x100;
    private const uint Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    private const uint MaximumAllowed = 0x02000000, GenericAll = 0x10000000, GenericWrite = 0x40000000;

    /// <summary>Rights on the helper or its directory that let a holder change what runs.</summary>
    internal const uint ProgramWriteRights = WriteData | AppendData | WriteExtendedAttributes | DeleteChild | WriteAttributes
        | Delete | WriteDac | WriteOwner | MaximumAllowed | GenericAll | GenericWrite;

    /// <summary>
    /// Rights on a directory above the helper's that let a holder replace a component. Creating new entries
    /// (<c>FILE_ADD_SUBDIRECTORY</c>, which Authenticated Users have on <c>C:\</c>) does not.
    /// </summary>
    internal const uint AncestorWriteRights = DeleteChild | Delete | WriteDac | WriteOwner | MaximumAllowed | GenericAll;

    public static bool IsTrustedSid(string sid) => sid is LocalSystem or Administrators or TrustedInstaller;

    public static ProgramCheck Evaluate(IEnumerable<PathComponent> components)
    {
        foreach (var component in components)
        {
            if (!IsTrustedSid(component.OwnerSid) || component.Dacl is null) return ProgramCheck.Replaceable;
            var risky = component.Role == ComponentRole.Ancestor ? AncestorWriteRights : ProgramWriteRights;
            foreach (var entry in component.Dacl)
            {
                if (!entry.Allow || entry.InheritOnly || (entry.Mask & risky) == 0) continue;
                if (IsTrustedSid(entry.Sid) || entry.Sid is CreatorOwner or CreatorGroup or OwnerRights) continue;
                return ProgramCheck.Replaceable;
            }
        }
        return ProgramCheck.Trusted;
    }
}

/// <summary>Recognizes a JIT build of the helper before it is started, when its runtime would already run.</summary>
/// <remarks>
/// A framework-dependent or self-contained build has <c>&lt;name&gt;.dll</c>, <c>&lt;name&gt;.runtimeconfig.json</c>
/// or <c>&lt;name&gt;.deps.json</c> beside it, and its executable is the .NET apphost or single-file host, which
/// carries the bundle marker the SDK searches for when it bundles. A NativeAOT image has neither.
/// </remarks>
internal static class HelperImage
{
    // dotnet/runtime src/native/corehost/apphost/bundle_marker.c: SHA-256 of ".net core bundle". Stored
    // complemented and decoded on each use, not in a static initializer that the NativeAOT compiler could evaluate
    // ahead of time: a helper that links this check must not carry the marker itself.
    private static readonly byte[] ComplementedSignature =
    [
        0x74, 0xed, 0xfd, 0x46, 0x95, 0x9e, 0xdf, 0xc7, 0x8d, 0x84, 0x6c, 0xfd, 0xeb, 0x28, 0x5f, 0xcd,
        0xec, 0x0a, 0x46, 0x19, 0x10, 0x51, 0xcc, 0xe7, 0x11, 0xc4, 0xd2, 0x31, 0xdb, 0x4c, 0x95, 0x51,
    ];

    private static byte[] Decode(byte[] complemented)
    {
        var bytes = new byte[complemented.Length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)~complemented[i];
        return bytes;
    }

    public static ProgramCheck Check(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
                if (File.Exists(Path.Combine(directory, name + extension))) return ProgramCheck.NotNativeAot;
            return HasBundleMarker(path) ? ProgramCheck.NotNativeAot : ProgramCheck.Trusted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ProgramCheck.Unreadable;
        }
    }

    private static bool HasBundleMarker(string path)
    {
        var signature = Decode(ComplementedSignature);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        var kept = 0;
        while (true)
        {
            var read = file.Read(buffer, kept, buffer.Length - kept);
            if (read == 0) return false;
            var length = kept + read;
            if (buffer.AsSpan(0, length).IndexOf(signature.AsSpan()) >= 0) return true;
            // Keep the end, so a marker across two reads is found.
            kept = Math.Min(length, signature.Length - 1);
            buffer.AsSpan(length - kept, kept).CopyTo(buffer);
        }
    }
}

/// <summary>Where the helper of an action is on Windows.</summary>
internal static class HelperLocation
{
    private static readonly System.Buffers.SearchValues<char> Invalid = System.Buffers.SearchValues.Create("<>:\"|?*");

    /// <summary>
    /// The helper's absolute path: <paramref name="windowsProgram"/> (an absolute path or one relative to
    /// <paramref name="applicationDirectory"/>) or, without it, the file name of the declared Unix program with
    /// <c>.exe</c> in <paramref name="applicationDirectory"/>. Null when a path is not a normalized local path.
    /// </summary>
    public static string? Resolve(PrivilegedActionDeclaration action, string? windowsProgram, string applicationDirectory)
    {
        if (Normalize(applicationDirectory, requireAbsolute: true) is not { } directory) return null;
        if (windowsProgram is null)
        {
            var name = action.Program[(action.Program.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            return IsValidSegment(name) ? directory + "\\" + name : null;
        }
        if (IsDriveAbsolute(windowsProgram)) return Normalize(windowsProgram, requireAbsolute: true);
        return Normalize(windowsProgram, requireAbsolute: false) is { } relative ? directory + "\\" + relative : null;
    }

    /// <summary>Whether <paramref name="value"/> is a valid <c>WindowsProgram</c>: absolute (<c>C:\...</c>) or relative, without <c>.</c> or <c>..</c>.</summary>
    public static bool IsValidProgram(string value) => Normalize(value, IsDriveAbsolute(value)) is not null;

    /// <summary>Whether <paramref name="value"/> is a local absolute directory such as <c>C:\Program Files\Example</c>.</summary>
    public static bool IsValidDirectory(string value) => Normalize(value, requireAbsolute: true) is not null;

    private static bool IsDriveAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    // Drive-absolute paths only: UNC paths, device paths (\\?\, \\.\) and drive-relative paths (C:foo) are refused,
    // because their location, or who can change it, depends on more than the path.
    private static string? Normalize(string path, bool requireAbsolute)
    {
        if (path.Length is 0 or > 4096) return null;
        string prefix = "";
        var rest = path;
        if (requireAbsolute)
        {
            if (!IsDriveAbsolute(path)) return null;
            prefix = char.ToUpperInvariant(path[0]) + ":";
            rest = path[3..];
        }
        var segments = rest.Split('\\', '/');
        // A trailing separator is allowed on a directory; an empty path below the drive is the drive's root.
        if (segments.Length > 0 && segments[^1].Length == 0) segments = segments[..^1];
        if (segments.Length == 0) return requireAbsolute ? prefix : null;
        foreach (var segment in segments)
            if (!IsValidSegment(segment)) return null;
        var joined = string.Join('\\', segments);
        return requireAbsolute ? prefix + "\\" + joined : joined;
    }

    // Windows strips trailing dots and spaces from a name, so "helper.exe." would be another spelling of the same file.
    private static bool IsValidSegment(string segment) =>
        segment.Length > 0 && segment is not ("." or "..") && segment[^1] is not ('.' or ' ')
        && segment.AsSpan().IndexOfAny(Invalid) < 0 && !segment.Any(char.IsControl);
}

/// <summary>Turns a failed <c>ShellExecuteExW</c> or the helper's status into a result.</summary>
internal static class ElevationOutcome
{
    internal const string Domain = "Runic.PrivilegedAction";
    internal const int ErrorCancelled = 1223;

    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorInvalidImageHash = 577;
    private const int ErrorServiceDisabled = 1058, ErrorServiceDoesNotExist = 1060, ErrorServiceNotActive = 1062;
    private const int ErrorAccessDisabledByPolicy = 1260;

    /// <summary>The result of a <c>ShellExecuteExW</c> that started nothing, from its error code.</summary>
    /// <remarks>
    /// UAC returns <c>ERROR_CANCELLED</c> both when the user answers No and when a standard user closes the credential
    /// prompt without an administrator's password, so the two cannot be told apart and both are dismissed.
    /// </remarks>
    public static PlatformResult<PrivilegedActionCompletion> FromLaunchError(int error) => error switch
    {
        ErrorCancelled => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.UserDismissed,
            new("Win32", error, "The elevation prompt was declined or dismissed.")),
        // Software restriction or AppLocker policy, elevation refused by policy, or a helper the user cannot read.
        ErrorAccessDenied or ErrorAccessDisabledByPolicy => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied, new("Win32", error)),
        // "Only elevate executables that are signed and validated" refuses an unsigned helper.
        ErrorInvalidImageHash => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData,
            new("Win32", error, "Windows requires a signed helper for elevation, and the helper's signature is missing or invalid.")),
        ErrorFileNotFound or ErrorPathNotFound => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, ProgramDiagnostic(ProgramCheck.Missing)),
        // The Application Information service, which shows the prompt, is disabled or missing.
        ErrorServiceDisabled or ErrorServiceDoesNotExist or ErrorServiceNotActive => new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        _ => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new("Win32", error)),
    };

    /// <summary>The result of a helper that connected and reported something other than <see cref="HelperStatus.Accepted"/>.</summary>
    public static PlatformResult<PrivilegedActionCompletion> FromStatus(HelperStatus status) => status switch
    {
        HelperStatus.NotElevated => new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        HelperStatus.Rejected => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData,
            new(Domain, 5, "The helper does not accept the action or its arguments; its declarations differ from the application's.")),
        HelperStatus.NotNativeAot => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData,
            new(Domain, 4, "The helper is not a NativeAOT build; its runtime started with the user's environment before it refused.")),
        HelperStatus.UntrustedLocation => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, ProgramDiagnostic(ProgramCheck.Replaceable)),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>A cancelled action outlived TerminateAfterCancellation and its handle does not allow termination.</summary>
    public static PlatformResult<PrivilegedActionCompletion> NotTerminated() =>
        new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied,
            new(Domain, 8, "The cancelled helper did not stop and could not be terminated; it may still be running."));

    public static PlatformResult<PrivilegedActionCompletion> ProtocolFailure(string message) =>
        new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new(Domain, 7, message));

    public static PlatformDiagnostic ProgramDiagnostic(ProgramCheck check) => check switch
    {
        ProgramCheck.Missing => new(Domain, 1, "The declared helper does not exist."),
        ProgramCheck.NotRealPath => new(Domain, 2, "The declared helper's path contains a symbolic link, junction or mount point, or its drive letter is substituted or has no volume."),
        ProgramCheck.Unreadable => new(Domain, 9, "The declared helper could not be read to check that it is a NativeAOT build."),
        ProgramCheck.NotNativeAot => new(Domain, 4, "The helper is not a NativeAOT build, so its runtime would load code from the user's environment when it starts."),
        _ => new(Domain, 3, "The declared helper, or a directory above it, is not owned by administrators or can be changed by other users."),
    };
}

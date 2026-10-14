using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform;
using Runic.Platform.Windows.Elevation;
using TUnit.Core;
using static Runic.Elevation.Smoke.Checks;

namespace Runic.Elevation.Smoke;

/// <summary>The portable parts: command lines, frames, lines, helper locations, trust and outcomes.</summary>
internal sealed partial class ProtocolTests
{
    internal static readonly string[] AwkwardArguments =
    [
        "", "a", "two words", "tab\there", "quote\"inside", "trailing\\", "trailing\\\\", "\\\"", "a\\\\b", "\\\\server\\share",
        @"C:\Program Files\Example\", "; & | < > ^ %PATH% !x! $(id) `id`", "new\nline", "ünïcödé ✓", "\"", "\"\"", " leading", "trailing ",
        "--runic-elevation", "-flag=\"x y\"",
    ];

    [Test, DisplayName("command line: every argument comes back unchanged from the C runtime's rules")]
    public void CommandLineRoundTrip()
    {
        var line = WindowsCommandLine.Build(AwkwardArguments);
        var parsed = ParseLikeCrt(line);
        Check(parsed.SequenceEqual(AwkwardArguments), $"parsed {string.Join(" | ", parsed)} from {line}");
        foreach (var argument in AwkwardArguments)
            Check(ParseLikeCrt(WindowsCommandLine.Build([argument])).SequenceEqual([argument]), $"alone: {argument}");
        Check(WindowsCommandLine.Build(["plain", "words"]) == "plain words", "plain arguments are not quoted");
    }

    [Test, DisplayName("command line: CommandLineToArgvW splits it into the same arguments on Windows")]
    public void CommandLineMatchesWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parsed = CommandLineToArgv("helper.exe " + WindowsCommandLine.Build(AwkwardArguments));
        Check(parsed.Skip(1).SequenceEqual(AwkwardArguments), $"CommandLineToArgvW: {string.Join(" | ", parsed)}");
    }

    [Test, DisplayName("protocol: helper arguments, pipe names, frames and greetings")]
    public async Task FramesAsync()
    {
        var name = ElevationProtocol.NewPipeName();
        Check(ElevationProtocol.IsPipeName(name) && name != ElevationProtocol.NewPipeName(), name);
        foreach (var bad in new[] { "runic-elevation-", "runic-elevation-" + new string('A', 32), @"..\..\x", "runic-elevation-" + new string('0', 31) + "/", "other" })
            Check(!ElevationProtocol.IsPipeName(bad), $"not a pipe name: {bad}");
        var arguments = ElevationProtocol.HelperArguments(name, ["report", "x"]).ToList();
        Check(ElevationProtocol.TryParseHelperArguments(arguments, out var parsedName, out var vector) && parsedName == name && vector.SequenceEqual(["report", "x"]), "round trip");
        Check(!ElevationProtocol.TryParseHelperArguments(["report", "x"], out _, out _), "without the marker");
        Check(!ElevationProtocol.TryParseHelperArguments([ElevationProtocol.Marker, @"\\.\pipe\other", "report"], out _, out _), "another pipe");
        Check(!ElevationProtocol.TryParseHelperArguments([ElevationProtocol.Marker], out _, out _), "no pipe name");

        using var stream = new MemoryStream();
        ElevationProtocol.WriteHello(stream, HelperStatus.NotElevated);
        ElevationProtocol.WriteFrame(stream, ElevationProtocol.StandardOutput, Encoding.UTF8.GetBytes("hello\n"));
        ElevationProtocol.WriteFrame(stream, ElevationProtocol.Go, []);
        stream.Position = 0;
        var buffer = new byte[ElevationProtocol.MaximumPayload];
        Check(await ElevationProtocol.ReadFrameAsync(stream, buffer, default) is (ElevationProtocol.Hello, 2), "hello frame");
        Check(ElevationProtocol.ParseHello(buffer.AsSpan(0, 2)) == HelperStatus.NotElevated, "hello status");
        Check(await ElevationProtocol.ReadFrameAsync(stream, buffer, default) is (ElevationProtocol.StandardOutput, 6) && Encoding.UTF8.GetString(buffer, 0, 6) == "hello\n", "output frame");
        Check(await ElevationProtocol.ReadFrameAsync(stream, buffer, default) is (ElevationProtocol.Go, 0), "empty frame");
        Check(await ElevationProtocol.ReadFrameAsync(stream, buffer, default) is null, "end of stream");
        Check(ElevationProtocol.ParseHello([2, 0]) is null && ElevationProtocol.ParseHello([1, 99]) is null && ElevationProtocol.ParseHello([1]) is null, "other versions and statuses");

        using var oversized = new MemoryStream([ElevationProtocol.StandardOutput, 0xFF, 0xFF, 0, 0]);
        var thrown = false;
        try { await ElevationProtocol.ReadFrameAsync(oversized, buffer, default); }
        catch (InvalidDataException) { thrown = true; }
        Check(thrown, "an oversized frame is refused before it is read");
        using var truncated = new MemoryStream([ElevationProtocol.StandardOutput, 4, 0, 0, 0, (byte)'a']);
        Check(await ElevationProtocol.ReadFrameAsync(truncated, buffer, default) is null, "a truncated frame is the end");
    }

    [Test, DisplayName("lines: CRLF, long lines in parts, UTF-8 not cut and a last line without a newline")]
    public void Lines()
    {
        var lines = new List<string>();
        var splitter = new LineSplitter(lines.Add);
        var text = "first\r\nsecond\n" + new string('é', LineSplitter.MaximumLineBytes) + "\nlast";
        var bytes = Encoding.UTF8.GetBytes(text);
        // Odd-sized pieces cross line and character boundaries.
        for (var i = 0; i < bytes.Length; i += 777) splitter.Push(bytes.AsSpan(i, Math.Min(777, bytes.Length - i)));
        splitter.Flush();
        Check(lines[0] == "first" && lines[1] == "second" && lines[^1] == "last", string.Join(" | ", lines.Select(l => l.Length > 20 ? l[..20] + "…" : l)));
        Check(string.Concat(lines.Skip(2).SkipLast(1)) == new string('é', LineSplitter.MaximumLineBytes), "the long line arrives whole in parts");
        Check(lines.Skip(2).SkipLast(1).All(l => !l.Contains('\uFFFD', StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(l) <= LineSplitter.MaximumLineBytes), "no part cuts a character");
    }

    [Test, DisplayName("location: the declared program's file name by default, WindowsProgram relative or absolute, nothing else")]
    public void Locations()
    {
        var action = new PrivilegedActionDeclaration("org.example.app.activate", "/usr/libexec/example/example-helper") { Verb = "activate" };
        Check(HelperLocation.Resolve(action, null, @"C:\Program Files\Example") == @"C:\Program Files\Example\example-helper.exe", "default");
        Check(HelperLocation.Resolve(action, null, @"c:\Program Files\Example\") == @"C:\Program Files\Example\example-helper.exe", "trailing separator and drive case");
        Check(HelperLocation.Resolve(new("org.example.app.x", "/opt/x/helper.exe"), null, @"C:\App") == @"C:\App\helper.exe", "an .exe name is kept");
        Check(HelperLocation.Resolve(action, @"helpers\example-helper.exe", @"C:\App") == @"C:\App\helpers\example-helper.exe", "relative");
        Check(HelperLocation.Resolve(action, "helpers/example-helper.exe", @"C:\App") == @"C:\App\helpers\example-helper.exe", "forward slashes");
        Check(HelperLocation.Resolve(action, @"D:\Tools\helper.exe", @"C:\App") == @"D:\Tools\helper.exe", "absolute");
        foreach (var bad in new[] { @"..\helper.exe", @"helpers\.\helper.exe", @"\\server\share\helper.exe", @"\\?\C:\helper.exe", "C:helper.exe", @"helper.exe.", "helper.exe ", "help|er.exe", "a:b.exe", "" })
            Check(HelperLocation.Resolve(action, bad, @"C:\App") is null && !HelperLocation.IsValidProgram(bad), $"refused: {bad}");
        foreach (var bad in new[] { "relative", @"\\server\share", @"C:\App\..\Other", "/usr/lib" })
            Check(HelperLocation.Resolve(action, null, bad) is null && !HelperLocation.IsValidDirectory(bad), $"refused directory: {bad}");
    }

    [Test, DisplayName("trust: a Program Files installation is trusted; anything another user can change is not")]
    public void Trust()
    {
        const string Users = "S-1-5-32-545", AuthenticatedUsers = "S-1-5-11", User = "S-1-5-21-1-2-3-1001", AllPackages = "S-1-15-2-1";
        const uint ReadExecute = 0x1200A9, Modify = 0x1301BF, AddSubdirectory = 0x4, Full = 0x1F01FF;
        AccessEntry Allow(string sid, uint mask, bool inheritOnly = false) => new(true, inheritOnly, mask, sid);
        PathComponent Root(params AccessEntry[] extra) => new(@"C:\", ComponentRole.Ancestor, ProgramTrust.TrustedInstaller,
            [Allow(ProgramTrust.Administrators, Full), Allow(ProgramTrust.LocalSystem, Full), Allow(Users, ReadExecute),
             Allow(AuthenticatedUsers, AddSubdirectory), Allow(AuthenticatedUsers, Modify, inheritOnly: true), .. extra]);
        PathComponent ProgramFiles = new(@"C:\Program Files", ComponentRole.Ancestor, ProgramTrust.TrustedInstaller,
            [Allow(ProgramTrust.TrustedInstaller, Full), Allow(ProgramTrust.LocalSystem, Modify), Allow(ProgramTrust.Administrators, Modify),
             Allow(Users, ReadExecute), Allow(AllPackages, ReadExecute), Allow("S-1-3-0", Full, inheritOnly: true)]);
        PathComponent Installed(ComponentRole role, string owner = ProgramTrust.Administrators, params AccessEntry[] extra) => new(@"C:\Program Files\Example", role, owner,
            [Allow(ProgramTrust.Administrators, Full), Allow(ProgramTrust.LocalSystem, Full), Allow(Users, ReadExecute), .. extra]);

        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory), Installed(ComponentRole.Program)]) == ProgramCheck.Trusted, "Program Files");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory, extra: new AccessEntry(false, false, Full, User)), Installed(ComponentRole.Program)]) == ProgramCheck.Trusted,
            "a denied entry only removes rights");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory, extra: Allow(User, Modify, inheritOnly: true)), Installed(ComponentRole.Program)]) == ProgramCheck.Trusted,
            "an inherit-only entry does not apply to the directory");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory, extra: Allow(User, Modify)), Installed(ComponentRole.Program)]) == ProgramCheck.Replaceable,
            "a user who can add files to the helper's directory could plant a DLL");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory), Installed(ComponentRole.Program, extra: Allow(Users, 0x2))]) == ProgramCheck.Replaceable,
            "writable by Users");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory), Installed(ComponentRole.Program, owner: User)]) == ProgramCheck.Replaceable,
            "owned by a user, who can change its DACL");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory), new(@"C:\Program Files\Example\helper.exe", ComponentRole.Program, ProgramTrust.Administrators, null)]) == ProgramCheck.Replaceable,
            "a NULL DACL grants everyone everything");
        Check(ProgramTrust.Evaluate([Root(Allow(User, 0x40)), ProgramFiles, Installed(ComponentRole.Directory), Installed(ComponentRole.Program)]) == ProgramCheck.Replaceable,
            "deleting children of an ancestor replaces the path");
        Check(ProgramTrust.Evaluate([Root() with { Role = ComponentRole.Directory }, Installed(ComponentRole.Program)]) == ProgramCheck.Replaceable,
            "a helper in C:\\, where users can add files, is not trusted");
        Check(ProgramTrust.Evaluate([Root(), ProgramFiles, Installed(ComponentRole.Directory, extra: Allow("unknown", uint.MaxValue)), Installed(ComponentRole.Program)]) == ProgramCheck.Replaceable,
            "an unknown entry fails closed");
    }

    [Test, DisplayName("image: a JIT build (runtime files beside it, or the apphost's bundle marker) is not a NativeAOT helper")]
    public void HelperImages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runic-elevation-image-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var helper = Path.Combine(directory, "helper.exe");
            // Complemented, so this executable does not carry the marker itself (its NativeAOT build is checked below).
            byte[] marker = [0x74, 0xed, 0xfd, 0x46, 0x95, 0x9e, 0xdf, 0xc7, 0x8d, 0x84, 0x6c, 0xfd, 0xeb, 0x28, 0x5f, 0xcd, 0xec, 0x0a, 0x46, 0x19, 0x10, 0x51, 0xcc, 0xe7, 0x11, 0xc4, 0xd2, 0x31, 0xdb, 0x4c, 0x95, 0x51];
            for (var i = 0; i < marker.Length; i++) marker[i] = (byte)~marker[i];
            File.WriteAllBytes(helper, new byte[200_000]);
            Check(HelperImage.Check(helper) == ProgramCheck.Trusted, "a plain image");
            // Across the boundary of two 64 KiB reads.
            var image = new byte[200_000];
            marker.CopyTo(image, 64 * 1024 - 10);
            File.WriteAllBytes(helper, image);
            Check(HelperImage.Check(helper) == ProgramCheck.NotNativeAot, "the bundle marker across reads");
            File.WriteAllBytes(helper, new byte[1000]);
            foreach (var extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            {
                File.WriteAllText(Path.Combine(directory, "helper" + extension), "");
                Check(HelperImage.Check(helper) == ProgramCheck.NotNativeAot, extension);
                File.Delete(Path.Combine(directory, "helper" + extension));
            }
            // This executable: the apphost under JIT, a NativeAOT image when published so.
            var self = Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Windows.Elevation.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(self))
                Check(HelperImage.Check(self) == (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? ProgramCheck.NotNativeAot : ProgramCheck.Trusted),
                    $"this executable, JIT={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("outcomes: declined, refused by policy, unsigned, no prompt service, and the helper's reports")]
    public void Outcomes()
    {
        Check(ElevationOutcome.FromLaunchError(1223) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.UserDismissed, { Code: 1223 }), "ERROR_CANCELLED is dismissed: UAC reports a No and a closed credential prompt the same way");
        Check(ElevationOutcome.FromLaunchError(5) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied, _), "access denied");
        Check(ElevationOutcome.FromLaunchError(1260) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied, _), "blocked by policy");
        Check(ElevationOutcome.FromLaunchError(577) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, _), "unsigned where signatures are required");
        Check(ElevationOutcome.FromLaunchError(2) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 1 }), "missing");
        Check(ElevationOutcome.FromLaunchError(1058) is PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "Application Information service disabled");
        Check(ElevationOutcome.FromLaunchError(1155) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, { Code: 1155 }), "anything else");
        Check(ElevationOutcome.FromStatus(HelperStatus.NotElevated) is PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "UAC off");
        Check(ElevationOutcome.FromStatus(HelperStatus.Rejected) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 5 }), "rejected");
        Check(ElevationOutcome.FromStatus(HelperStatus.NotNativeAot) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 4 }), "JIT helper");
        Check(ElevationOutcome.FromStatus(HelperStatus.UntrustedLocation) is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 3 }), "helper in a user-writable place");
        Check(ElevationOutcome.ProgramDiagnostic(ProgramCheck.NotNativeAot).Code == 4, "a JIT image found before the prompt");
    }

    [Test, DisplayName("match: the verb selects the action, the rest must be within it")]
    public void Match()
    {
        var all = PrivilegedActions.All;
        Check(ElevatedHelper.MatchCore(all, ["report", "a", "b"], out var rest) == PrivilegedActions.Report && rest.SequenceEqual(["a", "b"]), "report");
        Check(ElevatedHelper.MatchCore(all, ["flood", "12"], out _) == PrivilegedActions.Flood, "flood");
        Check(ElevatedHelper.MatchCore(all, ["flood", "x"], out _) is null, "the pattern applies");
        Check(ElevatedHelper.MatchCore(all, ["wait", "extra"], out _) is null, "too many arguments");
        Check(ElevatedHelper.MatchCore(all, ["unknown"], out _) is null && ElevatedHelper.MatchCore(all, [], out _) is null, "no verb");
        Check(ElevatedHelper.Match(all, ["report"], out _) is null, "Match refuses arguments without the pipe");
    }

    [Test, DisplayName("create: off Windows unavailable; ambiguous actions and bad helper paths are caller errors")]
    public async Task CreateAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            var unavailable = WindowsPrivilegedActions.Create(PrivilegedActions.All, PrivilegedActions.WindowsElevation);
            Check(await unavailable.RunAsync(new(PrivilegedActions.Wait, [])) is PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "unavailable");
        }
        var verbless = new PrivilegedActionDeclaration("org.example.app.any", "/opt/a");
        Throws<ArgumentException>(() => WindowsPrivilegedActions.Create([PrivilegedActions.Report, verbless]), "a verbless action with another");
        Throws<ArgumentException>(() => WindowsPrivilegedActions.Create([PrivilegedActions.Report, PrivilegedActions.Report]), "twice");
        Throws<ArgumentException>(() => _ = new WindowsPrivilegedActions(PrivilegedActions.All, new() { Programs = new Dictionary<string, string> { ["org.example.other"] = "x.exe" } }, @"C:\App", new FakeHost()), "an undeclared program entry");
        Throws<ArgumentException>(() => _ = new WindowsPrivilegedActions(PrivilegedActions.All, new() { Programs = new Dictionary<string, string> { [PrivilegedActions.Report.Id] = @"..\x.exe" } }, @"C:\App", new FakeHost()), "a program outside");
        if (OperatingSystem.IsWindows())
            Throws<ArgumentException>(() => WindowsPrivilegedActions.Create(PrivilegedActions.All, new() { ApplicationDirectory = @"\\server\share" }), "a network directory");
        Throws<ArgumentOutOfRangeException>(() => WindowsPrivilegedActions.Create(PrivilegedActions.All, new() { TerminateAfterCancellation = TimeSpan.FromSeconds(-1) }), "a negative wait");
    }

    // The C runtime's rules (and CommandLineToArgvW's for arguments after the program name, apart from "" inside
    // quotes, which WindowsCommandLine never writes).
    internal static List<string> ParseLikeCrt(string line)
    {
        var arguments = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            if (i >= line.Length) return arguments;
            var argument = new StringBuilder();
            var quoted = false;
            while (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
            {
                if (line[i] == '\\')
                {
                    var start = i;
                    while (i < line.Length && line[i] == '\\') i++;
                    var count = i - start;
                    if (i < line.Length && line[i] == '"')
                    {
                        argument.Append('\\', count / 2);
                        if (count % 2 == 1)
                        {
                            argument.Append('"');
                            i++;
                        }
                    }
                    else argument.Append('\\', count);
                }
                else if (line[i] == '"')
                {
                    quoted = !quoted;
                    i++;
                }
                else argument.Append(line[i++]);
            }
            arguments.Add(argument.ToString());
        }
    }

    private static unsafe string[] CommandLineToArgv(string line)
    {
        var argv = CommandLineToArgvW(line, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(((nint*)argv)[i])!;
            return result;
        }
        finally { LocalFree(argv); }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string line, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

internal static class Checks
{
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected {typeof(T).Name}: {message}");
    }
}

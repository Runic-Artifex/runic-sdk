using System.Collections.Immutable;
using System.Text;
using Runic.Platform.Linux.Portal;

// A temporary XDG tree for one case: home, data home, two data directories, config home, a config directory, a PATH
// directory and a pixmaps directory. The provider reads only these, never the user's directories or session bus.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class DesktopApplicationsFixture : IDisposable
{
    public string Root { get; }
    public string Home => Path.Combine(Root, "home");
    public string DataHome => Path.Combine(Home, ".local", "share");
    public string LocalShare => Path.Combine(Root, "usr", "local", "share");
    public string Share => Path.Combine(Root, "usr", "share");
    public string ConfigHome => Path.Combine(Home, ".config");
    public string ConfigDirectory => Path.Combine(Root, "etc", "xdg");
    public string Bin => Path.Combine(Root, "bin");
    public string Pixmaps => Path.Combine(Root, "pixmaps");

    public DesktopApplicationsFixture()
    {
        Root = Directory.CreateTempSubdirectory("runic-apps-").FullName;
        foreach (var directory in (string[])[Home, DataHome, LocalShare, Share, ConfigHome, ConfigDirectory, Bin, Pixmaps]) Directory.CreateDirectory(directory);
    }

    public DesktopApplicationsOptions Options(string[]? desktops = null, string locale = "", string? theme = null, bool sandboxed = false) => new()
    {
        HomeDirectory = Home, DataHome = DataHome, DataDirectories = [LocalShare, Share],
        ConfigHome = ConfigHome, ConfigDirectories = [ConfigDirectory],
        CurrentDesktops = [.. desktops ?? []], SearchPath = [Bin], Locale = locale, IconTheme = theme,
        ReadPortalIconTheme = false, BusAddress = "unix:path=" + Path.Combine(Root, "no-bus"), Sandboxed = sandboxed,
        PixmapDirectories = [Pixmaps],
    };

    public LinuxDesktopApplications Create(string[]? desktops = null, string locale = "", string? theme = null, bool sandboxed = false,
        List<PortalDiagnostic>? diagnostics = null) =>
        new(Options(desktops, locale, theme, sandboxed), new PortalApplication(null, diagnostics is null ? null : diagnostics.Add));

    public static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public static string Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Writes an application entry into <paramref name="dataDirectory"/>/applications.</summary>
    public static string Entry(string dataDirectory, string relativePath, string name, string extra = "", string exec = "true") =>
        Write(Path.Combine(dataDirectory, "applications", relativePath), $"[Desktop Entry]\nType=Application\nName={name}\nExec={exec}\n{extra}");

    public string Executable(string name)
    {
        string path = Write(Path.Combine(Bin, name), "#!/nonexistent\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // A PNG signature and IHDR chunk with the width; enough for the provider, which does not decode images.
    public static byte[] Png(int width) =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, 0, 0, 0, (byte)width, 8, 6, 0, 0, 0];

    /// <summary>One matchlet of the binary magic format.</summary>
    public sealed record Rule(int Indent, int Offset, byte[] Value, byte[]? Mask = null, int WordSize = 1, int Range = 1, string? Garbage = null);

    /// <summary>Writes the binary magic format: "MIME-Magic\0\n", then "[priority:type]\n" and its matchlet lines.</summary>
    public static byte[] Magic(params (int Priority, string Type, Rule[] Rules)[] sections)
    {
        var data = new List<byte>(Encoding.ASCII.GetBytes("MIME-Magic\0\n"));
        foreach (var (priority, type, rules) in sections)
        {
            data.AddRange(Encoding.ASCII.GetBytes($"[{priority}:{type}]\n"));
            foreach (var rule in rules)
            {
                if (rule.Indent > 0) data.AddRange(Encoding.ASCII.GetBytes(rule.Indent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                data.AddRange(Encoding.ASCII.GetBytes($">{rule.Offset}="));
                data.Add((byte)(rule.Value.Length >> 8)); data.Add((byte)rule.Value.Length);
                data.AddRange(rule.Value);
                if (rule.Mask is not null) { data.Add((byte)'&'); data.AddRange(rule.Mask); }
                if (rule.WordSize != 1) data.AddRange(Encoding.ASCII.GetBytes($"~{rule.WordSize}"));
                if (rule.Range != 1) data.AddRange(Encoding.ASCII.GetBytes($"+{rule.Range}"));
                if (rule.Garbage is not null) data.AddRange(Encoding.ASCII.GetBytes(rule.Garbage));
                data.Add((byte)'\n');
            }
        }
        return [.. data];
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public void Dispose()
    {
        // Cases make files unreadable; restore access so the tree can be removed.
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            try { if (new FileInfo(path).LinkTarget is null) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// The probe application of the launch cases: this test executable, started with --launch-probe <directory>, records its
// working directory and arguments, NUL-separated, in a new file in that directory.
internal static class LaunchProbe
{
    public static int Record(string directory, string[] arguments)
    {
        string temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temporary, string.Join('\0', [Environment.CurrentDirectory, .. arguments]), new UTF8Encoding(false));
        File.Move(temporary, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".probe"));
        return 0;
    }

    /// <summary>The start of an Exec value that runs the probe, quoted by the Desktop Entry rules.</summary>
    public static string Exec(string directory)
    {
        string process = Environment.ProcessPath!;
        var command = new List<string> { process };
        // dotnet Runic.Platform.Linux.Portal.Tests.dll, as CI runs the JIT build.
        if (Path.GetFileNameWithoutExtension(process) == "dotnet") command.Add(Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Linux.Portal.Tests.dll"));
        command.Add("--launch-probe");
        command.Add(directory);
        return string.Join(' ', command.Select(LinuxLoginStartup.Quote));
    }

    /// <summary>Waits for <paramref name="count"/> probe runs and returns each one's working directory and arguments.</summary>
    public static async Task<List<(string Directory, ImmutableArray<string> Arguments)>> WaitAsync(string directory, int count)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(30));
        while (Directory.GetFiles(directory, "*.probe").Length < count && DateTime.UtcNow < deadline) await Task.Delay(25);
        await Task.Delay(200); // A process too many would show up now.
        return [.. Directory.GetFiles(directory, "*.probe").Select(static path => File.ReadAllText(path).Split('\0'))
            .Select(static parts => (parts[0], parts.Skip(1).ToImmutableArray())).OrderBy(static run => string.Join('\u0001', run.Item2), StringComparer.Ordinal)];
    }
}

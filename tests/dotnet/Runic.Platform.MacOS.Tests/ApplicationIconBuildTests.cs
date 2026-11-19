using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

/// <summary>
/// Runic.Platform's application icon targets (sdk#1207) in small projects: a project without ApplicationIcon gets the
/// .ico written from its package icon, RunicApplicationIconGenerationEnabled=false and the project's own icon turn it
/// off, and an icon that is not square, too small or no PNG is a warning. One real SDK build shows that the compiler
/// embeds the icon as the assembly's Win32 icon group.
/// </summary>
internal sealed class ApplicationIconBuildTests
{
    private static readonly string Targets = Path.Combine(AppContext.BaseDirectory, "privileged-targets", "Runic.Platform.targets");

    [Test, DisplayName("build: a project without ApplicationIcon gets the .ico written from icons/icon.png")]
    public async Task GeneratesApplicationIconAsync()
    {
        using var project = new IconProject();
        project.Icon("icons/icon.png", ApplicationIconCodecTests.Sample(512));
        var (exit, output, icon) = await project.ApplicationIconAsync();
        Check(exit == 0, output);
        Check(icon == project.Generated, $"ApplicationIcon is '{icon}'\n{output}");
        Check(!output.Contains("RUNICICON", StringComparison.Ordinal), output);
        CheckIco(File.ReadAllBytes(project.Generated));

        // Unchanged inputs leave the file alone; a changed icon writes it again.
        var written = File.GetLastWriteTimeUtc(project.Generated);
        await Task.Delay(50);
        (exit, output, icon) = await project.ApplicationIconAsync();
        Check(exit == 0 && icon == project.Generated && File.GetLastWriteTimeUtc(project.Generated) == written, $"incremental\n{output}");
        var first = File.ReadAllBytes(project.Generated);
        project.Icon("icons/icon.png", ApplicationIconCodecTests.Sample(256));
        (exit, output, icon) = await project.ApplicationIconAsync();
        Check(exit == 0 && icon == project.Generated && !File.ReadAllBytes(project.Generated).AsSpan().SequenceEqual(first), $"rewritten\n{output}");
    }

    [Test, DisplayName("build: RunicPackageIcon names the source, as for dotnet runic package")]
    public async Task ReadsRunicPackageIconAsync()
    {
        using var project = new IconProject("<RunicPackageIcon>art/app.png</RunicPackageIcon>");
        project.Icon("art/app.png", ApplicationIconCodecTests.Sample(300));
        var (exit, output, icon) = await project.ApplicationIconAsync();
        Check(exit == 0 && icon == project.Generated && File.Exists(project.Generated), $"'{icon}'\n{output}");
    }

    [Test, DisplayName("build: the opt-out, the project's own icon, libraries, test projects and a missing icon get no generated icon")]
    public async Task LeavesApplicationIconAloneAsync()
    {
        foreach (var (properties, expected) in new[]
        {
            ("<RunicApplicationIconGenerationEnabled>false</RunicApplicationIconGenerationEnabled>", ""),
            ("<ApplicationIcon>own.ico</ApplicationIcon>", "own.ico"),
            ("<Win32Resource>own.res</Win32Resource>", ""),
            ("<OutputType>Library</OutputType>", ""),
            ("<IsTestProject>true</IsTestProject>", ""),
        })
        {
            using var project = new IconProject(properties);
            project.Icon("icons/icon.png", ApplicationIconCodecTests.Sample(256));
            var (exit, output, icon) = await project.ApplicationIconAsync();
            Check(exit == 0 && icon == expected && !File.Exists(project.Generated), $"{properties}: '{icon}'\n{output}");
        }
        using (var none = new IconProject())
        {
            var (exit, output, icon) = await none.ApplicationIconAsync();
            Check(exit == 0 && icon.Length == 0 && !output.Contains("RUNICICON", StringComparison.Ordinal), $"no icon: '{icon}'\n{output}");
        }
    }

    [Test, DisplayName("build: an icon that is not square, smaller than 256 px or not a PNG is a RUNICICON warning")]
    public async Task RefusesUnsuitableIconsAsync()
    {
        foreach (var (code, write) in new (string, Action<IconProject>)[]
        {
            ("RUNICICON001", project => project.File("icons/icon.png", "not a PNG"u8.ToArray())),
            ("RUNICICON002", project => project.Icon("icons/icon.png", new RunicIconImage(512, 384, new byte[512 * 384 * 4]))),
            ("RUNICICON003", project => project.Icon("icons/icon.png", ApplicationIconCodecTests.Sample(128))),
        })
        {
            using var project = new IconProject();
            // A good icon first: the refused one replaces it, and the build no longer uses the stale file.
            project.Icon("icons/icon.png", ApplicationIconCodecTests.Sample(256));
            var (exit, output, icon) = await project.ApplicationIconAsync();
            Check(exit == 0 && icon == project.Generated, $"{code}: good icon first\n{output}");
            write(project);
            (exit, output, icon) = await project.ApplicationIconAsync();
            Check(exit == 0 && icon.Length == 0 && !File.Exists(project.Generated), $"{code}: '{icon}'\n{output}");
            Check(output.Contains($"warning {code}", StringComparison.Ordinal), $"{code} reported\n{output}");
        }
    }

    [Test, DisplayName("build: the compiler embeds the generated icon as the assembly's Win32 icon group")]
    public async Task EmbedsIconInAssemblyAsync()
    {
        using var project = new IconProject(sdk: true);
        project.Icon("icons/icon.png", ApplicationIconCodecTests.Sample(512));
        var (exit, output, error) = await project.RunAsync("build", "IconApp.csproj", "-nologo", "-v:m", "-nodeReuse:false");
        Check(exit == 0, output + error);
        string assembly = Directory.GetFiles(project.Directory, "IconApp.dll", SearchOption.AllDirectories).Single(path => path.Contains("bin", StringComparison.Ordinal));
        var sizes = IconGroupSizes(File.ReadAllBytes(assembly));
        Check(string.Join(",", sizes) == "16,20,24,32,40,48,64,256", $"icon group of {assembly}: {string.Join(",", sizes)}");
    }

    /// <summary>The sizes of the first RT_GROUP_ICON of a PE file's resources.</summary>
    internal static List<int> IconGroupSizes(byte[] image)
    {
        using var reader = new PEReader(new MemoryStream(image));
        var directory = reader.PEHeaders.PEHeader!.ResourceTableDirectory;
        Check(directory.Size > 0, "the PE file has resources");
        byte[] resources = [.. reader.GetSectionData(directory.RelativeVirtualAddress).GetContent()];
        // IMAGE_RESOURCE_DIRECTORY: named and ID entry counts at 12 and 14, then 8-byte entries; a set high bit of an
        // entry's second word marks a subdirectory, whose offset counts from the start of the resources.
        (uint Name, uint Offset)[] Entries(int at)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(resources.AsSpan(at + 12)) + BinaryPrimitives.ReadUInt16LittleEndian(resources.AsSpan(at + 14));
            return Enumerable.Range(0, count).Select(i => (BinaryPrimitives.ReadUInt32LittleEndian(resources.AsSpan(at + 16 + i * 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(resources.AsSpan(at + 20 + i * 8)))).ToArray();
        }
        var group = Entries(0).Single(entry => entry.Name == 14);
        var name = Entries((int)(group.Offset & 0x7fffffff))[0];
        var language = Entries((int)(name.Offset & 0x7fffffff))[0];
        int data = (int)language.Offset;
        int rva = BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(data)), size = BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(data + 4));
        byte[] icon = [.. reader.GetSectionData(rva).GetContent(0, size)];
        // GRPICONDIR: reserved, type, count, then 14-byte entries starting with the width (0 for 256).
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        return Enumerable.Range(0, entries).Select(i => icon[6 + i * 14] == 0 ? 256 : icon[6 + i * 14]).ToList();
    }

    private static void CheckIco(byte[] ico)
    {
        Check(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)) == 8, "an .ico with 8 images");
        var sizes = Enumerable.Range(0, 8).Select(i => ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16]);
        Check(string.Join(",", sizes) == "16,20,24,32,40,48,64,256", string.Join(",", sizes));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IconProject : IDisposable
    {
        public IconProject(string properties = "", bool sdk = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (sdk)
            {
                // A real executable: the SDK compiles it with the ApplicationIcon the targets set.
                File("IconApp.csproj", System.Text.Encoding.UTF8.GetBytes($"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                        <AssemblyName>IconApp</AssemblyName>
                        <UseAppHost>false</UseAppHost>
                        <ImplicitUsings>disable</ImplicitUsings>
                      </PropertyGroup>
                      <Import Project="{Targets}" />
                    </Project>
                    """));
                File("Program.cs", "return 0;\n"u8.ToArray());
                return;
            }
            // The SDK's properties for an executable, the project's, then the package's targets; CoreCompile stands in
            // for the compiler.
            File("App.proj", System.Text.Encoding.UTF8.GetBytes($"""
                <Project>
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <AssemblyName>Notes</AssemblyName>
                    <IntermediateOutputPath>obj/Debug/</IntermediateOutputPath>
                    {properties}
                  </PropertyGroup>
                  <Target Name="CoreCompile" />
                  <Import Project="{Targets}" />
                </Project>
                """));
        }

        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "runic-application-icon-" + Guid.NewGuid().ToString("N")[..8]);

        public string Generated => Path.Combine(Directory, "obj", "Debug", "runic-icon", "application.ico");

        public void Icon(string path, RunicIconImage image) => File(path, RunicPngEncoder.Encode(image));

        public void File(string path, byte[] content)
        {
            string full = Path.Combine(Directory, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, content);
        }

        // Runs CoreCompile and reads ApplicationIcon after it; the warnings go to standard error.
        public async Task<(int ExitCode, string Output, string ApplicationIcon)> ApplicationIconAsync()
        {
            var (exit, output, error) = await RunAsync("msbuild", "App.proj", "-t:CoreCompile", "-getProperty:ApplicationIcon", "-nologo", "-nodeReuse:false");
            // With -getProperty, standard output is the value; the log, with the warnings, goes to standard error.
            return (exit, output + error, output.Trim());
        }

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Directory,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitScaledAsync(TimeSpan.FromMinutes(3));
            return (process.ExitCode, await stdout, await stderr);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

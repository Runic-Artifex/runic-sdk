using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.Assets.Tests;

// The cross-platform archive APIs: AssetArchive.ReadAsync, the trusted directory writer, the default exclusions
// (.git directories and .env* files, SEC-32) and the packer's RAS1005 source-map warning, in the packer and through
// the Runic.Assets MSBuild targets. The native job runs this class on Windows and macOS as well.
internal sealed class ArchiveDirectoryTests
{
    // The canonical manifest digest (AssetArchive.Inspect) of WriteTreeAsync's tree. The same tree gives the same
    // manifest and content on Linux, Windows and macOS, so every leg checks the same value. The ZIP bytes are the
    // same on each OS, but not across them: System.IO.Compression records the origin OS and its file attributes.
    private const string CanonicalTreeManifestSha256 = "11270532face7bb9ef63f750974ce264754ed7ed7b23c1cd41c4cc72ec3cfc24";

    [Test, DisplayName("ReadAsync matches Read for seekable and non-seekable streams")]
    public async Task ReadAsyncMatchesRead()
    {
        using var directory = new TemporaryDirectory();
        byte[] archive = await WriteTreeAsync(directory).ConfigureAwait(false);

        AssetArchiveSource expected = AssetArchive.Read(new MemoryStream(archive));
        AssetArchiveSource seekable = await AssetArchive.ReadAsync(new MemoryStream(archive)).ConfigureAwait(false);
        AssetArchiveSource forwardOnly = await AssetArchive.ReadAsync(new ForwardOnlyStream(archive)).ConfigureAwait(false);
        foreach (AssetArchiveSource actual in new[] { seekable, forwardOnly })
        {
            await actual.ValidateAsync().ConfigureAwait(false);
            SequenceEqual(
                expected.Manifest.Assets.Select(static asset => (asset.RelativePath, asset.MediaType, asset.Length, asset.Sha256, asset.IsEntryPoint, asset.CacheMode)),
                actual.Manifest.Assets.Select(static asset => (asset.RelativePath, asset.MediaType, asset.Length, asset.Sha256, asset.IsEntryPoint, asset.CacheMode)));
        }
    }

    [Test, DisplayName("ReadAsync applies the limits and validation of Read")]
    public async Task ReadAsyncLimits()
    {
        using var directory = new TemporaryDirectory();
        byte[] archive = await WriteTreeAsync(directory).ConfigureAwait(false);

        await ThrowsAsync<InvalidDataException>(async () => await AssetArchive.ReadAsync(
            new ForwardOnlyStream(archive), new AssetArchiveReadOptions { MaxArchiveBytes = archive.Length - 1 }).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<InvalidDataException>(async () => await AssetArchive.ReadAsync(
            new MemoryStream(archive), new AssetArchiveReadOptions { MaxFileCount = 1 }).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<InvalidDataException>(async () => await AssetArchive.ReadAsync(
            new MemoryStream(archive), new AssetArchiveReadOptions { MaxTotalUncompressedBytes = 4 }).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<InvalidDataException>(async () => await AssetArchive.ReadAsync(
            new MemoryStream(archive[..(archive.Length / 2)])).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<ArgumentOutOfRangeException>(async () => await AssetArchive.ReadAsync(
            new MemoryStream(archive), new AssetArchiveReadOptions { MaxManifestBytes = 0 }).ConfigureAwait(false)).ConfigureAwait(false);
        await ThrowsAsync<ArgumentNullException>(async () => await AssetArchive.ReadAsync(null!).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test, DisplayName("ReadAsync honours cancellation before and while it reads")]
    public async Task ReadAsyncCancellation()
    {
        using var directory = new TemporaryDirectory();
        byte[] archive = await WriteTreeAsync(directory).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await ThrowsAsync<OperationCanceledException>(async () => await AssetArchive.ReadAsync(
            new ForwardOnlyStream(archive), cancellationToken: cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);

        using var during = new CancellationTokenSource();
        var stream = new ForwardOnlyStream(archive, chunk: 16, afterFirstRead: during.Cancel);
        await ThrowsAsync<OperationCanceledException>(async () => await AssetArchive.ReadAsync(
            stream, cancellationToken: during.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test, DisplayName("the trusted directory writer is deterministic and gives one manifest on every operating system")]
    public async Task TrustedWriterIsPortable()
    {
        using var directory = new TemporaryDirectory();
        byte[] first = await WriteTreeAsync(directory).ConfigureAwait(false);
        byte[] second = await WriteTreeAsync(directory).ConfigureAwait(false);
        SequenceEqual(first, second);
        Equal(CanonicalTreeManifestSha256, AssetArchive.Inspect(new MemoryStream(first)).ManifestSha256);

        AssetArchiveSource source = AssetArchive.Read(new MemoryStream(first));
        SequenceEqual(
            new[] { "assets/app-Cf3tzbYH.js", "assets/site.css", "index.html", "nested/deeper/data.json" },
            source.Manifest.Assets.Select(static asset => asset.RelativePath));
        Equal("index.html", source.Manifest.EntryPoint.RelativePath);
    }

    [Test, DisplayName("the trusted directory writer rejects symbolic links, and junctions on Windows")]
    public async Task TrustedWriterRejectsLinks()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("site/index.html", "<p>x</p>");
        directory.Write("target/secret.txt", "secret");
        string site = Path.Combine(directory.Path, "site");
        string target = Path.Combine(directory.Path, "target");
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no privilege; it is a reparse point.
            Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(site, "junction"), target);
            await ThrowsLinkAsync(site).ConfigureAwait(false);
            Directory.Delete(Path.Combine(site, "junction"));
        }

        // A symbolic link on Windows needs Developer Mode or elevation, which hosted runners have.
        if (TryCreateSymbolicLink(Path.Combine(site, "linked.txt"), Path.Combine(target, "secret.txt"), directory: false))
        {
            await ThrowsLinkAsync(site).ConfigureAwait(false);
            File.Delete(Path.Combine(site, "linked.txt"));
        }

        if (TryCreateSymbolicLink(Path.Combine(site, "linked"), target, directory: true))
        {
            await ThrowsLinkAsync(site).ConfigureAwait(false);
        }

        static async Task ThrowsLinkAsync(string site)
        {
            using var output = new MemoryStream();
            await ThrowsAsync<InvalidDataException>(async () =>
                await AssetArchive.WriteTrustedDirectoryAsync(site, output).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    [Test, DisplayName("the directory writers leave .git and .env* out by default and pack them on request")]
    public async Task DefaultExclusions()
    {
        using var directory = new TemporaryDirectory();
        string site = WriteDotfileTree(directory);
        string[] packed = await PackedPathsAsync(site, new AssetArchiveDirectoryOptions()).ConfigureAwait(false);
        SequenceEqual(new[] { "app.js", "app.js.map", "environment.json", "index.html", "nested/env.txt" }, packed);

        string[] everything = await PackedPathsAsync(site, new AssetArchiveDirectoryOptions { ExcludeDefaultPatterns = false }).ConfigureAwait(false);
        SequenceEqual(
            new[] { ".env", ".envrc", ".git/HEAD", ".git/objects/ab/cdef", "app.js", "app.js.map", "environment.json", "index.html",
                "nested/.ENV.local", "nested/.git", "nested/env.txt", "nested/repo/.git/config" },
            everything);

        string[] exact = await PackedPathsAsync(site, new AssetArchiveDirectoryOptions { ExcludedPaths = ["app.js.map", "nested/env.txt"] }).ConfigureAwait(false);
        SequenceEqual(new[] { "app.js", "environment.json", "index.html" }, exact);

        // An entry point that a default pattern leaves out has no entry point to pack.
        directory.Write("site/.env.html", "<p>env</p>");
        using var output = new MemoryStream();
        await ThrowsAsync<ArgumentException>(async () => await AssetArchive.WriteTrustedDirectoryAsync(
            site, output, new AssetArchiveDirectoryOptions { EntryPoint = ".env.html" }).ConfigureAwait(false)).ConfigureAwait(false);

        if (OperatingSystem.IsLinux())
        {
            // The pinned writer applies the same patterns; its former overload still packs every file (D039.13).
            using var pinned = new MemoryStream();
            await AssetArchive.WriteDirectoryAsync(site, pinned, new AssetArchiveDirectoryOptions(), CancellationToken.None).ConfigureAwait(false);
            pinned.Position = 0;
            SequenceEqual(new[] { "app.js", "app.js.map", "environment.json", "index.html", "nested/env.txt" },
                AssetArchive.Read(pinned).Manifest.Assets.Select(static asset => asset.RelativePath));
            using var former = new MemoryStream();
            await AssetArchive.WriteDirectoryAsync(site, former).ConfigureAwait(false);
            former.Position = 0;
            True(AssetArchive.Read(former).Manifest.TryGetAsset(".env", out _), "The former overload packs .env.");
        }
    }

    [Test, DisplayName("the packer leaves .git and .env* out, names the count and warns RAS1005 once for source maps")]
    public async Task PackerDefaultsAndSourceMapWarning()
    {
        using var directory = new TemporaryDirectory();
        string site = WriteDotfileTree(directory);
        directory.Write("site/second.css.map", "{}");
        string archive = Path.Combine(directory.Path, "out.runic-assets");

        (int exitCode, string output, string error) = await PackerProcess.RunAsync(site, archive, "--trusted-generated-output").ConfigureAwait(false);
        Equal(0, exitCode);
        Contains("Left out 6 .git and .env* entries; pass --no-default-excludes to pack them.", output);
        string[] warnings = [.. error.Split('\n').Where(static line => line.Contains("RAS1005", StringComparison.Ordinal))];
        Equal(1, warnings.Length);
        True(warnings[0].StartsWith("warning RAS1005: The archive contains 2 source maps (*.map), first 'app.js.map'.", StringComparison.Ordinal), warnings[0]);
        True(!ArchivePaths(archive).Any(static path => path.Contains(".git", StringComparison.Ordinal) || path.Contains(".env", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", ArchivePaths(archive)));

        // JSON keeps the runic.assets.pack-result/1 payload; the warning stays on standard error.
        (exitCode, output, error) = await PackerProcess.RunAsync(site, archive, "--trusted-generated-output", "--no-default-excludes", "--format", "json").ConfigureAwait(false);
        Equal(0, exitCode);
        using (JsonDocument document = JsonDocument.Parse(output))
            Equal($"{{\"archiveLength\":{new FileInfo(archive).Length}}}", document.RootElement.GetProperty("payload").GetRawText());
        Contains("warning RAS1005:", error);
        True(ArchivePaths(archive).Contains(".git/HEAD"), "--no-default-excludes packs .git.");

        // No source map, no warning; an entry point under a default pattern is RAS1002.
        (exitCode, _, error) = await PackerProcess.RunAsync(site, archive, "--trusted-generated-output", "--exclude", "app.js.map;second.css.map").ConfigureAwait(false);
        Equal(0, exitCode);
        True(!error.Contains("RAS1005", StringComparison.Ordinal), error);
        directory.Write("site/.env.html", "<p>env</p>");
        (exitCode, output, _) = await PackerProcess.RunAsync(site, archive, "--trusted-generated-output", "--entry-point", ".env.html", "--format", "json").ConfigureAwait(false);
        Equal(1, exitCode);
        using (JsonDocument document = JsonDocument.Parse(output))
            Equal("RAS1002", document.RootElement.GetProperty("fault").GetProperty("code").GetString());
    }

    [Test, DisplayName("the Runic.Assets build reports RAS1005, honours NoWarn and RunicAssetsDefaultExcludes")]
    public async Task BuildTargetsReportSourceMaps()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("App/dist/index.html", "<p>x</p>");
        directory.Write("App/dist/app.js.map", "{}");
        directory.Write("App/dist/.git/HEAD", "ref: refs/heads/main");
        directory.Write("App/dist/.env", "SECRET=1");
        string buildTransitive = Path.Combine(PackerProcess.Repository, "packages/dotnet/Runic.Assets/buildTransitive");
        string project = directory.Write("App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{buildTransitive}/Runic.Assets.props" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RunicAssetsDist>$(MSBuildProjectDirectory)/dist</RunicAssetsDist>
                <RunicAssetsPackerPath>{PackerProcess.PackerPath}</RunicAssetsPackerPath>
              </PropertyGroup>
              <Import Project="{buildTransitive}/Runic.Assets.targets" />
            </Project>
            """);
        string archive = Path.Combine(directory.Path, "App/obj/Debug/net10.0/runic-assets/App.runic-assets");

        string log = await PackWithMSBuildAsync(project).ConfigureAwait(false);
        Contains("warning RAS1005: The archive contains 1 source map (*.map), first 'app.js.map'.", log);
        SequenceEqual(new[] { "app.js.map", "index.html" }, ArchivePaths(archive));

        log = await PackWithMSBuildAsync(project, "-p:NoWarn=RAS1005").ConfigureAwait(false);
        True(!log.Contains("warning RAS1005", StringComparison.Ordinal), log);

        log = await PackWithMSBuildAsync(project, "-p:RunicAssetsDefaultExcludes=false").ConfigureAwait(false);
        SequenceEqual(new[] { ".env", ".git/HEAD", "app.js.map", "index.html" }, ArchivePaths(archive));

        async Task<string> PackWithMSBuildAsync(string projectPath, params string[] properties)
        {
            string intermediate = Path.Combine(directory.Path, "App/obj");
            if (Directory.Exists(intermediate)) Directory.Delete(intermediate, recursive: true);
            (int exitCode, string output, string error) = await PackerProcess.RunDotNetAsync(
                Path.GetDirectoryName(projectPath)!,
                ["msbuild", projectPath, "-t:RunicAssetsPackFileSystem", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-p:Configuration=Debug", .. properties])
                .ConfigureAwait(false);
            Equal(0, exitCode);
            return output + error;
        }
    }

    // A small tree with a hashed file, a nested directory and the entry point.
    private static async Task<byte[]> WriteTreeAsync(TemporaryDirectory directory)
    {
        string site = Path.Combine(directory.Path, "tree");
        if (!Directory.Exists(site))
        {
            directory.Write("tree/index.html", "<!doctype html><title>Runic</title><script src=\"assets/app-Cf3tzbYH.js\"></script>");
            directory.Write("tree/assets/app-Cf3tzbYH.js", "console.log('runic');\n");
            directory.Write("tree/assets/site.css", "body { color: rebeccapurple; }\n");
            directory.Write("tree/nested/deeper/data.json", "{\"value\":1}\n");
        }

        using var output = new MemoryStream();
        await AssetArchive.WriteTrustedDirectoryAsync(site, output).ConfigureAwait(false);
        return output.ToArray();
    }

    private static string WriteDotfileTree(TemporaryDirectory directory)
    {
        directory.Write("site/index.html", "<p>x</p>");
        directory.Write("site/app.js", "x");
        directory.Write("site/app.js.map", "{}");
        directory.Write("site/environment.json", "{}");
        directory.Write("site/.env", "SECRET=1");
        directory.Write("site/.envrc", "use flake");
        directory.Write("site/.git/HEAD", "ref: refs/heads/main");
        directory.Write("site/.git/objects/ab/cdef", "object");
        directory.Write("site/nested/.ENV.local", "SECRET=2");
        directory.Write("site/nested/.git", "gitdir: ../.git/modules/nested");
        directory.Write("site/nested/env.txt", "not a dotfile");
        directory.Write("site/nested/repo/.git/config", "[core]");
        return Path.Combine(directory.Path, "site");
    }

    private static async Task<string[]> PackedPathsAsync(string site, AssetArchiveDirectoryOptions options)
    {
        using var output = new MemoryStream();
        await AssetArchive.WriteTrustedDirectoryAsync(site, output, options).ConfigureAwait(false);
        output.Position = 0;
        return [.. (await AssetArchive.ReadAsync(output).ConfigureAwait(false)).Manifest.Assets.Select(static asset => asset.RelativePath)];
    }

    private static string[] ArchivePaths(string archive)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        return [.. zip.Entries.Select(static entry => entry.FullName)
            .Where(static name => name.StartsWith("assets/", StringComparison.Ordinal))
            .Select(static name => name["assets/".Length..])
            .Order(StringComparer.Ordinal)];
    }

    private static bool TryCreateSymbolicLink(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed: {error}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}' but found '{actual}'.");
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' in:\n{actual}");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        T[] expectedItems = [.. expected];
        T[] actualItems = [.. actual];
        if (!expectedItems.SequenceEqual(actualItems))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expectedItems)}] but found [{string.Join(", ", actualItems)}].");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    // A readable stream that cannot seek and returns at most `chunk` bytes per read.
    private sealed class ForwardOnlyStream(byte[] content, int chunk = 4096, Action? afterFirstRead = null) : Stream
    {
        private int _position;
        private Action? _afterFirstRead = afterFirstRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = Math.Min(Math.Min(chunk, buffer.Length), content.Length - _position);
            content.AsSpan(_position, read).CopyTo(buffer);
            _position += read;
            Interlocked.Exchange(ref _afterFirstRead, null)?.Invoke();
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-archive-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public string Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

// Runs the packer and dotnet as child processes, as the build does.
internal static class PackerProcess
{
    // The test runs from <repository>/tests/dotnet/Runic.Assets.Tests/bin/<Configuration>/net10.0/.
    private static readonly DirectoryInfo Output = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

    internal static string Repository { get; } = Path.GetFullPath(Path.Combine(Output.FullName, "../../../../../.."));

    internal static string PackerPath { get; } =
        Path.Combine(Repository, "tools/Runic.Assets.Packer/bin", Output.Parent!.Name, "net10.0/Runic.Assets.Packer.dll");

    internal static Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        if (!File.Exists(PackerPath))
        {
            throw new InvalidOperationException($"The packer was not found at '{PackerPath}'. Build tools/Runic.Assets.Packer first.");
        }

        return RunDotNetAsync(null, [PackerPath, .. arguments]);
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunDotNetAsync(string? workingDirectory, string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // MSBuild must not inherit the test run's own build properties.
        start.Environment.Remove("MSBuildExtensionsPath");
        start.Environment.Remove("MSBuildSDKsPath");
        start.Environment.Remove("MSBUILD_EXE_PATH");
        using Process process = Process.Start(start)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }
}

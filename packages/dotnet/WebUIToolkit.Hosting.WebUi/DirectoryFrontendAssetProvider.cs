using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WebUIToolkit.Hosting.WebUi;

/// <summary>
/// Serves only exact manifest entries below one fixed directory and verifies every
/// length and SHA-256 digest before UI initialization.
/// </summary>
public sealed class DirectoryFrontendAssetProvider : IFrontendAssetProvider
{
    private const ulong LinuxOpenCloseOnExec = 0x0008_0000;
    private const ulong LinuxOpenDirectoryFlag = 0x0001_0000;
    private const int LinuxOpenDirectory = 0x0001_0000;
    private const int LinuxOpenNoFollow = 0x0002_0000;
    private const ulong LinuxResolveNoMagicLinks = 0x02;
    private const ulong LinuxResolveNoSymbolicLinks = 0x04;
    private const ulong LinuxResolveBeneath = 0x08;
    private const long LinuxSystemCallOpenAt2 = 437;
    private const uint WindowsFileShareRead = 0x1;
    private const uint WindowsFileShareWrite = 0x2;
    private const uint WindowsFileShareDelete = 0x4;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsFileFlagBackupSemantics = 0x0200_0000;
    private const int MacOpenDirectory = 0x0010_0000;
    private const int MacOpenCloseOnExec = 0x0100_0000;
    private const int MacGetPath = 50;
    private const int MacMaximumPathBytes = 1_024;

    private readonly string _root;
    private readonly Dictionary<string, FrontendAsset> _assets;
    private readonly Action<string>? _beforeOpen;

    /// <summary>Initializes a deterministic directory-backed provider.</summary>
    public DirectoryFrontendAssetProvider(string rootDirectory, IFrontendAssetManifest manifest)
        : this(rootDirectory, manifest, beforeOpen: null)
    {
    }

    internal DirectoryFrontendAssetProvider(
        string rootDirectory,
        IFrontendAssetManifest manifest,
        Action<string>? beforeOpen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _root = Path.GetFullPath(rootDirectory);
        _beforeOpen = beforeOpen;
        _assets = new Dictionary<string, FrontendAsset>(StringComparer.Ordinal);
        foreach (FrontendAsset asset in manifest.Assets)
        {
            if (!_assets.TryAdd(asset.RelativePath, asset))
            {
                throw new ArgumentException(
                    "The frontend manifest contains a duplicate ordinal path.",
                    nameof(manifest));
            }
        }
    }

    /// <inheritdoc />
    public IFrontendAssetManifest Manifest { get; }

    /// <inheritdoc />
    public async ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException("The frontend asset directory does not exist.");
        }

        if (Manifest.Assets.Count == 0)
        {
            throw new InvalidDataException("The frontend asset manifest is empty.");
        }

        int entryPointCount = 0;
        string? previousPath = null;
        foreach (FrontendAsset asset in Manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previousPath is not null
                && StringComparer.Ordinal.Compare(previousPath, asset.RelativePath) >= 0)
            {
                throw new InvalidDataException(
                    "The frontend asset manifest is not in deterministic ordinal order.");
            }

            previousPath = asset.RelativePath;
            entryPointCount += asset.IsEntryPoint ? 1 : 0;
            ResolveDeclaredPath(asset.RelativePath);
            await using FileStream stream = OpenVerifiedFile(asset.RelativePath);
            if (stream.Length != asset.Length)
            {
                throw new InvalidDataException(
                    "A declared frontend asset is missing or has an unexpected length.");
            }

            byte[] digest = await Task.Run(
                    () => SHA256.HashData(stream),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(digest), asset.Sha256))
            {
                throw new InvalidDataException(
                    "A declared frontend asset does not match its manifest digest.");
            }
        }

        if (entryPointCount != 1)
        {
            throw new InvalidDataException(
                "The frontend asset manifest must contain exactly one entry point.");
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_assets.ContainsKey(relativePath))
        {
            throw new FileNotFoundException(
                "The requested frontend asset is not declared by the manifest.");
        }

        ResolveDeclaredPath(relativePath);
        Stream stream = OpenVerifiedFile(relativePath);
        return ValueTask.FromResult(stream);
    }

    private string ResolveDeclaredPath(string relativePath)
    {
        string fullPath = Path.GetFullPath(
            Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A frontend asset resolves outside its root.");
        }

        return fullPath;
    }

    private FileStream OpenVerifiedFile(string relativePath)
    {
        _beforeOpen?.Invoke(relativePath);
        if (OperatingSystem.IsLinux())
        {
            return OpenLinuxAnchored(relativePath);
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return OpenAndVerifyResolvedHandle(relativePath);
        }

        throw new PlatformNotSupportedException(
            "This platform does not expose the anchored file-handle operations required for frontend assets.");
    }

    private FileStream OpenLinuxAnchored(string relativePath)
    {
        nint fileSystemRootPointer = Marshal.StringToCoTaskMemUTF8("/");
        int fileSystemRootDescriptor;
        try
        {
            fileSystemRootDescriptor = LinuxOpen(
                fileSystemRootPointer,
                LinuxOpenDirectory | LinuxOpenNoFollow | checked((int)LinuxOpenCloseOnExec));
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileSystemRootPointer);
        }

        if (fileSystemRootDescriptor < 0)
        {
            throw new IOException(
                "The filesystem root could not be anchored.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        using var fileSystemRootHandle =
            new SafeFileHandle((nint)fileSystemRootDescriptor, ownsHandle: true);
        string rootRelativePath = _root.TrimStart(Path.DirectorySeparatorChar);
        if (rootRelativePath.Length == 0)
        {
            rootRelativePath = ".";
        }

        var rootHow = new LinuxOpenHow
        {
            Flags = LinuxOpenCloseOnExec | LinuxOpenDirectoryFlag,
            Resolve = LinuxResolveBeneath
                | LinuxResolveNoMagicLinks
                | LinuxResolveNoSymbolicLinks,
        };
        long rootDescriptor = LinuxOpenAt2Path(
            fileSystemRootHandle.DangerousGetHandle().ToInt32(),
            rootRelativePath,
            ref rootHow);
        if (rootDescriptor < 0)
        {
            throw new IOException(
                "The frontend asset root could not be opened without following links.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        using var rootHandle = new SafeFileHandle((nint)rootDescriptor, ownsHandle: true);
        var fileHow = new LinuxOpenHow
        {
            Flags = LinuxOpenCloseOnExec,
            Resolve = LinuxResolveBeneath
                | LinuxResolveNoMagicLinks
                | LinuxResolveNoSymbolicLinks,
        };
        long descriptor = LinuxOpenAt2Path(
            rootHandle.DangerousGetHandle().ToInt32(),
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            ref fileHow);
        if (descriptor < 0)
        {
            throw new IOException(
                "The frontend asset could not be opened without following links.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return new FileStream(handle, FileAccess.Read, bufferSize: 81_920, isAsync: false);
    }

    private static long LinuxOpenAt2Path(
        int directoryDescriptor,
        string path,
        ref LinuxOpenHow how)
    {
        nint pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return LinuxOpenAt2(
                LinuxSystemCallOpenAt2,
                directoryDescriptor,
                pathPointer,
                ref how,
                (nuint)Marshal.SizeOf<LinuxOpenHow>());
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private FileStream OpenAndVerifyResolvedHandle(string relativePath)
    {
        string fullPath = ResolveDeclaredPath(relativePath);
        FileStream? stream = null;
        SafeFileHandle? rootHandle = null;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (OperatingSystem.IsWindows())
            {
                rootHandle = WindowsCreateFile(
                    _root,
                    desiredAccess: 0,
                    WindowsFileShareRead | WindowsFileShareWrite | WindowsFileShareDelete,
                    0,
                    WindowsOpenExisting,
                    WindowsFileFlagBackupSemantics,
                    0);
                if (rootHandle.IsInvalid)
                {
                    throw new IOException(
                        "The frontend asset root could not be anchored.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                }

                string resolvedRoot = GetWindowsFinalPath(rootHandle);
                string resolvedFile = GetWindowsFinalPath(stream.SafeFileHandle);
                EnsureRootIsNotRedirected(
                    Path.GetFullPath(_root),
                    NormalizeWindowsPath(resolvedRoot),
                    StringComparison.OrdinalIgnoreCase);
                EnsureDeclaredFileIsNotRedirected(
                    Path.GetFullPath(fullPath),
                    NormalizeWindowsPath(resolvedFile),
                    StringComparison.OrdinalIgnoreCase);
                EnsureContained(resolvedRoot, resolvedFile, StringComparison.OrdinalIgnoreCase);
            }
            else if (OperatingSystem.IsMacOS())
            {
                rootHandle = OpenMacDirectory(_root);
                string resolvedRoot = GetMacFinalPath(rootHandle);
                string resolvedFile = GetMacFinalPath(stream.SafeFileHandle);
                EnsureRootIsNotRedirected(
                    Path.GetFullPath(_root),
                    resolvedRoot,
                    StringComparison.Ordinal);
                EnsureDeclaredFileIsNotRedirected(
                    Path.GetFullPath(fullPath),
                    resolvedFile,
                    StringComparison.Ordinal);
                EnsureContained(resolvedRoot, resolvedFile, StringComparison.Ordinal);
            }

            FileStream result = stream;
            stream = null;
            return result;
        }
        finally
        {
            rootHandle?.Dispose();
            stream?.Dispose();
        }
    }

    private static SafeFileHandle OpenMacDirectory(string path)
    {
        nint pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        int descriptor;
        try
        {
            descriptor = LinuxOpen(pathPointer, MacOpenDirectory | MacOpenCloseOnExec);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }

        if (descriptor < 0)
        {
            throw new IOException(
                "The frontend asset root could not be anchored.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static string GetMacFinalPath(SafeFileHandle handle)
    {
        nint buffer = Marshal.AllocHGlobal(MacMaximumPathBytes);
        try
        {
            if (MacFcntl(
                handle.DangerousGetHandle().ToInt32(),
                MacGetPath,
                buffer) != 0)
            {
                throw new IOException(
                    "A frontend asset handle could not be resolved.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return Marshal.PtrToStringUTF8(buffer)
                ?? throw new IOException("A frontend asset handle returned an empty path.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string NormalizeWindowsPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static void EnsureRootIsNotRedirected(
        string declaredRoot,
        string resolvedRoot,
        StringComparison comparison)
    {
        if (!string.Equals(
            Path.TrimEndingDirectorySeparator(declaredRoot),
            Path.TrimEndingDirectorySeparator(resolvedRoot),
            comparison))
        {
            throw new IOException(
                "The frontend asset root and all of its ancestors must not be links or reparse points.");
        }
    }

    private static void EnsureDeclaredFileIsNotRedirected(
        string declaredFile,
        string resolvedFile,
        StringComparison comparison)
    {
        if (!string.Equals(declaredFile, resolvedFile, comparison))
        {
            throw new IOException(
                "Frontend asset directories and files must not be links or reparse points.");
        }
    }

    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        uint length = WindowsGetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
        {
            throw new IOException(
                "A frontend asset handle could not be resolved.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = WindowsGetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                throw new IOException("A frontend asset handle path exceeded platform limits.");
            }
        }

        return new string(buffer, 0, checked((int)length));
    }

    private static void EnsureContained(
        string resolvedRoot,
        string resolvedFile,
        StringComparison comparison)
    {
        string prefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;
        if (!resolvedFile.StartsWith(prefix, comparison))
        {
            throw new IOException(
                "A frontend asset handle resolved outside its anchored root.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxOpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long LinuxOpenAt2(
        long number,
        int directoryDescriptor,
        nint path,
        ref LinuxOpenHow how,
        nuint size);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen(nint path, int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int MacFcntl(int descriptor, int command, nint buffer);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle WindowsCreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint WindowsGetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);
}

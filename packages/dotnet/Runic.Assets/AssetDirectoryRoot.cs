using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Runic.Assets;

/// <summary>
/// The storage boundary of a development directory: every read, directory test and enumeration resolves below
/// one root, and no path component may be a symbolic link or reparse point.
/// </summary>
internal abstract class AssetDirectoryRoot : IDisposable
{
    internal const string LinkMessage = "Asset development directories cannot contain symbolic links or reparse points.";

    /// <summary>Opens the root for this operating system: a pinned handle on Linux and macOS, a checked path on Windows.</summary>
    internal static AssetDirectoryRoot Open(string rootPath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new PosixAssetRoot(rootPath);
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsAssetRoot(rootPath);
        }

        throw new PlatformNotSupportedException(
            "DevelopmentDirectoryAssetSource is supported on Linux, Windows and macOS.");
    }

    /// <summary>Gets the path a filesystem watcher observes for this root.</summary>
    internal abstract string WatchPath { get; }

    /// <summary>Opens one file below the root for reading.</summary>
    internal abstract FileStream OpenRead(string relativePath);

    /// <summary>Gets whether a path below the root is a directory; a link or reparse point throws <see cref="InvalidDataException"/>.</summary>
    internal abstract bool IsDirectory(string relativePath);

    /// <summary>Lists the entry names of the root (an empty path) or a directory below it.</summary>
    internal abstract IReadOnlyList<string> EnumerateEntries(string relativeDirectory);

    public abstract void Dispose();

    private protected static string[] Segments(string relativePath)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("A relative asset path cannot be empty.", nameof(relativePath));
        }

        return segments;
    }
}

/// <summary>
/// Owns a no-follow directory handle on Linux or macOS. Every file opens with handle-relative <c>openat</c> walks, so a
/// replaced root or ancestor cannot redirect reads. Operations hold a reference on the handle, so disposal closes it
/// only after the operations that already started.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class PosixAssetRoot : AssetDirectoryRoot
{
    private const int ReadOnly = 0;
    private const int NotDirectoryError = 20;
    private static readonly bool s_macOS = OperatingSystem.IsMacOS();
    private static readonly int s_directory = s_macOS ? 0x0010_0000 : 0x0001_0000;
    private static readonly int s_noFollow = s_macOS ? 0x0000_0100 : 0x0002_0000;
    private static readonly int s_closeOnExec = s_macOS ? 0x0100_0000 : 0x0008_0000;
    private static readonly int s_loopError = s_macOS ? 62 : 40;
    // fcntl(F_GETPATH) and MAXPATHLEN (sys/fcntl.h, sys/param.h).
    private const int GetPathCommand = 50;
    private const int MaximumPathLength = 1024;
    private readonly SafeFileHandle _root;

    internal PosixAssetRoot(string rootPath)
    {
        int descriptor = Open(rootPath, ReadOnly | s_directory | s_noFollow | s_closeOnExec);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            // O_DIRECTORY can report a link to a directory as "not a directory" before O_NOFOLLOW reports the link.
            if (new DirectoryInfo(rootPath).LinkTarget is not null)
            {
                throw new InvalidDataException(LinkMessage);
            }

            ThrowOpenError(rootPath, error);
        }

        _root = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>
    /// Gets the path that resolves to the pinned root: its procfs link on Linux, which follows the handle after the
    /// directory moves, and the handle's current path on macOS.
    /// </summary>
    internal override string WatchPath => WithRoot(root => s_macOS
        ? GetPath(root)
        : "/proc/self/fd/" + root.ToString(CultureInfo.InvariantCulture));

    internal override FileStream OpenRead(string relativePath)
    {
        SafeFileHandle handle = OpenRelative(relativePath, finalDirectory: false);
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 81_920, isAsync: false);
            handle = null!;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal override bool IsDirectory(string relativePath)
    {
        try
        {
            using SafeFileHandle directory = OpenRelative(relativePath, finalDirectory: true);
            return true;
        }
        catch (IOException exception) when (exception.HResult == HResultFromErrno(NotDirectoryError))
        {
            return false;
        }
    }

    internal override IReadOnlyList<string> EnumerateEntries(string relativeDirectory)
    {
        // A fresh description of the directory, so the enumeration never shares a file offset with another one.
        using SafeFileHandle directory = relativeDirectory.Length == 0
            ? OpenRootCopy()
            : OpenRelative(relativeDirectory, finalDirectory: true);
        int descriptor = checked((int)directory.DangerousGetHandle());
        string openDirectoryPath = s_macOS
            ? GetPath(descriptor)
            : "/proc/self/fd/" + descriptor.ToString(CultureInfo.InvariantCulture);
        var entries = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(openDirectoryPath))
        {
            entries.Add(Path.GetFileName(entry));
        }

        return entries;
    }

    public override void Dispose() => _root.Dispose();

    private SafeFileHandle OpenRootCopy() => WithRoot(root =>
    {
        int next = OpenAt(root, ".", ReadOnly | s_directory | s_closeOnExec);
        if (next < 0)
        {
            ThrowOpenError(".");
        }

        return new SafeFileHandle((IntPtr)next, ownsHandle: true);
    });

    private SafeFileHandle OpenRelative(string relativePath, bool finalDirectory)
    {
        string[] segments = Segments(relativePath);
        return WithRoot(root =>
        {
            SafeFileHandle? current = null;
            try
            {
                int currentDescriptor = root;
                for (int index = 0; index < segments.Length; index++)
                {
                    bool directory = index < segments.Length - 1 || finalDirectory;
                    int next = OpenAt(
                        currentDescriptor,
                        segments[index],
                        ReadOnly | s_noFollow | s_closeOnExec | (directory ? s_directory : 0));
                    if (next < 0)
                    {
                        ThrowOpenError(relativePath);
                    }

                    current?.Dispose();
                    current = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                    currentDescriptor = next;
                }

                SafeFileHandle result = current ?? throw new IOException("Could not open the development asset path.");
                current = null;
                return result;
            }
            finally
            {
                current?.Dispose();
            }
        });
    }

    // Keeps the root descriptor open for the operation; a disposed root throws ObjectDisposedException.
    private T WithRoot<T>(Func<int, T> operation)
    {
        bool added = false;
        _root.DangerousAddRef(ref added);
        try
        {
            return operation(checked((int)_root.DangerousGetHandle()));
        }
        finally
        {
            if (added)
            {
                _root.DangerousRelease();
            }
        }
    }

    private static string GetPath(int descriptor)
    {
        byte[] buffer = new byte[MaximumPathLength];
        // fcntl is variadic. Apple's arm64 calling convention passes variadic arguments on the stack, so the
        // buffer follows six unused register arguments there; x64 passes it in the third register.
        int result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? FcntlGetPathArm64(descriptor, GetPathCommand, 0, 0, 0, 0, 0, 0, buffer)
            : FcntlGetPath(descriptor, GetPathCommand, buffer);
        if (result < 0)
        {
            throw new IOException(
                "Could not read the path of the development asset directory.",
                HResultFromErrno(Marshal.GetLastPInvokeError()));
        }

        int length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static void ThrowOpenError(string path) => ThrowOpenError(path, Marshal.GetLastPInvokeError());

    private static void ThrowOpenError(string path, int error)
    {
        if (error == s_loopError)
        {
            throw new InvalidDataException(LinkMessage);
        }

        throw new IOException($"Could not open the development asset path '{path}'.", HResultFromErrno(error));
    }

    private static int HResultFromErrno(int error) => unchecked((int)(0x80070000u | (uint)error));

    [DllImport("libc", EntryPoint = "open", SetLastError = true, BestFitMapping = false)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, BestFitMapping = false)]
    private static extern int OpenAt(int directoryFileDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlGetPath(int fileDescriptor, int command, [Out] byte[] path);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlGetPathArm64(
        int fileDescriptor, int command, nint unused2, nint unused3, nint unused4, nint unused5, nint unused6,
        nint unused7, [Out] byte[] path);
}

/// <summary>
/// A best-effort development root on Windows: each path component is checked for a reparse point before the file
/// opens. Windows has no handle-relative open in .NET, so a replaced root or ancestor is not pinned.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsAssetRoot : AssetDirectoryRoot
{
    private readonly string _root;
    private volatile bool _disposed;

    internal WindowsAssetRoot(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        FileAttributes attributes = File.GetAttributes(_root);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(LinkMessage);
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"The development asset root '{rootPath}' is not a directory.");
        }
    }

    internal override string WatchPath
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _root;
        }
    }

    internal override FileStream OpenRead(string relativePath)
    {
        string path = Resolve(relativePath, out FileAttributes attributes);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw new UnauthorizedAccessException($"The development asset path '{relativePath}' is a directory.");
        }

        // Writers keep working while a file is hashed or copied: a frontend build that rewrites the file must not
        // fail, and the digest check rejects content that changed while it was read.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81_920, FileOptions.None);
    }

    internal override bool IsDirectory(string relativePath)
    {
        _ = Resolve(relativePath, out FileAttributes attributes);
        return attributes.HasFlag(FileAttributes.Directory);
    }

    internal override IReadOnlyList<string> EnumerateEntries(string relativeDirectory)
    {
        string directory = _root;
        if (relativeDirectory.Length != 0)
        {
            directory = Resolve(relativeDirectory, out FileAttributes attributes);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException($"The development asset path '{relativeDirectory}' is not a directory.");
            }
        }
        else
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var entries = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            entries.Add(Path.GetFileName(entry));
        }

        return entries;
    }

    public override void Dispose() => _disposed = true;

    // Checks every component from the root down; the last one's attributes are returned.
    private string Resolve(string relativePath, out FileAttributes attributes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string current = _root;
        attributes = FileAttributes.Directory;
        foreach (string segment in Segments(relativePath))
        {
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException($"Could not open the development asset path '{relativePath}'.");
            }

            current = Path.Join(current, segment);
            attributes = File.GetAttributes(current);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(LinkMessage);
            }
        }

        return current;
    }
}

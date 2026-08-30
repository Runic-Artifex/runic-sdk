using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.Assets;

/// <summary>Owns a no-follow Linux directory handle for all development source operations.</summary>
internal sealed class LinuxAssetRoot : IDisposable
{
    private const int ReadOnly = 0;
    private const int Directory = 65_536;
    private const int NoFollow = 131_072;
    private const int CloseOnExec = 524_288;
    private const int NotDirectoryError = 20;
    private const int LoopError = 40;
    private readonly SafeFileHandle _root;

    internal LinuxAssetRoot(string rootPath)
    {
        int descriptor = Open(rootPath, ReadOnly | Directory | NoFollow | CloseOnExec);
        if (descriptor < 0)
        {
            ThrowOpenError(rootPath);
        }

        _root = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    internal FileStream OpenRead(string relativePath)
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

    internal static FileStream OpenReadNoFollow(string rootPath, string relativePath)
    {
        using var root = new LinuxAssetRoot(rootPath);
        return root.OpenRead(relativePath);
    }

    /// <summary>Gets the procfs path that resolves to this pinned directory handle.</summary>
    internal string WatchPath => "/proc/self/fd/" + _root.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal bool IsDirectory(string relativePath)
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

    internal IEnumerable<string> EnumerateEntries(string relativeDirectory)
    {
        SafeFileHandle? directory = relativeDirectory.Length == 0
            ? null
            : OpenRelative(relativeDirectory, finalDirectory: true);
        try
        {
            IntPtr descriptor = directory?.DangerousGetHandle() ?? _root.DangerousGetHandle();
            string openDirectoryPath = "/proc/self/fd/" + descriptor.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (string entry in global::System.IO.Directory.EnumerateFileSystemEntries(openDirectoryPath))
            {
                yield return Path.GetFileName(entry);
            }
        }
        finally
        {
            directory?.Dispose();
        }
    }

    public void Dispose() => _root.Dispose();

    private SafeFileHandle OpenRelative(string relativePath, bool finalDirectory)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("A relative asset path cannot be empty.", nameof(relativePath));
        }

        SafeFileHandle? current = null;
        try
        {
            int currentDescriptor = checked((int)_root.DangerousGetHandle());
            for (int index = 0; index < segments.Length; index++)
            {
                bool directory = index < segments.Length - 1 || finalDirectory;
                int next = OpenAt(
                    currentDescriptor,
                    segments[index],
                    ReadOnly | NoFollow | CloseOnExec | (directory ? Directory : 0));
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
    }

    private static void ThrowOpenError(string path)
    {
        int error = Marshal.GetLastPInvokeError();
        if (error == LoopError)
        {
            throw new InvalidDataException("Asset development directories cannot contain symbolic links or reparse points.");
        }

        throw new IOException($"Could not open the development asset path '{path}'.", HResultFromErrno(error));
    }

    private static int HResultFromErrno(int error) => unchecked((int)(0x80070000u | (uint)error));

    [DllImport("libc", EntryPoint = "open", SetLastError = true, BestFitMapping = false)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, BestFitMapping = false)]
    private static extern int OpenAt(int directoryFileDescriptor, string path, int flags);
}

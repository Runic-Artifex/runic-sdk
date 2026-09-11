using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

using Runic.Platform.Administration.Windows.Shares;
namespace Runic.Platform.Administration.Windows.Internal.Backends;

/// <summary>Local or remote SMB share administration using NetAPI and the current Windows identity.</summary>
internal sealed partial class HandwrittenShareClient
{
    private readonly string? _server;
    /// <summary>Creates a share client; null targets the local computer.</summary>
    public HandwrittenShareClient(string? server = null)
    {
        NativeError.Windows();
        if (server is not null) NativeError.Text(server, nameof(server));
        _server = server;
    }

    /// <summary>Enumerates share names/types/descriptions without requiring security descriptor access.</summary>
    public unsafe ImmutableArray<ShareSummary> Enumerate()
    {
        var result = ImmutableArray.CreateBuilder<ShareSummary>();
        uint resume = 0;
        int status;
        do
        {
            status = NetShareEnum(_server, 1, out var buffer, 65536, out var count, out _, ref resume);
            try
            {
                if (status is not (0 or 234)) throw NativeError.Win32("Enumerate SMB shares", status);
                var rows = (Info1*)buffer;
                for (var i = 0u; i < count; i++) result.Add(new(Text(rows[i].Name), Text(rows[i].Remark), rows[i].Type));
                if (status == 234 && count == 0) throw NativeError.Win32("Read SMB share page", 13);
            }
            finally { if (buffer != 0) _ = NetApiBufferFree(buffer); }
        } while (status == 234);
        return result.ToImmutable();
    }

    /// <summary>Reads a share including security; null means absent, while denied security access remains an error.</summary>
    public unsafe ShareSnapshot? Find(string name)
    {
        NativeError.Text(name, nameof(name));
        var status = NetShareGetInfo(_server, name, 502, out var buffer);
        try
        {
            if (status == 2310) return null;
            if (status != 0) throw NativeError.Win32("Read SMB share", status);
            var value = (Info502*)buffer;
            return new(Text(value->Name), Text(value->Path), Text(value->Remark), value->Type,
                value->MaximumUses, value->CurrentUses, ShareSecurity.ReadDescriptor(value->Descriptor));
        }
        finally { if (buffer != 0) _ = NetApiBufferFree(buffer); }
    }

    /// <summary>Creates a disk share without replacing an existing share.</summary>
    public unsafe void Create(ShareSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        NativeError.Text(specification.Name, nameof(specification.Name));
        NativeError.Text(specification.Path, nameof(specification.Path));
        NativeError.Text(specification.Description, nameof(specification.Description), true);
        var descriptor = specification.SecurityDescriptor is { } supplied ? ShareSecurity.ValidateDescriptor(supplied) : null;
        fixed (char* name = specification.Name)
        fixed (char* path = specification.Path)
        fixed (char* description = specification.Description)
        fixed (byte* security = descriptor)
        {
            var info = new Info502 { Name = (nint)name, Path = (nint)path, Remark = (nint)description,
                MaximumUses = specification.MaximumUses, Descriptor = (nint)security };
            var status = NetShareAdd(_server, 502, &info, out _);
            if (status != 0) throw NativeError.Win32("Create SMB share", status);
        }
    }

    /// <summary>Updates only supplied fields. Separate native updates are not transactional; re-read after a failure.</summary>
    public unsafe void Update(string name, ShareUpdate update)
    {
        NativeError.Text(name, nameof(name));
        ArgumentNullException.ThrowIfNull(update);
        if (update.Description is { } description) NativeError.Text(description, nameof(update.Description), true);
        var descriptor = update.SecurityDescriptor is { } supplied ? ShareSecurity.ValidateDescriptor(supplied) : null;
        if (update.Description is { } remark)
            fixed (char* text = remark)
            {
                var pointer = text;
                CheckSet(name, 1004, &pointer);
            }
        if (update.MaximumUses is { } max) CheckSet(name, 1006, &max);
        if (descriptor is not null)
            fixed (byte* security = descriptor)
            {
                var info = new Info1501 { Descriptor = (nint)security };
                CheckSet(name, 1501, &info);
            }
    }

    /// <summary>Deletes a share; active clients may be disconnected. Returns false only if the share was absent.</summary>
    public bool Delete(string name)
    {
        NativeError.Text(name, nameof(name));
        var status = NetShareDel(_server, name, 0);
        if (status == 2310) return false;
        if (status != 0) throw NativeError.Win32("Delete SMB share", status);
        return true;
    }

    private unsafe void CheckSet(string name, uint level, void* value)
    {
        var status = NetShareSetInfo(_server, name, level, value, out _);
        if (status != 0) throw NativeError.Win32("Update SMB share", status);
    }

    private static string Text(nint value) => Marshal.PtrToStringUni(value) ?? "";
    [StructLayout(LayoutKind.Sequential)] private struct Info1 { internal nint Name; internal uint Type; internal nint Remark; }
    [StructLayout(LayoutKind.Sequential)] private struct Info502
    {
        internal nint Name; internal uint Type; internal nint Remark;
        internal uint Permissions, MaximumUses, CurrentUses;
        internal nint Path, Password; internal uint Reserved; internal nint Descriptor;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Info1501 { internal uint Reserved; internal nint Descriptor; }
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareEnum(string? server, uint level, out nint buffer, uint preferredLength, out uint count, out uint total, ref uint resume);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareGetInfo(string? server, string name, uint level, out nint buffer);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int NetShareAdd(string? server, uint level, void* buffer, out uint parameterError);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int NetShareSetInfo(string? server, string name, uint level, void* buffer, out uint parameterError);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareDel(string? server, string name, uint reserved);
    [LibraryImport("netapi32.dll")] private static partial int NetApiBufferFree(nint buffer);
}

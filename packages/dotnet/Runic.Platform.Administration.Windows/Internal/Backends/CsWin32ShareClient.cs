using System.Collections.Immutable;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Runic.Platform.Administration.Windows.Shares;
namespace Runic.Platform.Administration.Windows.Internal.Backends;
[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
internal sealed unsafe class CsWin32ShareClient : IShareClient
{
    private readonly string? _server;
    internal CsWin32ShareClient(string? server = null)
    {
        NativeError.Windows();
        if (server is not null) NativeError.Text(server, nameof(server));
        _server = server;
    }
    public ImmutableArray<ShareSummary> Enumerate()
    {
        var rows = ImmutableArray.CreateBuilder<ShareSummary>();
        uint resume = 0, status;
        fixed (char* server = _server)
        do
        {
            byte* buffer = null; uint count = 0, total = 0;
            status = PInvoke.NetShareEnum(new PWSTR(server), 1, &buffer, 65536, &count, &total, &resume);
            try
            {
                if (status is not (0 or 234)) throw NativeError.Win32("Enumerate SMB shares", (int)status);
                var items = (SHARE_INFO_1*)buffer;
                for (uint i = 0; i < count; i++) rows.Add(new(items[i].shi1_netname.ToString(), items[i].shi1_remark.ToString(), (uint)items[i].shi1_type));
                if (status == 234 && count == 0) throw NativeError.Win32("Read SMB share page", 13);
            }
            finally { if (buffer != null) _ = PInvoke.NetApiBufferFree(buffer); }
        } while (status == 234);
        return rows.ToImmutable();
    }
    public ShareSnapshot? Find(string name)
    {
        NativeError.Text(name, nameof(name));
        fixed (char* server = _server)
        fixed (char* share = name)
        {
            byte* buffer = null;
            var status = PInvoke.NetShareGetInfo(new PWSTR(server), new PWSTR(share), 502, &buffer);
            try
            {
                if (status == 2310) return null;
                if (status != 0) throw NativeError.Win32("Read SMB share", (int)status);
                var item = (SHARE_INFO_502*)buffer;
                return new(item->shi502_netname.ToString(), item->shi502_path.ToString(), item->shi502_remark.ToString(),
                    (uint)item->shi502_type, item->shi502_max_uses, item->shi502_current_uses,
                    ShareSecurity.ReadDescriptor((nint)item->shi502_security_descriptor.Value));
            }
            finally { if (buffer != null) _ = PInvoke.NetApiBufferFree(buffer); }
        }
    }
    public void Create(ShareSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        NativeError.Text(specification.Name, nameof(specification.Name));
        NativeError.Text(specification.Path, nameof(specification.Path));
        NativeError.Text(specification.Description, nameof(specification.Description), true);
        var descriptor = specification.SecurityDescriptor is { } supplied ? ShareSecurity.ValidateDescriptor(supplied) : null;
        fixed (char* server = _server)
        fixed (char* name = specification.Name)
        fixed (char* path = specification.Path)
        fixed (char* description = specification.Description)
        fixed (byte* security = descriptor)
        {
            var info = new SHARE_INFO_502 { shi502_netname = new PWSTR(name), shi502_path = new PWSTR(path),
                shi502_remark = new PWSTR(description), shi502_max_uses = specification.MaximumUses,
                shi502_security_descriptor = new PSECURITY_DESCRIPTOR(security) };
            var status = PInvoke.NetShareAdd(new PWSTR(server), 502, (byte*)&info, null);
            if (status != 0) throw NativeError.Win32("Create SMB share", (int)status);
        }
    }
    public void Update(string name, ShareUpdate update)
    {
        NativeError.Text(name, nameof(name)); ArgumentNullException.ThrowIfNull(update);
        if (update.Description is { } description) NativeError.Text(description, nameof(update.Description), true);
        var descriptor = update.SecurityDescriptor is { } supplied ? ShareSecurity.ValidateDescriptor(supplied) : null;
        if (update.Description is { } remark)
            fixed (char* text = remark) { var info = new SHARE_INFO_1004 { shi1004_remark = new PWSTR(text) }; Set(name, 1004, &info); }
        if (update.MaximumUses is { } max) { var info = new SHARE_INFO_1006 { shi1006_max_uses = max }; Set(name, 1006, &info); }
        if (descriptor is not null)
            fixed (byte* security = descriptor)
            {
                var info = new SHARE_INFO_1501 { shi1501_security_descriptor = new PSECURITY_DESCRIPTOR(security) };
                Set(name, 1501, &info);
            }
    }
    private void Set(string name, uint level, void* info)
    {
        fixed (char* server = _server)
        fixed (char* share = name)
        {
            var status = PInvoke.NetShareSetInfo(new PWSTR(server), new PWSTR(share), level, (byte*)info, null);
            if (status != 0) throw NativeError.Win32("Update SMB share", (int)status);
        }
    }
    public bool Delete(string name)
    {
        NativeError.Text(name, nameof(name));
        fixed (char* server = _server)
        fixed (char* share = name)
        {
            var status = PInvoke.NetShareDel(new PWSTR(server), new PWSTR(share), 0);
            if (status == 2310) return false;
            if (status != 0) throw NativeError.Win32("Delete SMB share", (int)status);
            return true;
        }
    }
}

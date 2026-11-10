using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Runic.Application.Views.CsWebUi;

// The URL reservations of http.sys (netsh http show urlacl), which every user may read. Without a reservation, http.sys
// lets a user without administrator rights register http://localhost:{port}/ only where no other registration overlaps
// it, and refuses another process, of any user, a URL under it. A reservation that an administrator made for the port
// replaces that rule with its own access list: a user it lets listen may then register a URL under the endpoint's, which
// http.sys prefers for the requests it names. The blob endpoint therefore does not use a port that a reservation names.
[SupportedOSPlatform("windows")]
internal static partial class CsWebUiUrlReservations
{
    private const uint InitializeConfig = 2; // HTTP_INITIALIZE_CONFIG
    private const int UrlAclInfo = 2; // HttpServiceConfigUrlAclInfo
    private const int QueryNext = 1; // HttpServiceConfigQueryNext
    private const uint InsufficientBuffer = 122;
    private const uint NoMoreItems = 259;

    // The reserved URL prefixes that name port, whatever their scheme and host.
    internal static IReadOnlyList<string> Naming(int port) => All().Where(prefix => PortOf(prefix) == port).ToList();

    // The port of a reserved URL prefix (scheme://host:port/path, host possibly an IPv6 literal); null when it has none.
    internal static int? PortOf(string prefix)
    {
        var start = prefix.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return null;
        var authority = prefix[(start + 3)..];
        var slash = authority.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0) authority = authority[..slash];
        var colon = authority.LastIndexOf(':');
        if (colon < 0 || authority.LastIndexOf(']') > colon) return null;
        return int.TryParse(authority.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
    }

    private static unsafe List<string> All()
    {
        var version = new ApiVersion { Major = 1, Minor = 0 };
        if (HttpInitialize(version, InitializeConfig, 0) is var started and not 0)
            throw new Win32Exception((int)started, "http.sys could not be queried for URL reservations.");
        var prefixes = new List<string>();
        var buffer = new byte[1024];
        try
        {
            // The list is read by index (Token), not as a snapshot: a reservation deleted between two queries shifts the
            // later ones down, so one of them can be missed. Only an administrator can change the list, and one can
            // reserve the port after this read anyway, so the endpoint's check does not cover an administrator.
            for (uint token = 0; ; token++)
            {
                var query = new UrlAclQuery { QueryDesc = QueryNext, Token = token };
                uint result, length = 0;
                while (true)
                {
                    fixed (byte* output = buffer)
                        result = HttpQueryServiceConfiguration(0, UrlAclInfo, &query, (uint)sizeof(UrlAclQuery), output, (uint)buffer.Length, &length, 0);
                    if (result != InsufficientBuffer) break;
                    buffer = new byte[Math.Max(length, (uint)buffer.Length * 2)];
                }
                if (result == NoMoreItems) return prefixes;
                if (result != 0) throw new Win32Exception((int)result, "http.sys could not list its URL reservations.");
                fixed (byte* output = buffer)
                    if (((UrlAclSet*)output)->UrlPrefix is var prefix and not null)
                        prefixes.Add(new string(prefix));
            }
        }
        finally { _ = HttpTerminate(InitializeConfig, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ApiVersion
    {
        public ushort Major;
        public ushort Minor;
    }

    // HTTP_SERVICE_CONFIG_URLACL_QUERY.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UrlAclQuery
    {
        public int QueryDesc;
        public char* UrlPrefix;
        public uint Token;
    }

    // HTTP_SERVICE_CONFIG_URLACL_SET: the reserved prefix and its security descriptor.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UrlAclSet
    {
        public char* UrlPrefix;
        public char* SecurityDescriptor;
    }

    [LibraryImport("httpapi")]
    private static partial uint HttpInitialize(ApiVersion version, uint flags, nint reserved);

    [LibraryImport("httpapi")]
    private static partial uint HttpTerminate(uint flags, nint reserved);

    [LibraryImport("httpapi")]
    private static unsafe partial uint HttpQueryServiceConfiguration(nint serviceHandle, int configId, void* input, uint inputLength,
        void* output, uint outputLength, uint* returnLength, nint overlapped);
}

using System.Security.Cryptography;
using System.Text;

namespace Runic.Desktop.Internal;

// Compares an offered secret, such as a per-webview message token, with the expected one in constant time over their
// UTF-8 bytes, as the package's other credential checks do (SEC-24). A length difference is decided by FixedTimeEquals
// itself, so the comparison reveals no more than it does.
internal static class FixedTimeText
{
    internal static bool Matches(string? offered, string expected) =>
        offered is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(expected));
}

using System.Security.Cryptography;
using System.Text;

namespace Runic.Platform.Linux.Portal;

// libsecret's file backend format (libsecret 0.21, secret-file-collection.c, secret-file-item.c and egg-keyring1-*.c),
// which libsecret uses inside Flatpak and Snap with the Secret portal's secret as the password, at
// $XDG_DATA_HOME/keyrings/default.keyring:
//
//   "GnomeKeyring\n\r\0\n", major 1, minor 0, then the GVariant (uayutua(a{say}ay)) of the salt size, the salt (32
//   bytes), the PBKDF2 iteration count (100 000), the modification time (Unix seconds), a usage count and the items.
//   An item is its attributes, each value hashed as HMAC-SHA256(key, value) and sorted by name, and its encrypted
//   blob: AES-128-CBC with PKCS #7 padding of the GVariant (a{ss}sttay) of attributes, label, created and modified
//   times and the secret, followed by the 16-byte IV and HMAC-SHA256(key, ciphertext || IV). The key is
//   PBKDF2-HMAC-SHA256(password, salt, iterations) to 16 bytes.
//
// The integers are written little-endian (GUINT32_TO_LE), as GVariant on the little-endian machines .NET runs on.
internal sealed class LibsecretKeyring
{
    internal static ReadOnlySpan<byte> Header => "GnomeKeyring\n\r\0\n"u8;
    internal const int SaltSize = 32;
    internal const uint DefaultIterations = 100_000;
    internal const int KeySize = 16;
    private const int MacSize = 32;
    private const int BlockSize = 16;

    private static readonly GVariantCodec.GType FileType = GVariantCodec.Parse("(uayutua(a{say}ay))");
    private static readonly GVariantCodec.GType ItemType = GVariantCodec.Parse("(a{ss}sttay)");

    internal byte[] Salt { get; private init; } = [];
    internal uint Iterations { get; private init; }
    internal ulong Modified { get; set; }
    internal uint UsageCount { get; set; }
    // Each item's hashed attributes and encrypted blob, in file order.
    internal List<(List<(string Name, byte[] Mac)> Attributes, byte[] Blob)> Items { get; } = [];

    // An empty keyring with a new salt, as libsecret starts one.
    internal static LibsecretKeyring Create() => new()
    {
        Salt = RandomNumberGenerator.GetBytes(SaltSize), Iterations = DefaultIterations,
        Modified = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    /// <exception cref="FormatException">The file is not a libsecret keyring of version 1.0.</exception>
    internal static LibsecretKeyring Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < Header.Length + 2 || !file.StartsWith(Header)) throw new FormatException("The file is not a libsecret keyring.");
        if (file[Header.Length] != 1 || file[Header.Length + 1] != 0) throw new FormatException("The keyring has another version.");
        var fields = (object[])GVariantCodec.Deserialize(FileType, file[(Header.Length + 2)..]);
        byte[] salt = (byte[])fields[1];
        if ((uint)fields[0] != salt.Length || salt.Length == 0 || (uint)fields[2] == 0) throw new FormatException("The keyring's salt or iteration count is invalid.");
        var keyring = new LibsecretKeyring { Salt = salt, Iterations = (uint)fields[2], Modified = (ulong)fields[3], UsageCount = (uint)fields[4] };
        foreach (object[] item in ((List<object>)fields[5]).Cast<object[]>())
        {
            var attributes = ((List<object>)item[0]).Cast<object[]>().Select(static entry => ((string)entry[0], (byte[])entry[1])).ToList();
            keyring.Items.Add((attributes, (byte[])item[1]));
        }
        return keyring;
    }

    internal byte[] Serialize()
    {
        var items = Items.Select(static item => (object)new object[]
        {
            item.Attributes.Select(static attribute => (object)new object[] { attribute.Name, attribute.Mac }).ToList(), item.Blob,
        }).ToList();
        byte[] body = GVariantCodec.Serialize(FileType, new object[] { (uint)Salt.Length, Salt, Iterations, Modified, UsageCount, items });
        return [.. Header, 1, 0, .. body];
    }

    // The 16-byte key; the caller clears it.
    internal byte[] DeriveKey(ReadOnlySpan<byte> password) =>
        Rfc2898DeriveBytes.Pbkdf2(password, Salt, checked((int)Iterations), HashAlgorithmName.SHA256, KeySize);

    // Whether an item's hashed attributes include every attribute, as libsecret searches.
    internal static bool Matches(List<(string Name, byte[] Mac)> hashed, byte[] key, IReadOnlyDictionary<string, string> attributes)
    {
        Span<byte> mac = stackalloc byte[MacSize];
        foreach (var (name, value) in attributes)
        {
            int index = hashed.FindIndex(attribute => attribute.Name == name);
            if (index < 0 || hashed[index].Mac.Length != MacSize) return false;
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value), mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, hashed[index].Mac)) return false;
        }
        return true;
    }

    // An item's attributes hashed and sorted by name (strcmp order), as libsecret writes them.
    internal static List<(string Name, byte[] Mac)> Hash(byte[] key, IReadOnlyDictionary<string, string> attributes) =>
        attributes.OrderBy(static attribute => Encoding.UTF8.GetBytes(attribute.Key), ByteOrder.Instance)
            .Select(attribute => (attribute.Key, HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(attribute.Value)))).ToList();

    internal static byte[] Encrypt(byte[] key, Item item)
    {
        var attributes = item.Attributes.Select(static attribute => (object)new object[] { attribute.Key, attribute.Value }).ToList();
        byte[] plain = GVariantCodec.Serialize(ItemType, new object[] { attributes, item.Label, item.Created, item.Modified, item.Secret });
        byte[] iv = RandomNumberGenerator.GetBytes(BlockSize);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] encrypted = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
            byte[] blob = [.. encrypted, .. iv, .. new byte[MacSize]];
            HMACSHA256.HashData(key, blob.AsSpan(0, encrypted.Length + BlockSize), blob.AsSpan(encrypted.Length + BlockSize));
            CryptographicOperations.ZeroMemory(encrypted);
            return blob;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // The item, or null when its MAC or padding does not verify under the key.
    /// <exception cref="FormatException">The decrypted item is not of libsecret's item type.</exception>
    internal static Item? Decrypt(byte[] key, byte[] blob)
    {
        if (blob.Length < BlockSize + MacSize || (blob.Length - MacSize) % BlockSize != 0 || blob.Length - MacSize - BlockSize == 0) return null;
        int cipherLength = blob.Length - MacSize - BlockSize;
        Span<byte> mac = stackalloc byte[MacSize];
        HMACSHA256.HashData(key, blob.AsSpan(0, cipherLength + BlockSize), mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, blob.AsSpan(cipherLength + BlockSize))) return null;
        byte[] plain;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            try { plain = aes.DecryptCbc(blob.AsSpan(0, cipherLength), blob.AsSpan(cipherLength, BlockSize), PaddingMode.PKCS7); }
            catch (CryptographicException) { return null; }
        }
        try
        {
            var fields = (object[])GVariantCodec.Deserialize(ItemType, plain);
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (object[] entry in ((List<object>)fields[0]).Cast<object[]>()) attributes[(string)entry[0]] = (string)entry[1];
            return new Item(attributes, (string)fields[1], (ulong)fields[2], (ulong)fields[3], (byte[])fields[4]);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal sealed record Item(IReadOnlyDictionary<string, string> Attributes, string Label, ulong Created, ulong Modified, byte[] Secret);

    private sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

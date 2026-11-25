using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Runic.Assets.Tests;

// Edits of a written archive for the tests that alter an entry's bytes.
internal static class ZipTestEdits
{
    // Records the CRC-32 of a stored entry's current bytes in its local header and its central directory record, so
    // that a test which changed those bytes (keeping their length) reaches the digest check rather than the CRC-32's.
    internal static void UpdateStoredCrc(byte[] archive, string entryName)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        for (int central = 0; central <= archive.Length - 46; central++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central)) != 0x02014B50
                || BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(central + 28)) != name.Length
                || !archive.AsSpan(central + 46, name.Length).SequenceEqual(name))
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(central + 10)) != 0)
            {
                throw new InvalidOperationException($"'{entryName}' is not stored.");
            }

            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 20)));
            int local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 42)));
            int data = local + 30
                + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(local + 26))
                + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(local + 28));
            uint crc = AssetHashing.Crc32(archive.AsSpan(data, size));
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(central + 16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(local + 14), crc);
            return;
        }

        throw new InvalidDataException($"The archive has no central directory record for '{entryName}'.");
    }
}

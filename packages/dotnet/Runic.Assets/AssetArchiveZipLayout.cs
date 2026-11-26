using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Runic.Assets;

// The ZIP container rules that System.IO.Compression.ZipArchive neither checks nor exposes (specs/assets/archive-v1.md,
// "ZIP container"). ZipArchive reads the end records and the central directory; this reads them again for each entry's
// local header offset, then requires a local header there with the central record's name and compression method, no
// two entries' bytes (local header to end of data) overlapping each other or the central directory, and compression
// method 0 or 8. The reader inflates an entry from the archive's bytes through ArchiveEntryData, so that deflate data
// which inflates past the entry's size is found: ZipArchive's entry streams stop at that size.
internal static class ArchiveZipLayout
{
    internal const ushort Stored = 0;
    internal const ushort Deflate = 8;

    private const uint LocalHeaderSignature = 0x04034B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint Zip64EndSignature = 0x06064B50;
    private const int EndLength = 22;
    private const int Zip64LocatorLength = 20;
    private const int Zip64EndLength = 56;
    private const int CentralHeaderLength = 46;
    private const int LocalHeaderLength = 30;
    private const ushort Zip64ExtraId = 0x0001;

    // The data of every entry of the archive in stream, in central directory order, which is the order of
    // ZipArchive.Entries. ZipArchive has read the same records, so its entries must agree with them.
    internal static ArchiveEntryData[] Locate(Stream stream, IReadOnlyList<ZipArchiveEntry> entries)
    {
        long archiveLength = stream.Length;
        (long directoryOffset, long directoryLength, long count) = ReadEnd(stream, archiveLength);
        long directoryEnd = directoryOffset + directoryLength;
        if (directoryOffset < 0 || directoryLength < 0 || directoryEnd > archiveLength || count != entries.Count)
        {
            throw Invalid("its end record does not match its central directory");
        }

        var located = new ArchiveEntryData[entries.Count];
        Span<byte> header = stackalloc byte[CentralHeaderLength];
        long position = directoryOffset;
        for (int index = 0; index < entries.Count; index++)
        {
            ZipArchiveEntry entry = entries[index];
            if (position > directoryEnd - CentralHeaderLength)
            {
                throw Invalid("its central directory is truncated");
            }

            ReadAt(stream, position, header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralHeaderSignature)
            {
                throw Invalid("its central directory is damaged");
            }

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            ulong compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            ulong length = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            long recordEnd = position + CentralHeaderLength + nameLength + extraLength + commentLength;
            if (recordEnd > directoryEnd)
            {
                throw Invalid("its central directory is truncated");
            }

            byte[] name = new byte[nameLength];
            ReadAt(stream, position + CentralHeaderLength, name);
            byte[] extra = new byte[extraLength];
            ReadAt(stream, position + CentralHeaderLength + nameLength, extra);
            ReadZip64Extra(extra, ref length, ref compressedLength, ref localOffset);
            position = recordEnd;

            // The same record as ZipArchive's entry, or the two readers would read different archives.
            if ((ulong)entry.Length != length || (ulong)entry.CompressedLength != compressedLength || entry.Crc32 != crc)
            {
                throw Invalid("its central directory cannot be read consistently");
            }

            if (method is not (Stored or Deflate))
            {
                throw new InvalidDataException(
                    $"The asset archive entry '{entry.FullName}' has compression method {method}; only 0 (stored) and 8 (deflate) are read.");
            }

            if (localOffset > (ulong)(archiveLength - LocalHeaderLength))
            {
                throw Invalid($"the local header of entry '{entry.FullName}' is outside the archive");
            }

            long local = (long)localOffset;
            Span<byte> localHeader = header[..LocalHeaderLength];
            ReadAt(stream, local, localHeader);
            int localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[26..]);
            int localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[28..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != LocalHeaderSignature
                || local > archiveLength - LocalHeaderLength - localNameLength)
            {
                throw Invalid($"entry '{entry.FullName}' has no local header at its offset");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(localHeader[8..]) != method)
            {
                throw Invalid($"the local header of entry '{entry.FullName}' has another compression method than its central record");
            }

            byte[] localName = new byte[localNameLength];
            ReadAt(stream, local + LocalHeaderLength, localName);
            if (!localName.AsSpan().SequenceEqual(name))
            {
                throw Invalid($"the local header of entry '{entry.FullName}' has another name than its central record");
            }

            long dataOffset = local + LocalHeaderLength + localNameLength + localExtraLength;
            if (dataOffset > archiveLength || compressedLength > (ulong)(archiveLength - dataOffset))
            {
                throw Invalid($"the data of entry '{entry.FullName}' ends outside the archive");
            }

            long dataEnd = dataOffset + (long)compressedLength;
            if (local < directoryEnd && directoryOffset < dataEnd)
            {
                throw Invalid($"entry '{entry.FullName}' overlaps the central directory");
            }

            located[index] = new ArchiveEntryData(stream, entry.FullName, local, dataOffset, (long)compressedLength, method, crc);
        }

        // No two entries' bytes overlap, so no bytes are read under two names. Sorting the entries, a reference type,
        // shares the framework's sort code instead of adding an instantiation to every NativeAOT app.
        var byOffset = (ArchiveEntryData[])located.Clone();
        Array.Sort(byOffset, static (x, y) => x.LocalOffset.CompareTo(y.LocalOffset));
        for (int index = 1; index < byOffset.Length; index++)
        {
            if (byOffset[index].LocalOffset < byOffset[index - 1].DataEnd)
            {
                throw Invalid($"entries '{byOffset[index - 1].Name}' and '{byOffset[index].Name}' overlap");
            }
        }

        return located;
    }

    // The central directory's offset, length and entry count: the end record's, or for each field at its marker the
    // ZIP64 end record's. The end record is the last one in the file, as ZipArchive finds it.
    private static (long Offset, long Length, long Count) ReadEnd(Stream stream, long archiveLength)
    {
        int tailLength = (int)Math.Min(archiveLength, EndLength + ushort.MaxValue);
        byte[] tail = new byte[tailLength];
        ReadAt(stream, archiveLength - tailLength, tail);
        int end = -1;
        for (int index = tailLength - EndLength; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == EndSignature)
            {
                end = index;
                break;
            }
        }

        if (end < 0)
        {
            throw Invalid("it has no end of central directory record");
        }

        ReadOnlySpan<byte> record = tail.AsSpan(end, EndLength);
        long count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        bool countMarker = count == ushort.MaxValue;
        bool lengthMarker = length == uint.MaxValue;
        bool offsetMarker = offset == uint.MaxValue;
        long endPosition = archiveLength - tailLength + end;
        Span<byte> locator = stackalloc byte[Zip64LocatorLength];
        if (endPosition >= Zip64LocatorLength)
        {
            ReadAt(stream, endPosition - Zip64LocatorLength, locator);
        }

        if (endPosition < Zip64LocatorLength || BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
        {
            if (countMarker || lengthMarker || offsetMarker)
            {
                throw Invalid("its end record has ZIP64 markers but there is no ZIP64 end record");
            }

            return (offset, length, count);
        }

        ulong zip64End = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64End > (ulong)(archiveLength - Zip64EndLength))
        {
            throw Invalid("its ZIP64 end record is outside the archive");
        }

        Span<byte> zip64 = stackalloc byte[Zip64EndLength];
        ReadAt(stream, (long)zip64End, zip64);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndSignature)
        {
            throw Invalid("it has no ZIP64 end record at the locator's offset");
        }

        return (
            offsetMarker ? ToInt64(BinaryPrimitives.ReadUInt64LittleEndian(zip64[48..])) : offset,
            lengthMarker ? ToInt64(BinaryPrimitives.ReadUInt64LittleEndian(zip64[40..])) : length,
            countMarker ? ToInt64(BinaryPrimitives.ReadUInt64LittleEndian(zip64[32..])) : count);
    }

    // A ZIP64 extended information extra field holds, in this order, each of the uncompressed size, the compressed size
    // and the local header offset whose classic field is at its marker.
    private static void ReadZip64Extra(ReadOnlySpan<byte> extra, ref ulong length, ref ulong compressedLength, ref ulong localOffset)
    {
        bool lengthMarker = length == uint.MaxValue;
        bool compressedMarker = compressedLength == uint.MaxValue;
        bool offsetMarker = localOffset == uint.MaxValue;
        if (!lengthMarker && !compressedMarker && !offsetMarker)
        {
            return;
        }

        while (extra.Length >= 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (size > extra.Length - 4)
            {
                break;
            }

            ReadOnlySpan<byte> field = extra.Slice(4, size);
            extra = extra[(4 + size)..];
            if (id != Zip64ExtraId)
            {
                continue;
            }

            if (lengthMarker)
            {
                length = Next(ref field);
            }

            if (compressedMarker)
            {
                compressedLength = Next(ref field);
            }

            if (offsetMarker)
            {
                localOffset = Next(ref field);
            }

            return;
        }

        throw Invalid("an entry's ZIP64 extra field is missing");

        static ulong Next(ref ReadOnlySpan<byte> field)
        {
            if (field.Length < 8)
            {
                throw Invalid("an entry's ZIP64 extra field is truncated");
            }

            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(field);
            field = field[8..];
            return value;
        }
    }

    private static long ToInt64(ulong value) =>
        value <= long.MaxValue ? (long)value : throw Invalid("a ZIP64 field is out of range");

    private static void ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
        {
            throw Invalid("a record is outside the archive");
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
    }

    private static InvalidDataException Invalid(string reason) => new($"The asset archive is not a valid ZIP file: {reason}.");
}

// One entry's data in the archive's stream: its local header's offset, where its data starts, its compressed length, its
// method (0 or 8) and the CRC-32 that its central record gives. The reader serializes access to the stream
// (AssetArchiveContent).
internal sealed class ArchiveEntryData(
    Stream archive,
    string name,
    long localOffset,
    long dataOffset,
    long compressedLength,
    ushort method,
    uint crc32)
{
    internal string Name { get; } = name;

    internal long LocalOffset { get; } = localOffset;

    internal long DataEnd => dataOffset + compressedLength;

    internal uint Crc32 { get; } = crc32;

    // The entry's bytes as they are in the archive: inflated when the method is 8.
    internal Stream Open()
    {
        var data = new DataStream(archive, dataOffset, compressedLength);
        return method == ArchiveZipLayout.Deflate ? new DeflateStream(data, CompressionMode.Decompress) : data;
    }

    // A read-only window on the archive stream, which seeks before every read since other entries share the stream.
    private sealed class DataStream(Stream archive, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = length - _position;
            if (remaining <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            archive.Position = start + _position;
            int read = archive.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

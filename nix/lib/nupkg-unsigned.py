"""Write a NuGet package without its signature, as NuGet hashes it for packages.lock.json.

NuGet records a signed package's contentHash over the package without its signature file, which signing adds as
the last entry of the archive. Removing that entry, its central directory record and adjusting the end of central
directory gives exactly those bytes, which are themselves a valid, unsigned package. An unsigned package is
written unchanged.

usage: nupkg-unsigned.py <package.nupkg> <output.nupkg>
"""

import struct
import sys

SIGNATURE = b".signature.p7s"


def unsigned(data: bytes) -> bytes:
    end = data.rfind(b"PK\x05\x06")
    if end < 0:
        raise ValueError("no end of central directory record")
    (_, disk, cd_disk, disk_entries, entries, cd_size, cd_offset, comment_length) = struct.unpack_from(
        "<IHHHHIIH", data, end
    )
    if disk != 0 or cd_disk != 0 or disk_entries != entries or 0xFFFF in (entries,) or 0xFFFFFFFF in (cd_size, cd_offset):
        raise ValueError("multi-disk and ZIP64 archives are not NuGet packages")
    records = []
    position = cd_offset
    for _ in range(entries):
        if data[position : position + 4] != b"PK\x01\x02":
            raise ValueError("corrupt central directory")
        name_length, extra_length, comment = struct.unpack_from("<HHH", data, position + 28)
        local_offset = struct.unpack_from("<I", data, position + 42)[0]
        name = data[position + 46 : position + 46 + name_length]
        length = 46 + name_length + extra_length + comment
        records.append((name, position, length, local_offset))
        position += length
    signatures = [record for record in records if record[0] == SIGNATURE]
    if not signatures:
        return data
    name, record_position, record_length, local_offset = signatures[0]
    if records[-1][0] != SIGNATURE or max(record[3] for record in records) != local_offset:
        raise ValueError("the signature is not the last entry")
    directory = b"".join(
        data[position : position + length] for (entry, position, length, _) in records if entry != SIGNATURE
    )
    end_record = struct.pack(
        "<IHHHHIIH", 0x06054B50, 0, 0, entries - 1, entries - 1, len(directory), local_offset, comment_length
    )
    return data[:local_offset] + directory + end_record + data[end + 22 : end + 22 + comment_length]


if __name__ == "__main__":
    with open(sys.argv[1], "rb") as source:
        result = unsigned(source.read())
    with open(sys.argv[2], "wb") as target:
        target.write(result)

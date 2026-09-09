"""Exact delivered-PNG samples, with no color management or alpha conversion."""

import hashlib
import struct
import zlib
from pathlib import Path


def icon_rgba_bytes(path):
    # Only the delivered 512x512, non-interlaced RGBA8 PNG format is supported.
    png = Path(path).read_bytes()
    assert png[:8] == b"\x89PNG\r\n\x1a\n", "Not a PNG"
    offset, compressed, header, ended = 8, bytearray(), None, False
    while offset < len(png):
        assert offset + 12 <= len(png), "Truncated chunk"
        size, kind = struct.unpack_from(">I4s", png, offset)
        end = offset + 12 + size
        assert end <= len(png), "Truncated chunk payload"
        chunk = png[offset + 8:end - 4]
        assert zlib.crc32(kind + chunk) == struct.unpack_from(">I", png, end - 4)[0], "PNG CRC mismatch"
        if kind == b"IHDR":
            assert offset == 8 and size == 13 and header is None
            header = struct.unpack(">IIBBBBB", chunk)
            assert header == (512, 512, 8, 6, 0, 0, 0), "Expected 512x512 RGBA8 without interlacing"
        elif kind == b"IDAT":
            assert header is not None
            compressed.extend(chunk)
        elif kind == b"IEND":
            assert size == 0 and end == len(png)
            ended = True
            break
        else:
            assert kind[0] & 32 or kind == b"PLTE", "Unsupported critical PNG chunk"
        offset = end
    assert ended and header is not None and compressed
    width, height = header[:2]
    stride = width * 4
    expected = (stride + 1) * height
    decoder = zlib.decompressobj()
    filtered = decoder.decompress(compressed, expected + 1)
    assert len(filtered) == expected and decoder.eof and not decoder.unused_data, "Invalid PNG scanline data"
    previous, rows = bytearray(stride), []
    for y in range(height):
        start = y * (stride + 1)
        mode = filtered[start]
        assert 0 <= mode <= 4, "Invalid PNG filter"
        row = bytearray(filtered[start + 1:start + 1 + stride])
        for i in range(stride):
            left = row[i - 4] if i >= 4 else 0
            above = previous[i]
            corner = previous[i - 4] if i >= 4 else 0
            predictor = left + above - corner
            distances = (abs(predictor - left), abs(predictor - above), abs(predictor - corner))
            paeth = (left, above, corner)[distances.index(min(distances))]
            row[i] = (row[i] + (0, left, above, (left + above) // 2, paeth)[mode]) & 255
        rows.append(row)
        previous = row
    # PNG samples are straight alpha. Flip rows only; preserve every channel byte.
    return struct.pack("<II", width, height) + b"".join(reversed(rows))


if __name__ == "__main__":
    path = Path(__file__).with_name("computer-icon.rgba")
    pixels = icon_rgba_bytes(path.with_suffix(".png"))
    path.write_bytes(pixels)
    print(f"{path}: {len(pixels)} bytes; SHA256 {hashlib.sha256(pixels).hexdigest()}")

#!/usr/bin/env python3
"""
Converts a 24-bit uncompressed BMP into OpenTone's run-length encoded RGB565 image (.rle), for hooks:

    python hooks/tools/bmp2rle.py assets/hello-red.bmp assets/hello-red.rle

Then in a hook:  OT_INCBIN(img, "../assets/hello-red.rle");  ot_lcd_rle(img, img_end - img, 0, 0);

Format (little-endian): "OTR1", u16 width, u16 height, then runs of (u8 count 1-255, u16 RGB565 colour)
covering width x height pixels left to right, top to bottom. Runs may continue onto the next row.
"""
import struct, sys


def read_bmp(path):
    d = open(path, "rb").read()
    if d[:2] != b"BM":
        sys.exit(f"{path}: not a BMP")
    offset, = struct.unpack_from("<I", d, 10)
    w, h, _, bpp, compression = struct.unpack_from("<iiHHI", d, 18)
    if bpp != 24 or compression != 0 or w <= 0 or h == 0:
        sys.exit(f"{path}: need an uncompressed 24-bit BMP, got {bpp}-bit (compression {compression})")
    stride = (w * 3 + 3) & ~3                   # rows are padded to 4 bytes
    pixels = []
    for y in range(abs(h)):
        row = abs(h) - 1 - y if h > 0 else y    # positive height: stored bottom-up
        base = offset + row * stride
        for x in range(w):
            b, g, r = d[base + 3 * x: base + 3 * x + 3]
            pixels.append(((r & 0xf8) << 8) | ((g & 0xfc) << 3) | (b >> 3))
    return w, abs(h), pixels


def encode(w, h, pixels):
    out = bytearray(b"OTR1" + struct.pack("<HH", w, h))
    i = 0
    while i < len(pixels):
        n = 1
        while n < 255 and i + n < len(pixels) and pixels[i + n] == pixels[i]:
            n += 1
        out += struct.pack("<BH", n, pixels[i])
        i += n
    return bytes(out)


def decode(data):
    """Reference decoder, used to check the output."""
    assert data[:4] == b"OTR1"
    w, h = struct.unpack_from("<HH", data, 4)
    pixels = []
    for o in range(8, len(data), 3):
        n, c = struct.unpack_from("<BH", data, o)
        pixels += [c] * n
    return w, h, pixels


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__.strip().splitlines()[2].strip())
    w, h, px = read_bmp(sys.argv[1])
    rle = encode(w, h, px)
    if decode(rle) != (w, h, px):
        sys.exit("round trip failed")
    open(sys.argv[2], "wb").write(rle)
    print(f"{sys.argv[2]}: {w}x{h}, {len(rle)} bytes ({len(px) * 2} as raw RGB565)")

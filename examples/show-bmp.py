#!/usr/bin/env python3
"""
Shows a 128x160 24-bit BMP on the amp's screen using only "opentone call" and "opentone set mem".
Needs a firmware with the debug hook (opentone patch ... --hook 12=hooks/debug.c).

    python examples/show-bmp.py assets/hello-red.bmp [-d mustang-lt25]
    python examples/show-bmp.py --free            # release the staging buffer when done

Each opentone command connects first, and connecting makes the firmware redraw the screen. So the
image is uploaded into a heap buffer (which redraws don't touch) over several commands, then copied
into the frame buffer with one memmove call as the last command. The image stays until the next knob,
button or connection.

Freeing the buffer would take another command, whose redraw would wipe the image, so the buffer is
kept and reused by the next run. Its address and the amp's tick at allocation are saved in the temp
folder. If the tick has gone backwards since, the amp restarted (clearing the heap) and a new buffer
is allocated. --free releases it.
"""
import argparse, os, re, struct, subprocess, sys, tempfile

FRAMEBUFFER = 0x240000e0    # LTDC layer 1 address (L1CFBAR), 128 x 160 RGB565
WIDTH, HEIGHT = 128, 160
MALLOC = 0x08173e44         # malloc (returns 0 when out of memory, unlike operator new)
FREE = 0x08145e08           # operator delete, frees through the same allocator
MEMMOVE = 0x081744f8
GET_TICK = 0x08028c24       # HAL_GetTick: ms since boot, used to notice a restart
CHUNK = 10240               # bytes per "set mem": 20 KB of hex keeps each command line well under 32 K

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STATE = os.path.join(tempfile.gettempdir(), "opentone-show-bmp.txt")     # "<buffer> <tick at malloc>"


def to_rgb565(path):
    d = open(path, "rb").read()
    if d[:2] != b"BM":
        sys.exit(f"{path}: not a BMP")
    offset, = struct.unpack_from("<I", d, 10)
    w, h, _, bpp, compression = struct.unpack_from("<iiHHI", d, 18)
    if (w, abs(h), bpp, compression) != (WIDTH, HEIGHT, 24, 0):
        sys.exit(f"{path}: need an uncompressed 24-bit {WIDTH}x{HEIGHT} BMP, got {w}x{h} {bpp}-bit")
    stride = (w * 3 + 3) & ~3                   # BMP rows are padded to 4 bytes
    out = bytearray()
    for y in range(HEIGHT):
        row = y if h < 0 else HEIGHT - 1 - y    # positive height: stored bottom-up
        for x in range(WIDTH):
            b, g, r = d[offset + row * stride + x * 3: offset + row * stride + x * 3 + 3]
            out += struct.pack("<H", ((r & 0xf8) << 8) | ((g & 0xfc) << 3) | (b >> 3))
    return bytes(out)


def opentone(*args, device):
    exe = os.path.join(ROOT, "bin", "Debug", "net10.0", "OpenTone.exe" if os.name == "nt" else "OpenTone")
    cmd = [exe] if os.path.exists(exe) else ["dotnet", "run", "--project", ROOT, "--no-build", "--"]
    # Table borders come out in the console code page when redirected; only the digits matter here.
    p = subprocess.run(cmd + list(args) + ["-d", device], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0 or "Error" in p.stdout:
        sys.exit(f"opentone {' '.join(args[:2])} failed:\n{p.stdout}{p.stderr}")
    return p.stdout


def call(*args, device):
    out = opentone("call", *[hex(a) for a in args], device=device)
    m = re.search(r"r0 = 0x([0-9a-f]+)", out)
    if not m:
        sys.exit(f"unexpected call output:\n{out}")
    return int(m.group(1), 16)


def saved_buffer(device):
    """The buffer from an earlier run, if the amp hasn't restarted since (its tick only grows)."""
    try:
        buf, tick = (int(v, 16) for v in open(STATE).read().split())
    except (OSError, ValueError):
        return None
    return buf if call(GET_TICK, device=device) >= tick else None


def main():
    ap = argparse.ArgumentParser(description="Show a 128x160 24-bit BMP on the amp's screen.")
    ap.add_argument("bmp", nargs="?")
    ap.add_argument("-d", "--device", default="mustang-lt25")
    ap.add_argument("--free", action="store_true", help="release the staging buffer")
    a = ap.parse_args()

    buf = saved_buffer(a.device)
    if a.free:
        if buf:
            call(FREE, buf, device=a.device)
            print(f"freed 0x{buf:08x}")
        if os.path.exists(STATE):
            os.remove(STATE)
        return
    if not a.bmp:
        ap.error("give a BMP, or --free")

    pixels = to_rgb565(a.bmp)
    if buf is None:
        tick = call(GET_TICK, device=a.device)
        buf = call(MALLOC, len(pixels), device=a.device)
        if buf == 0:
            sys.exit("malloc failed: not enough free heap on the amp")
        with open(STATE, "w") as f:
            f.write(f"{buf:x} {tick:x}")
    print(f"staging {len(pixels)} bytes at 0x{buf:08x}")
    for off in range(0, len(pixels), CHUNK):
        opentone("set", "mem", hex(buf + off), pixels[off:off + CHUNK].hex(), device=a.device)
        print(f"  uploaded {min(off + CHUNK, len(pixels))}/{len(pixels)}")
    call(MEMMOVE, FRAMEBUFFER, buf, len(pixels), device=a.device)    # last command: nothing redraws after it
    print("shown (stays until a knob, a button or the next connection)")


if __name__ == "__main__":
    main()

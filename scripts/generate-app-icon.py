#!/usr/bin/env python3
# =====================================================
#  generate-app-icon.py
#
#  Generates the .icns file used as the app icon. Renders
#  the design (black squircle + 4 white petals + center
#  dot) at every size macOS expects, packs them into a
#  proper .icns by hand, and writes app.icns.
#
#  macOS .icns format: 4-byte type ('icns') + 4-byte file size
#  + N× entries, where each entry is 4-byte type + 4-byte
#  size + PNG data. Standard Apple type codes are
#  documented at https://en.wikipedia.org/wiki/Apple_Icon_Image_format
#
#  Run from project root:
#    python3 scripts/generate-app-icon.py
#  → produces scripts/app.icns
# =====================================================
import os
import struct
import sys
from PIL import Image

# ---------- design constants ----------
BG_COLOR   = (28, 28, 30, 255)     # #1c1c1e — iOS-style dark gray
FG_COLOR   = (255, 255, 255, 255)  # pure white
BASE_SIZE  = 1024                  # master canvas; down-scaled for the .iconset
CORNER_R   = 0.223                 # iOS squircle corner radius (22.3% of width)

PETAL_RADIUS = 0.115               # radius of each petal as fraction of BASE_SIZE
PETAL_OFFSET = 0.122               # distance of each petal center from origin
CENTER_RADIUS = 0.052              # radius of the center dot


def draw_icon(size: int) -> Image.Image:
    """Render the icon at the requested square size (RGBA).

    Design: a dark squircle with 4 white "petals" at the cardinal
    directions (N / S / E / W). Each petal is a half-circle (180°
    pieslice) with its flat side facing the center and its arc
    facing outward. A small white dot sits at the exact center
    where the four flat edges meet.
    """
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    from PIL import ImageDraw
    draw = ImageDraw.Draw(img)
    s = lambda f: int(round(f * size))  # fraction -> px

    # 1. Dark squircle background
    corner = s(CORNER_R)
    draw.rounded_rectangle((0, 0, size - 1, size - 1),
                           radius=corner, fill=BG_COLOR)

    cx = cy = (size - 1) / 2
    petal_r     = s(0.135)   # half-circle radius
    petal_dist  = s(0.135)   # distance from canvas-center to petal-center
                           # (= petal_r so the flat edge of each petal
                           #  is exactly on the canvas-center)

    # PIL pieslice: 0° = 3 o'clock, 90° = 6, 180° = 9, 270° = 12 (clockwise)
    # Each petal is 180° wide: the flat side is the chord that
    # passes through the petal's center, the arc is the outer half.
    # North: petal at (cx, cy - petal_dist), arc faces UP (12 o'clock)
    draw.pieslice(
        (cx - petal_r, cy - petal_dist - petal_r,
         cx + petal_r, cy - petal_dist),
        start=180, end=360, fill=FG_COLOR,
    )
    # South: petal at (cx, cy + petal_dist), arc faces DOWN (6 o'clock)
    draw.pieslice(
        (cx - petal_r, cy + petal_dist,
         cx + petal_r, cy + petal_dist + petal_r),
        start=0, end=180, fill=FG_COLOR,
    )
    # East: petal at (cx + petal_dist, cy), arc faces RIGHT (3 o'clock)
    draw.pieslice(
        (cx + petal_dist - petal_r, cy - petal_r,
         cx + petal_dist + petal_r, cy + petal_r),
        start=270, end=90, fill=FG_COLOR,
    )
    # West: petal at (cx - petal_dist, cy), arc faces LEFT (9 o'clock)
    draw.pieslice(
        (cx - petal_dist - petal_r, cy - petal_r,
         cx - petal_dist + petal_r, cy + petal_r),
        start=90, end=270, fill=FG_COLOR,
    )

    # 3. Center white dot — small, sits where all 4 petals meet.
    dr = s(CENTER_RADIUS)
    draw.ellipse((cx - dr, cy - dr, cx + dr, cy + dr), fill=FG_COLOR)

    return img


def png_bytes(size: int) -> bytes:
    """Render and return raw PNG bytes for the icon at the given size."""
    import io
    img = draw_icon(size)
    buf = io.BytesIO()
    img.save(buf, 'PNG', optimize=True)
    return buf.getvalue()


# Each entry: (4-byte type code, size in pixels)
# Apple type codes from the .icns spec; we cover all the sizes Finder/Dock
# /Spotlight/etc. may pick from.
ICNS_ENTRIES = [
    (b'icp4',  16),    # 16x16
    (b'icp5',  32),    # 16x16@2x = 32
    (b'icp6',  64),    # 32x32@2x = 64
    (b'ic07',  128),   # 128x128
    (b'ic08',  256),   # 128x128@2x = 256
    (b'ic09',  512),   # 256x256@2x = 512
    (b'ic10',  1024),  # 512x512@2x = 1024 (also the 256@2x for older format)
    (b'ic11',  32),    # 16x16@2x (iOS, alt name)
    (b'ic12',  64),    # 32x32@2x (iOS, alt name)
    (b'ic13',  256),   # 16x16@2x for @3x density
    (b'ic14',  512),   # 32x32@2x for @3x density
]


def build_icns() -> bytes:
    """Build a valid .icns file by hand from the icon rendered at each size."""
    out = bytearray()
    out += b'icns'                # magic
    out += b'\x00\x00\x00\x00'    # placeholder for total file size
    body = bytearray()
    for type_code, size in ICNS_ENTRIES:
        png = png_bytes(size)
        # Each entry: 4-byte type + 4-byte entry size (big-endian, includes the 8-byte header)
        entry = type_code + struct.pack('>I', len(png) + 8) + png
        body += entry
    total = 8 + len(body)
    out[4:8] = struct.pack('>I', total)
    return bytes(out + body)


def main() -> int:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    out_icns = os.path.join(script_dir, 'app.icns')

    # Sanity-check: render the master and write a preview PNG so the
    # user can eyeball the design before installing.
    preview_png = os.path.join(script_dir, 'app-icon-preview.png')
    draw_icon(512).save(preview_png, 'PNG', optimize=True)
    print(f'preview: {preview_png}  (open it to eyeball the design)')

    icns = build_icns()
    with open(out_icns, 'wb') as f:
        f.write(icns)
    print(f'wrote:   {out_icns}  ({len(icns) // 1024} KB, {len(ICNS_ENTRIES)} sizes)')
    return 0


if __name__ == '__main__':
    sys.exit(main())

#!/usr/bin/env python3
"""Generate clock and circular-arrow launcher bitmaps."""
import argparse
import math
import struct
from pathlib import Path
WHITE, BLACK, BLUE, BLUE_LIGHT, RED, YELLOW, GREY, DARK_GREY, PALM_LIGHT_GREY, PALM_DARK_GREY = range(10)

def draw(size, mode):
    image = [[WHITE] * size for _ in range(size)]
    def pixel(x, y, colour):
        if 0 <= x < size and 0 <= y < size:
            image[y][x] = colour
    def line(x1, y1, x2, y2, colour):
        steps = max(abs(x2-x1), abs(y2-y1), 1)
        for i in range(steps+1):
            pixel(round(x1+(x2-x1)*i/steps), round(y1+(y2-y1)*i/steps), colour)
    c = (size-1)/2
    radius = size*0.29
    for degree in range(360):
        a = math.radians(degree)
        pixel(round(c+radius*math.cos(a)), round(c+radius*math.sin(a)), BLACK)
    line(round(c), round(c), round(c), round(c-radius*0.65), BLACK)
    line(round(c), round(c), round(c+radius*0.55), round(c+radius*0.3), BLACK)
    arrow = BLACK if mode == 'bw' else (PALM_DARK_GREY if mode == 'gray' else BLUE)
    for degree in range(205, 335):
        a = math.radians(degree)
        pixel(round(c+(radius+2)*math.cos(a)), round(c+(radius+2)*math.sin(a)), arrow)
    x = round(c+(radius+2)*math.cos(math.radians(334)))
    y = round(c+(radius+2)*math.sin(math.radians(334)))
    line(x, y, x-3, y-1, arrow)
    line(x, y, x-1, y-3, arrow)
    if mode != 'bw':
        pixel(round(c-radius*0.5), round(c+radius*0.5), PALM_LIGHT_GREY if mode == 'gray' else GREY)
    return image

def write_bmp(path: Path, image: list[list[int]], bits: int) -> None:
    if not image or not image[0]:
        raise ValueError("image must not be empty")
    width = len(image[0])
    height = len(image)
    if any(len(row) != width for row in image):
        raise ValueError("image rows must have equal width")

    if bits == 1:
        row_bytes = ((width + 31) // 32) * 4
        palette = [(255, 255, 255), (0, 0, 0)]
        rows = bytearray()
        for row in reversed(image):
            packed = bytearray(row_bytes)
            for x, colour in enumerate(row):
                if colour != WHITE:
                    packed[x // 8] |= 1 << (7 - x % 8)
            rows.extend(packed)
    elif bits == 8:
        row_bytes = (width + 3) & ~3
        base = [
            (255, 255, 255), (0, 0, 0), (0, 94, 184),
            (126, 204, 238), (228, 35, 43), (255, 220, 0),
            (224, 224, 214), (80, 80, 80),
        ]
        if any(colour >= PALM_LIGHT_GREY for row in image for colour in row):
            base.extend(((170, 170, 170), (85, 85, 85)))
        palette = base + [(0, 0, 0)] * (256 - len(base))
        rows = bytearray()
        for row in reversed(image):
            rows.extend(row)
            rows.extend(b"\x00" * (row_bytes - width))
    else:
        raise ValueError("bits must be 1 or 8")

    palette_bytes = b"".join(
        struct.pack("<BBBB", blue, green, red, 0)
        for red, green, blue in palette
    )
    data_offset = 14 + 40 + len(palette_bytes)
    header = struct.pack(
        "<2sIHHI", b"BM", data_offset + len(rows), 0, 0, data_offset
    )
    info = struct.pack(
        "<IiiHHIIiiII", 40, width, height, 1, bits, 0, len(rows),
        2835, 2835, len(palette), len(palette)
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(header + info + palette_bytes + rows)



def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output-dir', type=Path, default=Path('res/icons'))
    args = parser.parse_args()
    for label, size in (('large', 22), ('small', 15)):
        for mode in ('bw', 'gray', 'color'):
            image = draw(size, mode)
            if label == 'small':
                image = [image[round(y * (size - 1) / 8)] for y in range(9)]
            write_bmp(args.output_dir / f'autosync_{label}_{mode}.bmp', image, 1 if mode == 'bw' else 8)

if __name__ == '__main__':
    main()

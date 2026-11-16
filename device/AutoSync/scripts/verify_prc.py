#!/usr/bin/env python3
"""Validate PiloAutoSync identity and low-depth bitmap resources."""

from __future__ import annotations

import argparse
from pathlib import Path
import struct


def parse_resources(path: Path) -> tuple[bytes, bytes, bytes, dict[tuple[bytes, int], bytes]]:
    data = path.read_bytes()
    if len(data) < 78:
        raise ValueError("truncated Palm resource database")
    name = data[:32].split(b"\0", 1)[0]
    database_type = data[60:64]
    creator = data[64:68]
    count = struct.unpack_from(">H", data, 76)[0]
    table_end = 78 + count * 10
    if table_end > len(data):
        raise ValueError("truncated resource table")
    entries = [
        struct.unpack_from(">4sHI", data, 78 + index * 10)
        for index in range(count)
    ]
    resources: dict[tuple[bytes, int], bytes] = {}
    for index, (resource_type, resource_id, offset) in enumerate(entries):
        end = entries[index + 1][2] if index + 1 < count else len(data)
        if offset < table_end or end < offset or end > len(data):
            raise ValueError("invalid resource offsets")
        key = (resource_type, resource_id)
        if key in resources:
            raise ValueError(f"duplicate resource {key!r}")
        resources[key] = data[offset:end]
    return name, database_type, creator, resources


def bitmap_family_members(payload: bytes) -> list[tuple[int, int, bytes]]:
    headers: list[tuple[int, int, int]] = []
    seen: set[int] = set()
    offset = 0
    while True:
        if offset in seen or offset + 16 > len(payload):
            raise ValueError("invalid bitmap-family chain")
        seen.add(offset)
        depth = payload[offset + 8]
        flags = struct.unpack_from(">H", payload, offset + 6)[0]
        headers.append((offset, depth, flags))
        next_depth = struct.unpack_from(">H", payload, offset + 10)[0]
        if next_depth == 0:
            break
        offset += next_depth * 4
        if offset >= len(payload):
            raise ValueError("bitmap-family member is outside the resource")
    return [
        (
            depth,
            flags,
            payload[start:(headers[index + 1][0]
                           if index + 1 < len(headers) else len(payload))],
        )
        for index, (start, depth, flags) in enumerate(headers)
    ]


def bitmap_pixels(member: bytes) -> tuple[int, int, int, set[int]]:
    width, height, row_bytes, flags = struct.unpack_from(">HHHH", member, 0)
    depth = member[8]
    if flags & 0x8000:
        raise ValueError("gray tile must remain uncompressed")
    raw = member[16:16 + row_bytes * height]
    if len(raw) != row_bytes * height:
        raise ValueError("truncated bitmap pixels")
    pixels: set[int] = set()
    for y in range(height):
        row = raw[y * row_bytes:(y + 1) * row_bytes]
        for x in range(width):
            if depth == 1:
                value = (row[x // 8] >> (7 - x % 8)) & 1
            elif depth == 2:
                value = (row[x // 4] >> (6 - 2 * (x % 4))) & 3
            elif depth == 4:
                value = (row[x // 2] >> (4 - 4 * (x % 2))) & 15
            elif depth == 8:
                value = row[x]
            else:
                raise ValueError(f"unsupported bitmap depth {depth}")
            pixels.add(value)
    return width, height, depth, pixels


def validate(path: Path) -> None:
    name, database_type, creator, resources = parse_resources(path)
    if (name, database_type, creator) != (
        b"PiloAutoSync", b"appl", b"PSas"
    ):
        raise ValueError(
            f"unexpected identity: {name!r}/{database_type!r}/{creator!r}"
        )
    launcher = bitmap_family_members(resources[(b"tAIB", 1000)])
    if [depth for depth, _flags, _member in launcher] != [1, 2, 4, 8]:
        raise ValueError("launcher icon lacks its 1/2/4/8bpp family")
    launcher_expected = {
        1: {0, 1},
        2: {0, 1, 2, 3},
        4: {0, 5, 10, 15},
    }
    for depth, _flags, member in launcher:
        width, height, stored_depth, pixels = bitmap_pixels(member)
        if (width, height, stored_depth) != (22, 22, depth):
            raise ValueError(
                f"invalid launcher {depth}bpp dimensions: "
                f"{width}x{height}, stored {stored_depth}"
            )
        if depth in launcher_expected and pixels != launcher_expected[depth]:
            raise ValueError(
                f"invalid launcher {depth}bpp gray levels: {pixels}"
            )



def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("prc", type=Path)
    arguments = parser.parse_args()
    validate(arguments.prc)
    print(f"PRC validation passed: {arguments.prc}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

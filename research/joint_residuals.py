"""Compare grouping and dictionary options on adaptive core streams."""

import argparse
import json
import struct
from pathlib import Path

import numpy as np
import zstandard as zstd


def read_samples(path, requested):
    with path.open("rb") as source:
        header = source.read(48)
        if header[:10] != b"JEMA\r\n\x1a\n\x01\x00":
            raise ValueError("Expected a revision 1 JEMA file")
        frames, total, index, tracks = struct.unpack_from("<IQQH", header, 20)
        if not (0 < frames and 1 <= tracks <= 256):
            raise ValueError("Invalid block geometry")
        layout = struct.unpack(f"<{tracks}H", source.read(tracks * 2))
        channels = sum(layout)
        raw_size = frames * channels * 4
        if 0 in layout or channels > 256 or raw_size > 4 * 1024 * 1024 or frames % 8:
            raise ValueError("Expected complete eight-frame bit-plane groups and a valid track layout")
        full_blocks = total // frames
        if full_blocks < 2:
            raise ValueError("Expected at least two complete blocks")

        samples = []
        z = zstd.ZstdDecompressor()
        for b in np.unique(np.linspace(0, full_blocks - 1, min(requested, full_blocks), dtype=int)):
            source.seek(index + 24 * b)
            offset, size, mode = struct.unpack("<QI4xB7x", source.read(24))
            if mode != 17:
                continue
            source.seek(offset)
            core_size = struct.unpack("<I", source.read(4))[0]
            source.read(channels)
            if not 0 < core_size < min(raw_size, size - 4 - channels):
                raise ValueError("Invalid core stream")
            compressed = source.read(core_size)
            table_limit = 1 + channels * (2 + 65 * ((frames + 2047) // 2048))
            declared = zstd.frame_content_size(compressed)
            if not raw_size < declared <= raw_size + table_limit:
                raise ValueError("Invalid predictor table size")
            packet = z.decompress(compressed)
            samples.append((core_size, packet))
    return layout, frames, samples


def residual_sizes(packet, table, planes, layout, frames, z):
    sizes = {"joint": len(z.compress(packet))}
    for tile in (64, 128, 256, 512, 1024, 2048):
        if frames % tile != 0:
            continue
        groups = planes.reshape(32, sum(layout), frames // tile, tile // 8)
        sizes[f"plane-time-track-{tile}"] = len(z.compress(table + groups.transpose(0, 2, 1, 3).tobytes()))
        sizes[f"time-plane-track-{tile}"] = len(z.compress(table + groups.transpose(2, 0, 1, 3).tobytes()))

    sizes["instant-channel"] = len(z.compress(table + planes.transpose(0, 2, 1).tobytes()))
    for high in (4, 8, 12, 16, 20):
        sizes[f"two-frames-{high}"] = (4 + len(z.compress(planes[:high]))
                                      + len(z.compress(table + planes[high:].tobytes())))
    start = 0
    separate = len(z.compress(table))
    for track in layout:
        separate += 4 + len(z.compress(planes[:, start:start + track].tobytes()))
        start += track
    sizes["independent-residuals"] = separate
    return sizes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--blocks", type=int, default=48)
    parser.add_argument("--level", type=int, default=5)
    parser.add_argument("--dictionary", action="store_true")
    args = parser.parse_args()
    if args.blocks < 2:
        parser.error("Use at least two requested blocks")

    layout, frames, samples = read_samples(args.archive, args.blocks)
    if not samples:
        raise ValueError("No sampled block uses adaptive bit-plane coding")
    dictionary = None
    if args.dictionary:
        train = [sample[1] for i, sample in enumerate(samples) if i % 3 == 0]
        if len(train) < 8:
            raise ValueError("The dictionary probe needs at least eight adaptive training blocks")
        dictionary = zstd.ZstdCompressor(level=args.level, dict_data=zstd.train_dictionary(8192, train))
        samples = [sample for i, sample in enumerate(samples) if i % 3 != 0]

    raw_block = frames * sum(layout) * 4
    z = zstd.ZstdCompressor(level=args.level)
    total = {}
    best = 0
    for source_size, packet in samples:
        table = packet[:-raw_block]
        data = np.frombuffer(packet[-raw_block:], dtype="u1").reshape(32, sum(layout), frames // 8)
        sizes = residual_sizes(packet, table, data, layout, frames, z)
        baseline = sizes["joint"]
        best += max(0, baseline - min(size for key, size in sizes.items() if key != "independent-residuals") - 1)
        if dictionary:
            sizes["trained-8-kib-dictionary"] = len(dictionary.compress(packet))
        for name, size in sizes.items():
            total[name] = total.get(name, 0) + size - source_size

    raw_total = len(samples) * raw_block
    print(json.dumps({
        "validation_blocks": len(samples), "validation_pcm_bytes": raw_total,
        "stored_core_byte_delta": total,
        "sampled_layout_oracle_saved_bytes": best,
        "dictionary_bytes_per_file": 8192 if dictionary else 0,
    }, indent=2))


if __name__ == "__main__":
    main()

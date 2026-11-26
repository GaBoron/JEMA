"""Measure bit-exact baselines on little-endian, frame-interleaved Float32 PCM."""

import argparse
import json
import subprocess
import time
from pathlib import Path

import numpy as np
import zstandard as zstd


def byte_planes(words):
    return words.view("u1").reshape(words.shape + (4,)).transpose(2, 0, 1).copy()


def bit_planes(words):
    full = words.shape[1] // 8 * 8
    packed = np.packbits(
        np.unpackbits(byte_planes(np.ascontiguousarray(words[:, :full]))[..., None],
                      axis=3, bitorder="little").transpose(0, 3, 1, 2),
        axis=3, bitorder="little",
    )
    return packed.tobytes() + words[:, full:].tobytes()


def inverse_bytes(data, channels):
    return np.frombuffer(data, dtype="u1").reshape(4, channels, -1).transpose(1, 2, 0).tobytes()


def inverse_bits(data, channels, frames):
    groups = frames // 8
    size = 32 * channels * groups
    bits = np.unpackbits(np.frombuffer(data[:size], dtype="u1").reshape(4, 8, channels, groups),
                         axis=3, bitorder="little")
    complete = np.packbits(bits.transpose(2, 3, 0, 1), axis=3, bitorder="little").reshape(channels, groups * 8, 4)
    tail = np.frombuffer(data[size:], dtype="u1").reshape(channels, frames % 8, 4)
    return np.concatenate((complete, tail), axis=1).tobytes()


def block_baselines(pcm, block_frames, tracks, level):
    from pcodec import ChunkConfig, standalone

    compressor = zstd.ZstdCompressor(level=level)
    decoder = zstd.ZstdDecompressor()
    config = ChunkConfig()
    names = ("zstd", "byte-zstd", "bit-zstd", "pcodec")
    results = {name: {"bytes": 48 + 2 * len(tracks), "encode_s": 0, "decode_s": 0} for name in names}
    for start in range(0, len(pcm), block_frames):
        block = pcm[start:start + block_frames]
        lanes = block.T.copy()
        for name in names:
            tick = time.perf_counter()
            if name == "pcodec":
                encoded = [standalone.simple_compress(lane.view("<f4"), config) for lane in lanes]
                size = sum(4 + len(part) for part in encoded)
            else:
                sample = (block.tobytes() if name == "zstd" else
                          byte_planes(lanes).tobytes() if name == "byte-zstd" else bit_planes(lanes))
                encoded = compressor.compress(sample)
                size = len(encoded)
            middle = time.perf_counter()

            if name == "pcodec":
                restored = b"".join(standalone.simple_decompress(part).view("<u4").tobytes() for part in encoded)
            else:
                restored = decoder.decompress(encoded)
                if name == "byte-zstd":
                    restored = inverse_bytes(restored, len(lanes))
                elif name == "bit-zstd":
                    restored = inverse_bits(restored, len(lanes), len(block))
            end = time.perf_counter()

            expected = block.tobytes() if name == "zstd" else lanes.tobytes()
            if restored != expected:
                raise ValueError(f"{name} round trip mismatch at frame {start}")
            results[name]["bytes"] += 24 + min(size, block.nbytes)
            results[name]["encode_s"] += middle - tick
            results[name]["decode_s"] += end - middle
    return results


def run_wavpack(pcm_path, args, channels):
    folder = args.wavpack_dir
    output = args.output / "whole.wv"
    original = np.memmap(pcm_path, mode="r", dtype="u1")
    started = time.perf_counter()
    preset = {"normal": [], "very-high": ["-hh"]}[args.wavpack_preset]
    with pcm_path.open("rb") as input_file:
        subprocess.run([str(folder / "wavpack.exe"), "-q", *preset, f"--blocksize={args.block}",
                        f"--raw-pcm={args.rate},32f,{channels},le", "-", "-o", str(output)],
                       stdin=input_file, check=True)
    encoded = time.perf_counter() - started

    restored = args.output / "whole.raw"
    started = time.perf_counter()
    subprocess.run([str(folder / "wvunpack.exe"), "-q", "-r", str(output), "-o", str(restored)], check=True)
    decoded = time.perf_counter() - started
    actual = np.memmap(restored, mode="r", dtype="u1")
    if len(original) != len(actual):
        raise ValueError("WavPack decoded an unexpected number of bytes")
    for start in range(0, len(actual), 4 * 1024 * 1024):
        if not np.array_equal(actual[start:start + 4 * 1024 * 1024], original[start:start + 4 * 1024 * 1024]):
            raise ValueError(f"WavPack bit mismatch near byte {start}")
    return {"bytes": output.stat().st_size, "encode_s": encoded, "decode_s": decoded}


def independent_jema(pcm, args):
    results = {"bytes": 0, "encode_s": 0, "verify_s": 0}
    first = 0
    for number, channels in enumerate(args.tracks):
        source = args.output / f"track-{number}.f32le"
        archive = args.output / f"track-{number}.jema"
        with source.open("xb") as output:
            for start in range(0, len(pcm), 65536):
                output.write(pcm[start:start + 65536, first:first + channels].tobytes())
        first += channels
        command = ["dotnet", str(args.tool)]
        started = time.perf_counter()
        subprocess.run(command + ["encode", str(source), str(archive), str(args.rate), str(channels),
                                  str(args.block), str(args.level)], check=True)
        results["encode_s"] += time.perf_counter() - started
        results["bytes"] += archive.stat().st_size
        started = time.perf_counter()
        subprocess.run(command + ["verify", str(archive), str(source)],
                       stdout=subprocess.DEVNULL, check=True)
        results["verify_s"] += time.perf_counter() - started
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pcm", type=Path)
    parser.add_argument("rate", type=int)
    parser.add_argument("tracks", type=lambda value: [int(x) for x in value.split(",")])
    parser.add_argument("output", type=Path)
    parser.add_argument("--block", type=int, default=8192)
    parser.add_argument("--level", type=int, default=5)
    parser.add_argument("--wavpack-dir", type=Path)
    parser.add_argument("--wavpack-preset", choices=("normal", "very-high"), default="very-high")
    parser.add_argument("--tool", type=Path, help="Run independent-track JEMA through the built command line tool")
    parser.add_argument("--skip-block-codecs", action="store_true")
    args = parser.parse_args()

    args.output.mkdir()
    channels = sum(args.tracks)
    pcm = np.memmap(args.pcm, dtype="<u4", mode="r").reshape(-1, channels)
    results = {"raw": {"bytes": pcm.nbytes}}
    if args.wavpack_dir:
        results["wavpack-" + args.wavpack_preset] = run_wavpack(args.pcm, args, channels)
    if not args.skip_block_codecs:
        results.update(block_baselines(pcm, args.block, args.tracks, args.level))
    if args.tool:
        results["jema-independent"] = independent_jema(pcm, args)
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()

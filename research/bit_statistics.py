"""Measure per-bit sample entropy and temporal/track sign transitions."""

import argparse
import json
import struct
from collections import Counter

import numpy as np


def binary_entropy(probability):
    p = np.clip(probability, 1e-15, 1 - 1e-15)
    return -(p * np.log2(p) + (1 - p) * np.log2(1 - p))


def pcm_statistics(path, tracks, frames):
    channels = sum(tracks)
    pcm = np.memmap(path, dtype="<u4", mode="r").reshape(-1, channels)
    if frames < 2 or len(pcm) < 2:
        raise ValueError("Sign-transition statistics require at least two frames")
    starts = np.unique(np.linspace(0, max(0, len(pcm) - frames), 24, dtype="i8"))
    blocks = np.stack([pcm[start:start + frames] for start in starts])
    marginals = [float(binary_entropy(np.mean((blocks >> bit) & 1))) for bit in range(32)]

    sign = blocks >> 31
    sign_flip = sign[:, 1:] ^ sign[:, :-1]
    within = sign_flip.copy()
    cross = sign_flip.copy()
    start = 0
    for i, count in enumerate(tracks):
        for j in range(count):
            if j > 0:
                within[:, :, start + j] ^= sign_flip[:, :, start]
            if i > 0 and j < tracks[i - 1]:
                cross[:, :, start + j] ^= sign_flip[:, :, start + j - tracks[i - 1]]
            elif j > 0:
                cross[:, :, start + j] ^= sign_flip[:, :, start]
        start += count

    def entropy(values):
        return float(np.mean(binary_entropy(np.mean(values, axis=(0, 1)))))

    return {
        "sampled_frames": int(np.prod(blocks.shape[:2])),
        "marginal_entropy_by_bit": marginals,
        "mantissa_low8_mean": float(np.mean(marginals[:8])),
        "residual_sign_entropy": {"temporal": entropy(sign_flip), "within_track": entropy(within),
                                  "cross_track": entropy(cross)},
    }


def mode_counts(path):
    with open(path, "rb") as source:
        magic, revision, block, frames, index = struct.unpack("<8sH10xIQQ", source.read(40))
        if magic != b"JEMA\r\n\x1a\n" or revision != 1 or block < 1:
            raise ValueError("Expected a revision 1 JEMA file")
        source.seek(index)
        counts = Counter()
        sizes = Counter()
        for _ in range((frames + block - 1) // block):
            size, mode = struct.unpack("<8xI4xB7x", source.read(24))
            counts[mode] += 1
            sizes[mode] += size
    return dict(counts), dict(sizes)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pcm")
    parser.add_argument("tracks", type=lambda value: [int(x) for x in value.split(",")])
    parser.add_argument("--frames", type=int, default=8192)
    parser.add_argument("--archive")
    args = parser.parse_args()
    results = pcm_statistics(args.pcm, args.tracks, args.frames)
    if args.archive:
        results["mode_counts"], results["mode_bytes"] = mode_counts(args.archive)
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()

"""Generate deterministic high-entropy, correlated, and repeated PCM controls."""

import argparse
from pathlib import Path

import numpy as np


def write(directory, name, data):
    with (directory / (name + ".f32le")).open("xb") as output:
        data.tofile(output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir()
    rng = np.random.default_rng(949014)
    block = 8192
    phases = np.arange(4 * block, dtype="f8") / 44100
    envelope = np.exp(-np.mod(phases, 0.125) * 28)
    loop = np.column_stack((np.sin(phases * 2 * np.pi * 211), np.cos(phases * 2 * np.pi * 487)))
    loop = (loop * envelope[:, None]).astype("<f4")
    repeated = np.tile(loop, (8, 1))
    independent = rng.normal(0, 0.125, len(repeated)).astype("<f4")

    write(args.output, "repeated", np.column_stack((repeated, repeated[:, 0] * np.float32(-0.163))))
    write(args.output, "one-repeated-track", np.column_stack((repeated, independent)))
    write(args.output, "random-words", rng.integers(0, 1 << 32, (len(repeated), 3), dtype="<u4"))


if __name__ == "__main__":
    main()

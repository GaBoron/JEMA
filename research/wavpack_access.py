"""Measure WavPack's public Float32 seek and decode interface with a warm file cache."""

import argparse
import ctypes as ct
import json
import os
import random
import time
from contextlib import contextmanager
from pathlib import Path

import numpy as np


def bind(library, name, result, *arguments):
    function = getattr(library, "Wavpack" + name)
    function.restype = result
    function.argtypes = arguments
    return function


class WavPack:
    def __init__(self, path):
        library = ct.CDLL(str(path.resolve()))
        self.open_file = bind(library, "OpenFileInput", ct.c_void_p, ct.c_char_p, ct.POINTER(ct.c_char),
                              ct.c_int, ct.c_int)
        self.close_file = bind(library, "CloseFile", ct.c_void_p, ct.c_void_p)
        self.channels = bind(library, "GetNumChannels", ct.c_int, ct.c_void_p)
        self.frames = bind(library, "GetNumSamples64", ct.c_int64, ct.c_void_p)
        self.rate = bind(library, "GetSampleRate", ct.c_uint32, ct.c_void_p)
        self.errors = bind(library, "GetNumErrors", ct.c_int, ct.c_void_p)
        self.seek = bind(library, "SeekSample64", ct.c_int, ct.c_void_p, ct.c_int64)
        self.decode = bind(library, "UnpackSamples", ct.c_uint32, ct.c_void_p,
                           ct.POINTER(ct.c_int32), ct.c_uint32)

    @contextmanager
    def open(self, path):
        error = ct.create_string_buffer(128)
        name = str(path.resolve()).encode("mbcs") if os.name == "nt" else os.fsencode(path.resolve())
        handle = self.open_file(name, error, 0, 0)
        if not handle:
            raise OSError(error.value.decode(errors="replace"))
        try:
            yield handle
            if self.errors(handle):
                raise ValueError("WavPack reported a block error")
        finally:
            self.close_file(handle)


def distribution(samples):
    ordered = np.sort(samples)
    parts = [int(np.ceil(len(samples) * p)) - 1 for p in (0.5, 0.95, 0.99, 1.0)]
    return dict(zip(("P50", "P95", "P99", "Max"), ordered[parts].tolist()))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("library", type=Path)
    parser.add_argument("archive", type=Path)
    parser.add_argument("pcm", type=Path)
    parser.add_argument("--requests", type=int, default=1000)
    args = parser.parse_args()
    if args.requests < 1:
        parser.error("Requests must be positive")
    codec = WavPack(args.library)
    with args.archive.open("rb") as source:
        while source.read(1024 * 1024):
            pass
    with codec.open(args.archive) as reader:
        rate, channels, frames = codec.rate(reader), codec.channels(reader), codec.frames(reader)
    pcm = np.memmap(args.pcm, mode="r", dtype="<u4").reshape(-1, channels)
    if len(pcm) != frames:
        raise ValueError("PCM length mismatch")
    rows = []
    for window in (1, 512, max(1, min(rate // 3, 40000)), min(rate * 2, 192000), rate):
        count = min(window, frames)
        reopen = window == rate
        rng = random.Random(17891)
        starts = ([0] * min(args.requests, 128) if reopen else
                  [rng.randrange(frames - count + 1) for _ in range(args.requests)])
        output = (ct.c_int32 * (count * channels))()
        view = np.ctypeslib.as_array(output).view("<u4").reshape(-1, channels)
        samples = []
        opens = []
        with codec.open(args.archive) as reader:
            codec.decode(reader, output, count)
            for start in starts:
                tick = time.perf_counter_ns()
                if reopen:
                    with codec.open(args.archive) as fresh:
                        opens.append((time.perf_counter_ns() - tick) / 1e6)
                        got = codec.decode(fresh, output, count)
                else:
                    if not codec.seek(reader, start):
                        raise ValueError(f"WavPack seek failed at {start}")
                    got = codec.decode(reader, output, count)
                samples.append((time.perf_counter_ns() - tick) / 1e6)
                if got != count or not np.array_equal(view, pcm[start:start + count]):
                    raise ValueError(f"WavPack range mismatch at {start}")
        rows.append({"mode": "open-and-first" if reopen else "random", "frames": count,
                     "requests": len(starts), "latency_ms": distribution(samples),
                     "open_ms": distribution(opens) if opens else None})
    print(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()

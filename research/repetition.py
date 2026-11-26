"""Screen exact and approximate long-range redundancy on individual channels."""

import argparse
import json

import numpy as np
import zstandard as zstd

from compare import bit_planes


def residual(words):
    magnitude = words & 0x7fffffff
    residuals = np.diff(magnitude, prepend=np.uint32(0)) & 0x7fffffff
    signs = np.diff((words >> 31).astype(np.int8), prepend=np.int8(0)) != 0
    zigzag = ((residuals << 1) ^ (np.uint32(0) - (residuals >> 30))) & 0x7fffffff
    return zigzag | (signs.astype("u4") << 31)


def estimate(words, channel, rate, frames, queries, level):
    lane = np.ascontiguousarray(words[:, channel])
    if not np.isfinite(lane.view("<f4")).all():
        raise ValueError("Similarity search requires finite samples")
    step = frames // 4
    count = (len(lane) - frames) // step + 1
    if count < 2:
        raise ValueError("Sample is too short for this window")
    candidates = np.arange(count, dtype=np.int64) * step
    samples = lane.view("<f4")
    windows = np.lib.stride_tricks.sliding_window_view(samples, frames)[::step]
    pooled = windows.reshape(count, 64, frames // 64).mean(axis=2, dtype="f8")
    norm = np.maximum(np.linalg.norm(pooled, axis=1), 1e-20)
    features = np.asarray(pooled / norm[:, None], dtype="f4")

    active = np.flatnonzero(norm > 1e-7)
    available = active[candidates[active] >= 2 * rate]
    queries = np.unique(available[np.linspace(0, len(available) - 1, min(queries, len(available)), dtype=int)])
    compressor = zstd.ZstdCompressor(level=level)
    total = selected = 0
    accepted = []
    similarity = []
    exact_count = 0
    for query in queries:
        start = candidates[query]
        limit = np.searchsorted(candidates, start - rate, side="right")
        scores = features[:limit] @ features[query]
        best = np.argpartition(np.abs(scores), -min(4, limit))[-min(4, limit):]
        src = lane[start:start + frames]
        previous = residual(src)
        standalone = min(len(compressor.compress(bit_planes(x.reshape(1, -1)))) for x in (src, previous))
        possible = standalone
        for match in best:
            origin = candidates[match]
            # Search sample phase locally after the coarse whole-file feature match.
            offsets = np.arange(max(0, origin - 32), min(origin + 32, len(lane) - frames) + 1)
            x = samples[start:start + frames].astype("f8")
            targets = np.lib.stride_tricks.sliding_window_view(samples[offsets[0]:offsets[-1] + frames], frames)
            correlations = (targets @ x) / np.maximum(np.linalg.norm(targets, axis=1)
                                                        * np.linalg.norm(x), 1e-20)
            best_phase = int(np.argmax(np.abs(correlations)))
            target = lane[offsets[best_phase]:offsets[best_phase] + frames]
            if not len(similarity) or similarity[-1][0] != start:
                similarity.append([int(start), 0.0])
            similarity[-1][1] = max(similarity[-1][1], abs(float(correlations[best_phase])))
            if correlations[best_phase] < 0:
                target = target ^ np.uint32(0x80000000)
            difference = src ^ target
            if not np.any(difference):
                cost = 9
                exact_count += 1
            else:
                cost = 9 + min(len(compressor.compress(bit_planes(x.reshape(1, -1))))
                               for x in (difference, previous ^ residual(target)))
            possible = min(possible, cost)
        if possible < standalone:
            accepted.append(int(start))
        total += standalone
        selected += possible

    return {
        "channel": channel, "queries": len(queries), "best_p50_correlation":
            float(np.median([x[1] for x in similarity])) if similarity else None,
        "standalone_bytes": total, "selected_bytes": selected,
        "matched_windows": len(accepted), "exact_candidates": exact_count,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pcm")
    parser.add_argument("rate", type=int)
    parser.add_argument("channels", type=int)
    parser.add_argument("--channel", type=int, action="append")
    parser.add_argument("--frames", type=int, default=2048)
    parser.add_argument("--queries", type=int, default=32)
    args = parser.parse_args()
    if args.frames < 64 or args.frames % 64 or args.queries < 1:
        parser.error("Frames must be a positive multiple of 64 and queries must be positive")
    pcm = np.memmap(args.pcm, mode="r", dtype="<u4").reshape(-1, args.channels)
    channels = args.channel if args.channel is not None else list(range(args.channels))
    results = [estimate(pcm, channel, args.rate, args.frames, args.queries, 5) for channel in channels]
    print(json.dumps({
        "window_frames": args.frames, "search_hop": args.frames // 4,
        "minimum_reference_frames": args.rate, "results": results,
        "saved_fraction": 1 - sum(row["selected_bytes"] for row in results)
        / max(1, sum(row["standalone_bytes"] for row in results)),
    }, indent=2))


if __name__ == "__main__":
    main()

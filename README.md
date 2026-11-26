# JEMA

**Jointly Encoded Multitrack Audio**

JEMA is an experimental lossless multitrack audio format (`.jema`) focused on joint compression of floating-point PCM and low-latency random access.

## Overview

JEMA treats time-aligned audio tracks as a unified dataset, exploring correlations across time, channels, and tracks to reduce the storage overhead of multitrack audio.

Rather than compressing each track independently, JEMA investigates the compression efficiency of joint encoding while maintaining efficient random access.

## Features

| Property | Description |
| --- | --- |
| Sample format | IEEE 754 Float32 PCM |
| Compression | Joint multitrack compression |
| Losslessness | Bit-exact |
| Random access | Sample-accurate |
| Track count | Variable |
| Channel count | Variable |
| Sample rate | Variable |
| Metadata | Optional ID3v2 and application-specific data |
| File extension | `.jema` |

## Design

### Lossless Compression

JEMA operates on the original binary representation of Float32 PCM, preserving sample precision, channel layout, and temporal alignment across all tracks.

The encoder fits a reversible predictor per channel, can couple channel residuals within a track, and stores the residual bit planes across tracks in a shared entropy stream. Predictor changes inside a block follow local audio structure without changing its seek boundary.

### Random Access

JEMA is designed around time-range-based random access, allowing synchronized decoding of multiple tracks at arbitrary sample positions without sequentially decoding from the beginning of the audio.

This design accommodates playback resumption, seeking, interactive mixing, and other applications that require frequent access to localized audio data.

### Encoding and Decoding

JEMA follows an asymmetric coding design, prioritizing compression efficiency during encoding and low latency and resource efficiency during decoding.

Encoded audio is self-contained. Decoding does not depend on the original mix, source separation models, or external reference data.

### Metadata

JEMA's metadata design accommodates complete ID3v2 tags and application-specific data as optional components.

Optional metadata is stored on demand without reserved padding by default. Metadata access is independent of audio encoding and decoding.

## Specification

The logical structure of a JEMA file consists of:

- Format identification
- PCM properties
- Track and channel layout
- Codec parameters
- Random access index
- Integrity information
- Optional metadata
- Encoded audio data

The [experimental format specification](SPECIFICATION.md) defines the binary layout and decoding rules. [Reversible coding](ALGORITHM.md) describes the predictors, joint coding, and bounded references.

## Implementation

The .NET 8 reference library provides encoding, frame-range decoding, and independent metadata access. It accepts interleaved little-endian Float32 PCM and preserves every sample bit pattern. The [developer guide](DEV.md) covers the library interface and build.

## Usage

The command-line tool accepts interleaved raw PCM. The channel list declares one channel count per track. This example encodes two stereo tracks and one mono track at 44.1 kHz, then restores 2048 synchronized frames starting at frame 44100.

```sh
dotnet run --project src/Jema.Tool -c Release -- encode stems.f32le stems.jema 44100 2,2,1
dotnet run --project src/Jema.Tool -c Release -- decode stems.jema region.f32le 44100 2048
```

The encoder accepts time-aligned floating-point PCM data. The decoder accesses corresponding multitrack samples through a starting sample frame and a requested frame count.

The reconstructed PCM data can be used for audio playback, interactive mixing, and other audio processing operations.

## Benchmarks

Performance evaluation covers the following metrics:

- Compression ratio
- Encoding throughput
- Decoding throughput
- Random access latency
- CPU and memory usage
- Bit-exact reconstruction

Evaluation focuses on the compression gains of joint multitrack encoding over independent track encoding, as well as the relationship between compression efficiency and decoding performance.

The [benchmark report](BENCHMARKS.md) records measured size, bit-exact reconstruction, and access performance. [Research notes](RESEARCH.md) document individual coding hypotheses and their evidence.

## Scope

JEMA focuses exclusively on the encoding, storage, and reconstruction of floating-point audio data. Source separation, audio playback, mixing, audio editing, and format conversion are outside its scope.

JEMA is independent of any particular application, audio source, or source separation model.

## Status

**Experimental research.**

JEMA is currently in an early research stage. The compression algorithm, binary format specification, and codec interfaces remain under development. Compatibility between development versions is not guaranteed.

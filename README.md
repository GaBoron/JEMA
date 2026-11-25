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

Joint encoding allows coding dependencies between tracks and channels, rather than treating each audio stream as an independent compression unit.

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

The binary format specification and coding rules are currently under research.

## Implementation

The JEMA reference implementation is intended to provide a standalone codec library for encoding, decoding, random access, and metadata operations on `.jema` files.

## Usage

The conceptual data flow of JEMA:

```
Multitrack Float32 PCM
          |
       Encoder
          |
      JEMA File
          |
       Decoder
          |
Multitrack Float32 PCM
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

Benchmark results are not yet available.

## Scope

JEMA focuses exclusively on the encoding, storage, and reconstruction of floating-point audio data. Source separation, audio playback, mixing, audio editing, and format conversion are outside its scope.

JEMA is independent of any particular application, audio source, or source separation model.

## Status

**Experimental research.**

JEMA is currently in an early research stage. The compression algorithm, binary format specification, and codec interfaces remain under development. Compatibility between development versions is not guaranteed.

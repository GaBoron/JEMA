# JEMA

**Jointly Encoded Multitrack Audio**

JEMA stores synchronized Float32 audio tracks in a single lossless `.jema` file. It preserves every sample bit and can decode a requested range of frames across all tracks.

## Get started

Download and extract the [JEMA v1.0.0 CLI archive](https://github.com/GaBoron/JEMA/releases/tag/v1.0.0). The CLI runs with the .NET 8 runtime. Input is little-endian Float32 PCM, interleaved by frame and ordered by track and channel within each frame.

```sh
dotnet Jema.Tool.dll encode stems.f32le stems.jema 44100 2,2,1
dotnet Jema.Tool.dll verify stems.jema stems.f32le
dotnet Jema.Tool.dll decode stems.jema region.f32le 44100 2048
```

The example encodes two stereo tracks and one mono track at 44.1 kHz. The last command retrieves 2048 synchronized frames starting at frame 44100. Run `dotnet Jema.Tool.dll` for inspection, metadata, and encoding options.

## Format

- Each track can have its own channel count. All tracks share one sample rate and frame count.
- An indexed block contains a time range across all tracks. Local prediction and joint entropy coding reduce storage, and exact block references have bounded decode cost.
- A header CRC, index CRCs, and per-block PCM CRCs detect accidental corruption.
- Optional ID3v2.3, ID3v2.4, and application data can be read or rewritten separately from the audio.
- The `.jema` file contains everything needed to reconstruct its audio.

JEMA encodes and reconstructs PCM. Upstream source separation, playback, mixing, and editing are handled by applications.

## Performance

On six full-length recordings with six stereo tracks each, JEMA 1.0.0 reduced storage from **2.79 GB of raw PCM to 1.70 GB**, saving **39.18%**. In a three-recording high-compression test on the benchmark machine, encoding took **22.7–24.2 seconds per audio minute**, and two-second unbuffered reads had a P99 of **43.2–48.2 ms**. [Benchmark methods and raw PCM results](BENCHMARKS.md)

## Documentation

- [Format specification](SPECIFICATION.md) defines revision 1 of the binary format.
- [Reversible coding](ALGORITHM.md) explains prediction, joint coding, and exact reconstruction.
- [Developer guide](DEV.md) covers source builds and the .NET library API.
- [Research notes](RESEARCH.md) describe relevant PCM characteristics and block reuse.
- [Third-party notices](THIRD-PARTY-NOTICES.md) lists the distributed dependencies.

# Benchmarks

The six full-length six-stereo-track inputs total 2,789,107,248 bytes of Float32 PCM. JEMA stores them in 1,900,243,388 bytes, a 31.87% reduction. On the three full-length files compared with lossless WavPack, WavPack uses less space. Joint JEMA is smaller than independent-track JEMA for those three files and the four-track DSP recording. The independent encodes are smaller for the second separator's two-track excerpt.

## Corpus

| ID | Material | Rate | Channels per track |
| --- | --- | ---: | --- |
| stem-01 through stem-06 | Six complete songs with electronic, pop, acoustic, and live arrangements, separated by BS-RoFormer SW Fixed | 44100 | 2,2,2,2,2,2 |
| uvr-two | 45-second excerpt from a song in the same library, separated by UVR-MDX-NET-Inst_HQ_3 into vocals and instrumental | 44100 | 2,2 |
| album-raw | Bass, acoustic guitar right, viola, and outro vocal from AlbumDB's *It Ain't Easy (To Mend A Heart)*, resampled from the studio WAVs | 48000 | 1,1,1,1 |
| album-dsp | The same studio tracks after a two-tap Float32 filter | 48000 | 1,1,1,1 |
| recorded-float | Calibrated binaural recording `stimuli/indoor_multitone.wav` from the Chalmers vehicle-noise study | 48000 | 2 |

For the six-stem input, FFmpeg decodes each original to stereo `f32le` at 44.1 kHz. The upstream separator uses checkpoint revision `a443a2985534b3bc815ef54a5d446c6a0390f974`, CUDA Float16 inference, and its ordinary overlap setting. Its output order is vocals, drums, bass, guitar, piano, and other. The second separator takes seconds 10–55 from `stem-02`'s mix, uses WAV FLOAT input and output, and interleaves vocals before instrumental. Private song audio and metadata remain outside the repository. [Separation worker](https://github.com/GaBoron/SPW-Track-Mix/blob/5f18418/src/main/resources/trackmix/separate.py) · [Audio Separator](https://github.com/nomadkaraoke/python-audio-separator)

For AlbumDB, the inputs are `bass.wav`, `guitar_acoustic_r.wav`, `viola.wav`, and `vox_outro.wav` in `stems_raw/10 It Ain't Easy (To Mend A Heart)`. Decode each with `ffmpeg -i INPUT.wav -ar 48000 -ac 1 -f f32le OUTPUT.f32le`, truncate to the shortest lane, and interleave in that order. The DSP case computes `f32(f32(0.7937*x[t]) - f32(0.1731*x[t-1]))` per lane with a zero initial predecessor and Float32 coefficients. The Chalmers WAV already contains Float32 PCM. Extract it with `ffmpeg -i INPUT.wav -map 0:a:0 -c:a copy -f f32le OUTPUT.f32le`. It provides recorded and calibrated Float32 data. The device-native capture representation was outside this measurement. [AlbumDB](https://zenodo.org/records/19683001) · [Chalmers dataset](https://zenodo.org/records/16901844)

## Compression and bit-exactness

The primary results use 8192 frames per block and Zstandard level 5. JEMA sizes include the file header, track layout, complete block payloads, and index. Metadata is absent. A trailer contributes 24 bytes plus its payload bytes when present. Whole-input verification compared 3,318,095,248 decoded bytes directly with the encoder inputs. Separately generated format vectors covered every stored mode, both fixed-point side representations, partial blocks, special Float32 values, metadata isolation, corruption, reference depth, and sparse offsets above 4 GiB.

| ID | Raw bytes | JEMA bytes | JEMA/raw | Bits/sample |
| --- | ---: | ---: | ---: | ---: |
| stem-01 | 328,059,936 | 226,174,323 | 68.94% | 22.06 |
| stem-02 | 254,422,464 | 169,040,384 | 66.44% | 21.26 |
| stem-03 | 473,005,344 | 337,520,460 | 71.36% | 22.83 |
| stem-04 | 679,076,688 | 471,467,955 | 69.43% | 22.22 |
| stem-05 | 573,688,752 | 375,894,711 | 65.52% | 20.97 |
| stem-06 | 480,854,064 | 320,145,555 | 66.58% | 21.31 |
| uvr-two | 31,752,000 | 21,177,993 | 66.70% | 21.34 |
| album-raw | 184,405,216 | 74,510,643 | 40.41% | 12.93 |
| album-dsp | 184,405,216 | 75,773,172 | 41.09% | 13.15 |
| recorded-float | 128,425,568 | 97,283,720 | 75.75% | 24.24 |

The six-stem runs encoded at 8.6–14.3 MiB/s and took 16.9–47.8 seconds per file. The full comparison pass, which reads the original and compares byte spans, sustained 239–314 MiB/s. Some encoding jobs overlapped other corpus work. The single-process access run below isolates playback-path costs more closely.

### Codec baselines

Independent JEMA uses one archive per original track with the same block size and codec options. WavPack 5.9.0 uses its lossless very-high preset, a requested 8192-frame block size, and the original interleaved Float32 PCM. The Zstandard and Pcodec sizes are the measured independent 8192-frame payloads with modeled overhead for an equivalent 24-byte per-block index and common header. Pcodec level 8 encodes one typed lane per substream with a four-byte substream size. All comparators were checked for exact sample bits. [WavPack usage](https://www.wavpack.com/wavpack_doc.html) · [Pcodec standalone API](https://pcodec.readthedocs.io/en/latest/standalone.html)

| ID | Zstd | Byte shuffle + Zstd | Bit shuffle + Zstd | Pcodec | WavPack | JEMA joint | JEMA independent |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| stem-01 | 312,464,279 | 259,364,095 | 256,912,825 | 244,633,564 | 208,933,212 | 226,174,323 | 227,751,453 |
| stem-03 | 449,736,040 | 375,941,409 | 378,721,494 | 361,846,078 | 317,424,450 | 337,520,460 | 338,831,124 |
| stem-06 | 455,151,294 | 379,385,553 | 380,733,931 | 348,175,585 | 300,344,856 | 320,145,555 | 321,138,178 |

Joint JEMA saves 0.69%, 0.39%, and 0.31% over independent JEMA in the three rows. On `album-dsp` it saves 0.27% relative to the independent total of 75,978,816 bytes. On `uvr-two` it adds 0.75% against the independent total of 21,021,367 bytes. Independent layout and settings are held fixed. The result combines shared index bytes and the net coding effect. [Joint modeling analysis](RESEARCH.md#joint-modeling)

### Block size

The default 8192-frame geometry prioritizes storage while covering the measured buffered access window. The `stem-01` sweep quantifies the tradeoff.

| Block frames | File bytes | Ratio | Warm 512-frame P99 | Warm 88200-frame P99 |
| ---: | ---: | ---: | ---: | ---: |
| 2048 | 228,431,994 | 69.63% | 0.62 ms | 13.87 ms |
| 8192 | 226,174,323 | 68.94% | 2.29 ms | 15.61 ms |
| 32768 | 225,286,325 | 68.67% | 7.52 ms | 19.43 ms |

## Random access and resource use

Measurements use `stem-01` on Windows build 26300, Intel Core i9-14900HX, 32 GiB RAM, UMIS RPJYJ1T24MLR1HWY NVMe SSD, and .NET 8.0.34 x64. The separation environment uses FFmpeg `n8.1.3-18-g46d899d7a0`, Audio Separator 0.48.0, Torch 2.10.0+cu128, and an RTX 4060 Laptop GPU. Compression and playback-path measurements use the CPU. Baseline tools use Python 3.13, NumPy 2.4.6, python-zstandard 0.25.0, Pcodec 1.1.0, and WavPack 5.9.0.

The warm test reads the complete file first. The unbuffered test uses Windows `FILE_FLAG_NO_BUFFERING` with 4096-byte alignment to bypass the OS data cache. SSD controller and filesystem metadata caches can remain warm. The raw comparator reads the same requested frame ranges into a byte span. The JEMA run includes index lookup, decompression, inverse prediction, PCM CRC, and copying partial blocks. Random rows contain 1000 uniformly sampled starts per window. Open-and-first rows contain 128 fresh opens followed by the first second of PCM. Codec and OS paths receive an untimed initial call. [Windows file buffering](https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering)

| I/O regime | Source | Window | P50 ms | P95 ms | P99 ms | Max ms |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| Warm | Raw | 1 frame | 0.003 | 0.007 | 0.009 | 0.017 |
| Warm | JEMA | 1 frame | 1.102 | 1.548 | 2.155 | 5.398 |
| Warm | Raw | 512 frames | 0.007 | 0.017 | 0.025 | 0.043 |
| Warm | JEMA | 512 frames | 1.042 | 1.897 | 2.292 | 3.234 |
| Warm | Raw | 14700 frames | 0.127 | 0.178 | 0.216 | 0.272 |
| Warm | JEMA | 14700 frames | 2.842 | 4.061 | 5.874 | 7.157 |
| Warm | Raw | 88200 frames | 0.746 | 1.045 | 1.250 | 1.532 |
| Warm | JEMA | 88200 frames | 11.386 | 13.129 | 15.611 | 23.640 |
| Unbuffered | Raw | 512 frames | 0.130 | 0.190 | 0.247 | 0.300 |
| Unbuffered | JEMA | 512 frames | 1.228 | 2.364 | 2.590 | 2.979 |
| Unbuffered | Raw | 14700 frames | 0.400 | 0.522 | 0.600 | 0.751 |
| Unbuffered | JEMA | 14700 frames | 3.729 | 5.393 | 6.873 | 11.160 |
| Unbuffered | Raw | 88200 frames | 1.522 | 1.781 | 1.911 | 2.066 |
| Unbuffered | JEMA | 88200 frames | 15.107 | 17.208 | 19.875 | 26.010 |
| Warm | Raw | Open and first 44100 frames | 0.232 | 0.326 | 0.472 | 0.524 |
| Warm | JEMA | Open and first 44100 frames | 4.005 | 4.630 | 5.029 | 5.360 |
| Unbuffered | Raw | Open and first 44100 frames | 1.344 | 1.528 | 1.600 | 1.611 |
| Unbuffered | JEMA | Open and first 44100 frames | 7.042 | 9.195 | 12.137 | 12.455 |

Warm open-only P99 was 0.070 ms for raw and 0.247 ms for JEMA. Unbuffered open-only P99 was 0.092 ms and 0.455 ms. Sequential warm reading sustained 5809 MiB/s for raw and 405 MiB/s for JEMA. Unbuffered throughput was 1409 and 284 MiB/s respectively. Warm sequential CPU time was 63 ms for raw and 766 ms for JEMA. Unbuffered CPU time was 47 ms and 906 ms. Whole benchmark-process peak working sets were 42–44 MiB for raw and 61–64 MiB for default JEMA. These include runtime, caller buffers, and scratch. Decoder storage scales with the block size and caller's request span.

| Random request | JEMA logical bytes read / requested | JEMA decoded / requested | JEMA unbuffered issued / requested |
| ---: | ---: | ---: | ---: |
| 1 frame | 5636.63× | 8192.00× | 5807.53× |
| 512 frames | 11.74× | 17.06× | 12.09× |
| 14700 frames | 1.07× | 1.55× | 1.10× |
| 88200 frames | 0.75× | 1.09× | 0.77× |

The reference playback workload reads all six stereo tracks together. Its present prefetch code uses up to two seconds of PCM, targets one-third of a second ahead, and retains an opening segment. The raw baseline measures file reads alone. The worst two-second JEMA read in the measured unbuffered run used 26.0 ms to supply 2 seconds of PCM. This supports a background-prefetch design on this machine. Host seek response, concurrent-load tails, and audio callback timing remain unmeasured. [Prefetch implementation](https://github.com/GaBoron/SPW-Track-Mix/blob/5f18418/src/main/java/dev/gaboron/spwtrackmix/BassSourceDsp.java#L188-L210)

### WavPack seek comparison

The WavPack library benchmark uses the public seek and unpack functions with Float32 normalization disabled. The same file, warm cache preparation, window sizes, and request counts are used for both WavPack presets. The Python and .NET harnesses use different deterministic samples from the same uniform start distribution. WavPack output words were compared with the original after every measured read. [WavPack library API](https://www.wavpack.com/WavPack5LibraryDoc.pdf)

| Codec | File bytes | 1-frame P99 | 512-frame P99 | 14700-frame P99 | 88200-frame P99 | Open and first P99 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| WavPack normal | 213,025,036 | 3.276 ms | 3.644 ms | 9.587 ms | 40.227 ms | 17.976 ms |
| WavPack very-high | 208,933,212 | 5.014 ms | 5.107 ms | 15.031 ms | 54.514 ms | 26.675 ms |
| JEMA 8192 frames | 226,174,323 | 2.155 ms | 2.292 ms | 5.874 ms | 15.611 ms | 5.029 ms |

WavPack's normal preset is smaller and faster to encode than JEMA on this input. JEMA paid less work in the measured warm seek workload. The result applies to the tested multichannel mode and presets.

## Reproduction

```sh
dotnet build src/Jema.Tool -c Release
dotnet build research/Bench -c Release
dotnet src/Jema.Tool/bin/Release/net8.0/Jema.Tool.dll encode stems.f32le stems.jema 44100 2,2,2,2,2,2
dotnet src/Jema.Tool/bin/Release/net8.0/Jema.Tool.dll verify stems.jema stems.f32le
dotnet research/Bench/bin/Release/net8.0/Bench.dll raw stems.f32le stems.jema 1000 0
dotnet research/Bench/bin/Release/net8.0/Bench.dll jema stems.f32le stems.jema 1000 0
dotnet research/Bench/bin/Release/net8.0/Bench.dll jema stems.f32le stems.jema 1000 4096
python research/compare.py stems.f32le 44100 2,2,2,2,2,2 comparison --wavpack-dir WAVPACK_DIR --tool src/Jema.Tool/bin/Release/net8.0/Jema.Tool.dll
python research/wavpack_access.py WAVPACK_DLL comparison/whole.wv stems.f32le
```

The Python tools require NumPy and python-zstandard. The block comparator also requires Pcodec. The comparison creates a new output directory and can contain several copies of the input size. Windows WavPack binaries are available from the [WavPack release](https://github.com/dbry/WavPack/releases/tag/5.9.0).

The current corpus covers two separator architectures, studio stems, a calibrated Float32 recording, and synthetic exact-loop and high-entropy controls. Its scope is limited to the measured tracks and the Windows reference machine. Hardware-native capture provenance, other operating systems, concurrent playback, and external application integration remain outside the measured scope.

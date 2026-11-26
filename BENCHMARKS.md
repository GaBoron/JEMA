# JEMA v1 benchmarks

JEMA 1.0.0 reduced the six full-length six-stereo-track inputs from 2,789,107,248 bytes of uncompressed Float32 PCM to 1,696,471,794 bytes, saving **39.18%**. All reported JEMA encodes recovered the original PCM byte for byte.

## Storage

The default encoder uses 8192-frame blocks, Zstandard level 5, and adaptive predictors up to order 16. Raw sizes are the interleaved PCM byte counts. JEMA sizes include the complete file header, block data, and index. The benchmark files have no optional metadata payloads.

| Input | Raw PCM bytes | JEMA 1.0.0 bytes | Space saved |
| --- | ---: | ---: | ---: |
| stem-01 | 328,059,936 | 202,432,688 | 38.29% |
| stem-02 | 254,422,464 | 148,488,371 | 41.64% |
| stem-03 | 473,005,344 | 310,032,759 | 34.45% |
| stem-04 | 679,076,688 | 426,578,039 | 37.18% |
| stem-05 | 573,688,752 | 325,433,694 | 43.27% |
| stem-06 | 480,854,064 | 283,506,243 | 41.04% |
| **Six-track total** | **2,789,107,248** | **1,696,471,794** | **39.18%** |
| uvr-two | 31,752,000 | 20,344,098 | 35.93% |
| album-raw | 184,405,216 | 72,591,328 | 60.63% |
| album-dsp | 184,405,216 | 73,812,565 | 59.97% |
| recorded-float | 128,425,568 | 93,479,213 | 27.21% |

## Playback workload

Measurements use Windows build 26300, Intel Core i9-14900HX, 32 GiB RAM, an NVMe SSD, and .NET 8.0.34 x64. The benchmark reads all six stereo tracks from stem-01 in 88200-frame windows, covering two seconds of audio at 44.1 kHz. JEMA timing includes index lookup, decompression, inverse prediction, and block integrity validation.

The warm runs read the complete file first. Unbuffered runs bypass the Windows file data cache with 4096-byte alignment. Each raw PCM row has 1000 requests and each JEMA row has 500 requests, using the same seed and sampling method. These are offline read timings for the measured machine. Host playback, concurrent application load, and other operating systems remain outside the measurements.

| I/O regime | Format | P50 | P95 | P99 | Maximum |
| --- | --- | ---: | ---: | ---: | ---: |
| Warm | Raw PCM | 0.75 ms | 1.05 ms | 1.25 ms | 1.53 ms |
| Warm | JEMA 1.0.0 | 30.29 ms | 35.52 ms | 42.91 ms | 66.86 ms |
| Unbuffered | Raw PCM | 1.52 ms | 1.78 ms | 1.91 ms | 2.07 ms |
| Unbuffered | JEMA 1.0.0 | 36.36 ms | 42.01 ms | 50.04 ms | 60.69 ms |

The application workload reads two seconds of synchronized PCM and maintains about one-third of a second of lookahead. The local screening threshold was 80 ms P99. [Playback prefetch reference](https://github.com/GaBoron/SPW-Track-Mix/blob/5f18418/src/main/java/dev/gaboron/spwtrackmix/BassSourceDsp.java#L188-L210)

## Encoding time budget

Default single-worker encoding took 16.14–18.26 seconds per minute of audio across the six full-length sources. The high-compression measurements use 16384-frame blocks, Zstandard level 13, deep prediction, and four workers. The timed command covers process startup, PCM reads, compression, and archive writes. The separate verification runs take place after timing.

| Input | Raw PCM bytes | JEMA 1.0.0 bytes | Audio duration | Encode time | Seconds per audio minute | Unbuffered two-second read P99 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| stem-03 | 473,005,344 | 308,377,931 | 223.45 s | 88.91 s | 23.87 | 48.20 ms |
| stem-04 | 679,076,688 | 424,239,301 | 320.80 s | 129.52 s | 24.22 | 43.24 ms |
| stem-06 | 480,854,064 | 280,484,108 | 227.16 s | 85.94 s | 22.70 | 46.91 ms |

All three met the local targets of 60 seconds per minute to encode and 80 ms P99 for unbuffered reads. Wall time depends on CPU capacity, storage, and concurrent work.

## Corpus

| ID | Material | Rate | Channels per track |
| --- | --- | ---: | --- |
| stem-01 through stem-06 | Six complete songs separated by BS-RoFormer SW Fixed | 44100 | 2,2,2,2,2,2 |
| uvr-two | A 45-second excerpt separated by UVR-MDX-NET-Inst_HQ_3 | 44100 | 2,2 |
| album-raw | Four resampled studio stems from AlbumDB | 48000 | 1,1,1,1 |
| album-dsp | The same studio stems with a two-tap Float32 filter | 48000 | 1,1,1,1 |
| recorded-float | Calibrated stereo field recording from a Chalmers study | 48000 | 2 |

The six-stem inputs use a 44.1 kHz Float32 stereo mix, checkpoint revision `a443a2985534b3bc815ef54a5d446c6a0390f974`, and CUDA Float16 inference with ordinary overlap. Interleave vocals, drums, bass, guitar, piano, and other in that order. The second separator uses seconds 10–55 of the stem-02 mix, with vocals before instrumental. [Separation worker](https://github.com/GaBoron/SPW-Track-Mix/blob/5f18418/src/main/resources/trackmix/separate.py)

The four-stem files use `bass.wav`, `guitar_acoustic_r.wav`, `viola.wav`, and `vox_outro.wav` from AlbumDB's *It Ain't Easy (To Mend A Heart)*, decoded to 48 kHz mono Float32 and truncated to the shortest lane. The DSP version applies `f32(f32(0.7937*x[t]) - f32(0.1731*x[t-1]))` with a zero initial predecessor. The field recording is `stimuli/indoor_multitone.wav`, extracted as Float32 PCM without conversion. [AlbumDB](https://zenodo.org/records/19683001) · [Chalmers dataset](https://zenodo.org/records/16901844)

## Reproduce

From the v1.0.0 source tree on Windows, supply a 44.1 kHz six-stereo-track interleaved Float32 input named `stems.f32le`.

```powershell
dotnet build src/Jema.Tool -c Release
dotnet build research/Bench -c Release
$tool = 'src/Jema.Tool/bin/Release/net8.0/Jema.Tool.dll'
$bench = 'research/Bench/bin/Release/net8.0/Bench.dll'
dotnet $tool encode stems.f32le stems.jema 44100 2,2,2,2,2,2
dotnet $tool verify stems.jema stems.f32le

dotnet $bench raw stems.f32le stems.jema 1000 0 88200
dotnet $bench jema stems.f32le stems.jema 500 0 88200
dotnet $bench raw stems.f32le stems.jema 1000 4096 88200
dotnet $bench jema stems.f32le stems.jema 500 4096 88200

$elapsed = Measure-Command {
    dotnet $tool encode stems.f32le compact.jema 44100 2,2,2,2,2,2 16384 13 --deep-prediction --workers=4
}
if ($LASTEXITCODE -ne 0) { throw 'Encoding failed.' }
$audioSeconds = (Get-Item stems.f32le).Length / (44100 * 48)
$elapsed.TotalSeconds * 60 / $audioSeconds
dotnet $tool verify compact.jema stems.f32le
dotnet $bench jema stems.f32le compact.jema 500 4096 88200
```

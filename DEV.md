# Developer guide

JEMA uses the .NET 8 SDK and runtime. The reference library accepts streams and byte spans. Its PCM contract is independent of the .NET type `float`, which lets callers preserve all bit patterns.

```sh
dotnet build src/Jema.Tool -c Release
dotnet build research/Bench -c Release
```

The tool project builds the `Jema` library automatically. The portable CLI archive includes the library and its managed dependencies. [Third-party notices](THIRD-PARTY-NOTICES.md)

The public compatibility surface comprises the [revision 1 format](SPECIFICATION.md), public types and members in the `Jema` assembly, and the documented CLI commands and options. The software version is set in `src/Directory.Build.props`.

## Library usage

```csharp
using Jema;

var layout = new PcmLayout(44100, 2, 2, 1);
using var pcm = File.OpenRead("stems.f32le");
if (pcm.Length % layout.BytesPerFrame != 0)
    throw new InvalidDataException("Incomplete PCM frame.");

using var file = File.Open("stems.jema", FileMode.CreateNew, FileAccess.ReadWrite);
JemaEncoder.Encode(pcm, file, layout, pcm.Length / layout.BytesPerFrame);

using var reader = new JemaReader(file);
int count = (int)Math.Min(512, reader.Frames);
var bytes = new byte[count * reader.Layout.BytesPerFrame];
DecodeWork work = reader.ReadFrames(0, bytes);
```

The caller controls stream ownership and must serialize access to each reader and its source. Use separate readers and streams for concurrent access. An encode destination starts empty at file offset zero. It needs read access when exact block references are enabled. Create output in a separate file before replacing stored audio to keep failures from altering an existing file. An encode consumes precisely the declared PCM range. A failed decode may have written part of its destination span.

`EncoderOptions.BlockFrames = 0` selects up to 8192 frames while respecting the decoded byte limit. A smaller block reduces work on very short ranges. `CompressionLevel` configures Zstandard and defaults to 5. Separate options control fixed-point prediction, adaptive linear prediction, cross-track sign prediction, and exact references. The decoder uses the mode and layout in the file.

`ReadMetadata` returns both optional payloads. `CopyWithMetadata` copies the audio extent and writes a replacement trailer to an empty destination. The source stays usable if the destination write fails.

`EncoderOptions.DeepPrediction` (`--deep-prediction`) adds an encoding candidate that can use 24 or 32 temporal coefficients per lane. It retains the standard candidates and chooses the shortest stored block. Higher-order blocks consume more decode CPU. The option is intended for offline encoding with a decode budget established on the target playback hardware.

`EncoderOptions.WorkerCount` (`--workers=N`) permits 1–8 blocks to be compressed concurrently. It defaults to one. For larger blocks, the encoder reduces concurrency so staged PCM and staged payloads each occupy at most 4 MiB. Each worker has its own codec state. Input, exact-reference selection, and output remain ordered, so the same settings and PCM produce the same archive across worker counts. Use [the encoding-time measurements](BENCHMARKS.md#encoding-time-budget) to compare profiles on the target machine.

## Project entry points

- `src/Jema` owns frame geometry, reversible transforms, payload selection, checked index access, and opaque metadata.
- `src/Jema.Tool` provides binary PCM input and output, inspection, range decoding, comparison, and metadata access.
- `research` contains the reproducible experiments and workload measurements. They use external PCM and write results to user-selected directories.

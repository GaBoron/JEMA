# Developer guide

JEMA uses the .NET 8 SDK and runtime. The reference library accepts streams and byte spans. Its PCM contract is independent of the .NET type `float`, which lets callers preserve all bit patterns.

```sh
dotnet build src/Jema.Tool -c Release
dotnet build research/Bench -c Release
```

The tool project builds the `Jema` library automatically. It uses System.IO.Hashing for CRC-32 and encoder-side hashing, and ZstdSharp.Port for Zstandard. Both packages declare the MIT license. [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing/8.0.0) · [ZstdSharp.Port](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8)

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

`EncoderOptions.BlockFrames = 0` selects up to 8192 frames while respecting the decoded byte limit. A smaller block reduces work on very short ranges. `CompressionLevel` configures Zstandard and the default is 5. Candidate switches let callers measure fixed-point prediction, cross-track sign prediction, and exact references independently. Decoding requires only the stored mode and layout. Benchmarks report window sizes because the seek tradeoff depends on the caller's buffering strategy.

`ReadMetadata` returns both optional payloads. `CopyWithMetadata` copies the audio extent and writes a replacement trailer to an empty destination. The source stays usable if the destination write fails.

## Project entry points

- `src/Jema` owns frame geometry, reversible transforms, payload selection, checked index access, and opaque metadata.
- `src/Jema.Tool` provides binary PCM input and output, inspection, range decoding, comparison, and metadata access.
- `research` contains the reproducible experiments and workload measurements. They use external PCM and write results to user-selected directories.

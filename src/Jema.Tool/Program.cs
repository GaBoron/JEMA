using System.Globalization;
using Jema;

try
{
    switch (args)
    {
        case ["encode", var source, var destination, var sampleRate, var tracks, .. var options]:
            Encode(source, destination, sampleRate, tracks, options);
            break;
        case ["decode", var source, var destination, .. var range] when range.Length <= 2:
            Decode(source, destination, range);
            break;
        case ["info", var source]:
            using (var input = File.OpenRead(source))
            using (var reader = new JemaReader(input))
                Console.WriteLine($"{reader.Layout.SampleRate} Hz, {reader.Frames} frames, "
                    + $"tracks [{string.Join(',', reader.Layout.ChannelsPerTrack.ToArray())}], "
                    + $"{reader.BlockFrames} frames/block, {reader.AudioBytes} audio bytes");
            break;
        case ["verify", var source, var original]:
            Verify(source, original);
            break;
        case ["tags", var source, var kind, var destination] when kind is "id3" or "app":
            using (var input = File.OpenRead(source))
            using (var reader = new JemaReader(input))
            {
                var metadata = reader.ReadMetadata();
                var data = kind == "id3" ? metadata.Id3 : metadata.Application;
                if (data is null)
                    throw new InvalidDataException("Requested metadata is absent.");
                using var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write);
                output.Write(data);
            }
            break;
        case ["retag", var source, var destination, var id3, var application]:
            using (var input = File.OpenRead(source))
            using (var reader = new JemaReader(input))
            using (var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write))
                reader.CopyWithMetadata(output, new JemaMetadata(Payload(id3), Payload(application)));
            break;
        default:
            Console.Error.WriteLine("""
                jema encode INPUT.f32le OUTPUT.jema RATE CHANNELS[,CHANNELS...] [BLOCK_FRAMES] [LEVEL] [--no-fixed] [--no-cross] [--no-references]
                jema decode INPUT.jema OUTPUT.f32le [START_FRAME [FRAME_COUNT]]
                jema info INPUT.jema
                jema verify INPUT.jema ORIGINAL.f32le
                jema tags INPUT.jema id3|app OUTPUT
                jema retag INPUT.jema OUTPUT.jema ID3_FILE|- APPLICATION_FILE|-
                """);
            return 2;
    }
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException
    or OverflowException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static byte[]? Payload(string path)
{
    if (path == "-")
        return null;
    using var input = File.OpenRead(path);
    if (input.Length > 32L * 1024 * 1024)
        throw new InvalidDataException("A metadata payload exceeds 32 MiB.");
    var bytes = new byte[(int)input.Length];
    input.ReadExactly(bytes);
    return bytes;
}

static void Encode(string source, string destination, string rate, string tracks, string[] options)
{
    var layout = new PcmLayout(int.Parse(rate, CultureInfo.InvariantCulture),
        tracks.Split(',').Select(value => ushort.Parse(value, CultureInfo.InvariantCulture)).ToArray());
    var settings = new EncoderOptions();
    int numeric = 0;
    foreach (string argument in options)
    {
        settings = argument switch
        {
            "--no-cross" => settings with { CrossTrackSignPrediction = false },
            "--no-fixed" => settings with { FixedPointPrediction = false },
            "--no-references" => settings with { ExactBlockReferences = false },
            _ when numeric++ == 0 => settings with { BlockFrames = int.Parse(argument, CultureInfo.InvariantCulture) },
            _ when numeric == 2 => settings with { CompressionLevel = int.Parse(argument, CultureInfo.InvariantCulture) },
            _ => throw new ArgumentException($"Unknown encoder argument {argument}.")
        };
    }
    using var input = File.OpenRead(source);
    if (input.Length % layout.BytesPerFrame != 0)
        throw new InvalidDataException("Input has an incomplete PCM frame.");
    using var output = File.Open(destination, FileMode.CreateNew, FileAccess.ReadWrite);
    JemaEncoder.Encode(input, output, layout, input.Length / layout.BytesPerFrame, settings);
}

static void Decode(string source, string destination, string[] range)
{
    using var input = File.OpenRead(source);
    using var reader = new JemaReader(input);
    long first = range.Length > 0 ? long.Parse(range[0], CultureInfo.InvariantCulture) : 0;
    if (first < 0 || first > reader.Frames)
        throw new ArgumentOutOfRangeException(nameof(range));
    long count = range.Length > 1 ? long.Parse(range[1], CultureInfo.InvariantCulture) : reader.Frames - first;
    if (count < 0 || count > reader.Frames - first)
        throw new ArgumentOutOfRangeException(nameof(range));

    using var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write);
    int width = reader.Layout.BytesPerFrame;
    var buffer = new byte[reader.BlockFrames * width];
    long end = first + count;
    while (first < end)
    {
        int frames = (int)Math.Min(end - first, reader.BlockFrames - first % reader.BlockFrames);
        var samples = buffer.AsSpan(0, frames * width);
        reader.ReadFrames(first, samples);
        output.Write(samples);
        first += frames;
    }
}

static void Verify(string source, string original)
{
    using var input = File.OpenRead(source);
    using var raw = File.OpenRead(original);
    using var reader = new JemaReader(input);
    int width = reader.Layout.BytesPerFrame;
    if (raw.Length != reader.Frames * width)
        throw new InvalidDataException("Original PCM length does not match the file layout.");

    var actual = new byte[reader.BlockFrames * width];
    var expected = new byte[actual.Length];
    for (long start = 0; start < reader.Frames; start += reader.BlockFrames)
    {
        int bytes = (int)Math.Min(reader.BlockFrames, reader.Frames - start) * width;
        raw.ReadExactly(expected.AsSpan(0, bytes));
        reader.ReadFrames(start, actual.AsSpan(0, bytes));
        if (!actual.AsSpan(0, bytes).SequenceEqual(expected.AsSpan(0, bytes)))
            throw new InvalidDataException($"PCM mismatch in block starting at frame {start}.");
    }
    Console.WriteLine($"Verified {raw.Length} PCM bytes.");
}

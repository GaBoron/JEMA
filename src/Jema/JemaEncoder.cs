using System.IO.Hashing;

namespace Jema;

public static class JemaEncoder
{
    /// <summary>Consumes the specified frame count and writes a complete file at offset zero.</summary>
    public static void Encode(Stream pcm, Stream destination, PcmLayout layout, long frames,
        EncoderOptions? options = null, JemaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new EncoderOptions();
        if (!pcm.CanRead || ReferenceEquals(pcm, destination))
            throw new ArgumentException("PCM requires a readable, separate stream.", nameof(pcm));
        RequireEmptyOutput(destination, options.ExactBlockReferences);
        MetadataTrailer.Validate(metadata);
        int maximumBlockFrames = Container.MaxBlockBytes / layout.BytesPerFrame;
        int blockFrames = options.BlockFrames == 0 ? Math.Min(8192, maximumBlockFrames) : options.BlockFrames;
        if (blockFrames <= 0 || blockFrames > maximumBlockFrames)
            throw new ArgumentOutOfRangeException(nameof(options), "A decoded block must be 1 frame to 4 MiB.");
        if (options.CompressionLevel is < 1 or > 19)
            throw new ArgumentOutOfRangeException(nameof(options), "Compression level must be 1–19.");
        if (frames < 0 || frames > long.MaxValue / layout.BytesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(frames));

        var container = new Container(layout, blockFrames, frames, 0);
        if (container.BlockCount > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(frames), "Too many blocks for the reference encoder.");
        if (frames * layout.BytesPerFrame > long.MaxValue - container.LayoutEnd - container.BlockCount * IndexEntry.Size)
            throw new ArgumentOutOfRangeException(nameof(frames), "File position would exceed the supported range.");

        using var codec = new BlockCodec(layout, container.BlockBytes, options.CompressionLevel);
        var buffer = new byte[container.BlockBytes];
        var index = new List<IndexEntry>();
        var anchors = options.ExactBlockReferences ? new Dictionary<ulong, int>() : null;
        var anchorPcm = anchors is null ? [] : new byte[container.BlockBytes];

        destination.Position = container.LayoutEnd;
        for (int i = 0; i < container.BlockCount; i++)
        {
            var samples = buffer.AsSpan(0, container.BytesInBlock(i));
            pcm.ReadExactly(samples);
            uint crc = Crc32.HashToUInt32(samples);
            long position = destination.Position;

            if (anchors is not null)
            {
                // Hashes nominate anchors. A byte comparison is the authority for exact reuse.
                ulong hash = XxHash64.HashToUInt64(samples);
                if (anchors.TryGetValue(hash, out int ordinal)
                    && index[ordinal].PcmCrc == crc && container.BytesInBlock(ordinal) == samples.Length)
                {
                    var anchor = anchorPcm.AsSpan(0, samples.Length);
                    codec.Decode(destination, index[ordinal], anchor);
                    destination.Position = position;
                    if (samples.SequenceEqual(anchor))
                    {
                        index.Add(new IndexEntry(ordinal, 0, crc, BlockCodec.Reference));
                        continue;
                    }
                }
                anchors.TryAdd(hash, i);
            }

            var (mode, length) = codec.Encode(samples, destination, options);
            index.Add(new IndexEntry(position, length, crc, mode));
        }

        container = container with { IndexOffset = destination.Position };
        for (int i = 0; i < index.Count; i++)
            index[i].Write(destination, i);
        MetadataTrailer.Write(destination, metadata);
        long end = destination.Position;
        container.Write(destination);
        destination.Position = end;
    }

    internal static void RequireEmptyOutput(Stream destination, bool readable = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanSeek || !destination.CanWrite || (readable && !destination.CanRead)
            || destination.Length != 0)
            throw new ArgumentException("Destination must be empty, writable, seekable and readable for references.",
                nameof(destination));
    }
}

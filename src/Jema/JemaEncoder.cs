using System.IO.Hashing;
using System.Runtime.ExceptionServices;

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
        if (options.WorkerCount is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(options), "Worker count must be 1–8.");
        if (frames < 0 || frames > long.MaxValue / layout.BytesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(frames));

        var container = new Container(layout, blockFrames, frames, 0);
        if (container.BlockCount > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(frames), "Too many blocks for the reference encoder.");
        if (frames * layout.BytesPerFrame > long.MaxValue - container.LayoutEnd - container.BlockCount * IndexEntry.Size)
            throw new ArgumentOutOfRangeException(nameof(frames), "File position would exceed the supported range.");

        var index = new List<IndexEntry>();
        destination.Position = container.LayoutEnd;
        EncodeBlocks(pcm, destination, container, index, options);

        container = container with { IndexOffset = destination.Position };
        for (int i = 0; i < index.Count; i++)
            index[i].Write(destination, i);
        MetadataTrailer.Write(destination, metadata);
        long end = destination.Position;
        container.Write(destination);
        destination.Position = end;
    }

    private static void EncodeBlocks(Stream pcm, Stream destination, Container container, List<IndexEntry> index,
        EncoderOptions options)
    {
        // Bound each set of queued PCM buffers and encoded payloads to one maximum-size block.
        int workerCount = (int)Math.Min(container.BlockCount,
            Math.Min(options.WorkerCount, Container.MaxBlockBytes / container.BlockBytes));
        var slots = new BlockSlot[workerCount];
        bool parallel = workerCount > 1;
        var anchors = options.ExactBlockReferences ? new Dictionary<ulong, int>() : null;
        var anchorBuffer = anchors is null ? [] : new byte[container.BlockBytes];
        try
        {
            for (int i = 0; i < slots.Length; i++)
                slots[i] = new BlockSlot(container, options, parallel);

            while (index.Count < container.BlockCount)
            {
                int start = index.Count;
                int count = (int)Math.Min(workerCount, container.BlockCount - start);
                for (int i = 0; i < count; i++)
                {
                    int ordinal = start + i;
                    var slot = slots[i];
                    slot.Read(pcm, container.BytesInBlock(ordinal));
                    if (anchors is not null)
                        slot.Anchor = FindAnchor(slot, ordinal, slots, index, anchors, destination, anchorBuffer);
                }

                if (parallel)
                    CompressBatch(slots, count, options);
                else if (slots[0].Anchor < 0)
                    slots[0].Compress(destination, options);

                for (int i = 0; i < count; i++)
                    slots[i].Append(destination, index, parallel);
            }
        }
        finally
        {
            foreach (var slot in slots)
                slot?.Dispose();
        }
    }

    private static int FindAnchor(BlockSlot slot, int ordinal, BlockSlot[] slots, List<IndexEntry> index,
        Dictionary<ulong, int> anchors, Stream destination, byte[] anchorBuffer)
    {
        // Hashes nominate anchors. A byte comparison is the authority for exact reuse.
        ulong hash = XxHash64.HashToUInt64(slot.Samples);
        if (!anchors.TryGetValue(hash, out int previous))
        {
            anchors.Add(hash, ordinal);
            return -1;
        }
        if (previous >= index.Count)
        {
            var queued = slots[previous - index.Count];
            return queued.Crc == slot.Crc && slot.Samples.SequenceEqual(queued.Samples) ? previous : -1;
        }

        var entry = index[previous];
        // An anchor from a completed batch precedes the last block and has the full block size.
        if (entry.PcmCrc != slot.Crc || anchorBuffer.Length != slot.Samples.Length)
            return -1;
        var samples = anchorBuffer.AsSpan(0, slot.Samples.Length);
        long position = destination.Position;
        slot.ReadAnchor(destination, entry, samples);
        destination.Position = position;
        return slot.Samples.SequenceEqual(samples) ? previous : -1;
    }

    private static void CompressBatch(BlockSlot[] slots, int count, EncoderOptions options)
    {
        try
        {
            Parallel.For(0, count, i =>
            {
                if (slots[i].Anchor < 0)
                    slots[i].Compress(null, options);
            });
        }
        catch (AggregateException errors) when (errors.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Throw(errors.InnerExceptions[0]);
        }
    }

    private sealed class BlockSlot(Container container, EncoderOptions options, bool parallel) : IDisposable
    {
        private readonly byte[] buffer = new byte[container.BlockBytes];
        private readonly MemoryStream? payload = parallel ? new MemoryStream(container.BlockBytes) : null;
        private readonly BlockCodec codec = new(container.Layout, container.BlockBytes, options.CompressionLevel);
        private int size;
        private byte mode;
        private int compressed;
        private long position;

        public uint Crc { get; private set; }
        public int Anchor { get; set; }
        public Span<byte> Samples => buffer.AsSpan(0, size);

        public void Read(Stream pcm, int count)
        {
            size = count;
            pcm.ReadExactly(Samples);
            Crc = Crc32.HashToUInt32(Samples);
            Anchor = -1;
        }

        public void ReadAnchor(Stream source, IndexEntry entry, Span<byte> pcm) => codec.Decode(source, entry, pcm);

        public void Compress(Stream? destination, EncoderOptions options)
        {
            if (destination is null)
            {
                payload!.SetLength(0);
                (mode, compressed) = codec.Encode(Samples, payload, options);
            }
            else
            {
                position = destination.Position;
                (mode, compressed) = codec.Encode(Samples, destination, options);
            }
        }

        public void Append(Stream destination, List<IndexEntry> index, bool parallel)
        {
            if (Anchor >= 0)
            {
                index.Add(new IndexEntry(Anchor, 0, Crc, BlockCodec.Reference));
                return;
            }
            if (parallel)
            {
                position = destination.Position;
                destination.Write(payload!.GetBuffer().AsSpan(0, compressed));
            }
            index.Add(new IndexEntry(position, compressed, Crc, mode));
        }

        public void Dispose()
        {
            codec.Dispose();
            payload?.Dispose();
        }
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

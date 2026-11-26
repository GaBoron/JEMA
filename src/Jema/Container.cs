using System.Buffers.Binary;
using System.IO.Hashing;

namespace Jema;

internal readonly record struct IndexEntry(long Position, int Length, uint PcmCrc, byte Mode)
{
    public const int Size = 24;

    public void Write(Stream stream, long ordinal)
    {
        Span<byte> record = stackalloc byte[Size + sizeof(long)];
        record.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(record, Position);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], PcmCrc);
        record[16] = Mode;
        BinaryPrimitives.WriteInt64LittleEndian(record[Size..], ordinal);
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..], Crc32.HashToUInt32(record));
        stream.Write(record[..Size]);
    }

    public static IndexEntry Read(Stream stream, long ordinal)
    {
        Span<byte> record = stackalloc byte[Size + sizeof(long)];
        stream.ReadExactly(record[..Size]);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
        record[20..Size].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(record[Size..], ordinal);
        if (checksum != Crc32.HashToUInt32(record) || record[17..20].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid block index record.");
        return new IndexEntry(
            BinaryPrimitives.ReadInt64LittleEndian(record),
            BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[12..]),
            record[16]);
    }
}

internal sealed record Container(PcmLayout Layout, int BlockFrames, long Frames, long IndexOffset)
{
    private static ReadOnlySpan<byte> Magic => "JEMA\r\n\u001a\n"u8;
    private const int HeaderSize = 48;
    public const int MaxBlockBytes = 4 * 1024 * 1024;

    public int LayoutEnd => HeaderSize + Layout.ChannelsPerTrack.Length * 2;
    public int BlockBytes => BlockFrames * Layout.BytesPerFrame;
    public long BlockCount => Frames / BlockFrames + (Frames % BlockFrames == 0 ? 0 : 1);
    public long AudioEnd => IndexOffset + BlockCount * IndexEntry.Size;

    public int BytesInBlock(long block) =>
        (int)Math.Min(BlockFrames, Frames - block * BlockFrames) * Layout.BytesPerFrame;

    public void Write(Stream destination)
    {
        Span<byte> header = stackalloc byte[LayoutEnd];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], Layout.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], BlockFrames);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], Frames);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], IndexOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[40..], (ushort)Layout.ChannelsPerTrack.Length);
        for (int i = 0; i < Layout.ChannelsPerTrack.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(header[(HeaderSize + i * 2)..], Layout.ChannelsPerTrack[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Crc32.HashToUInt32(header));
        destination.Position = 0;
        destination.Write(header);
    }

    public static Container Read(Stream source)
    {
        Span<byte> storage = stackalloc byte[HeaderSize + 256 * 2];
        source.Position = 0;
        source.ReadExactly(storage[..HeaderSize]);
        if (!storage[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(storage[8..]) != 1)
            throw new InvalidDataException("Unknown JEMA format or revision.");
        if (storage[10..12].IndexOfAnyExcept((byte)0) >= 0 || storage[42..HeaderSize].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Unsupported header flags.");

        int trackCount = BinaryPrimitives.ReadUInt16LittleEndian(storage[40..]);
        if (trackCount is < 1 or > 256)
            throw new InvalidDataException("Invalid track count.");
        var header = storage[..(HeaderSize + trackCount * 2)];
        source.ReadExactly(header[HeaderSize..]);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        header[12..16].Clear();
        if (checksum != Crc32.HashToUInt32(header))
            throw new InvalidDataException("Header checksum mismatch.");

        var tracks = new ushort[trackCount];
        for (int i = 0; i < tracks.Length; i++)
            tracks[i] = BinaryPrimitives.ReadUInt16LittleEndian(header[(HeaderSize + i * 2)..]);
        PcmLayout layout;
        try
        {
            layout = new PcmLayout(BinaryPrimitives.ReadInt32LittleEndian(header[16..]), tracks);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid PCM layout.", error);
        }
        int blockFrames = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        long frames = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        long index = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        if (blockFrames <= 0 || blockFrames > MaxBlockBytes / layout.BytesPerFrame
            || frames < 0 || frames > long.MaxValue / layout.BytesPerFrame
            || index < header.Length || index > source.Length)
            throw new InvalidDataException("Invalid frame geometry or index position.");

        var container = new Container(layout, blockFrames, frames, index);
        if (container.BlockCount > (source.Length - index) / IndexEntry.Size)
            throw new InvalidDataException("Truncated index.");
        return container;
    }

    public IndexEntry ReadEntry(Stream source, long ordinal)
    {
        source.Position = IndexOffset + ordinal * IndexEntry.Size;
        var entry = IndexEntry.Read(source, ordinal);
        if (entry.Mode == BlockCodec.Reference)
        {
            if (entry.Position < 0 || entry.Position >= ordinal || entry.Length != 0
                || BytesInBlock(entry.Position) != BytesInBlock(ordinal))
                throw new InvalidDataException("Invalid block reference.");
            return entry;
        }
        int pcmBytes = BytesInBlock(ordinal);
        if (!BlockCodec.IsStoredMode(entry.Mode) || entry.Position < LayoutEnd || entry.Position > IndexOffset
            || entry.Length <= 0 || entry.Length > IndexOffset - entry.Position
            || (entry.Mode == BlockCodec.Raw ? entry.Length != pcmBytes : entry.Length >= pcmBytes))
            throw new InvalidDataException("Invalid block payload bounds.");
        return entry;
    }
}
